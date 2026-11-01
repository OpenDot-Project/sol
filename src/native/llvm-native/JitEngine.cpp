// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "JitApi.h"
#include "JitEnvelope.h"
#include "JitUnwind.h"
#include "Session.h"
#include "Support.h"
#include "../Checked.h"
#pragma warning(push)
#pragma warning(disable: 4244 4702)
#include <llvm/Bitcode/BitcodeReader.h>
#include <llvm/ExecutionEngine/Orc/LLJIT.h>
#include <llvm/ExecutionEngine/Orc/OrcABISupport.h>
#include <llvm/ExecutionEngine/Orc/SelfExecutorProcessControl.h>
#include <llvm/Support/MemoryBuffer.h>
#pragma warning(pop)
#include <algorithm>
#include <array>
#include <atomic>
#include <cstring>
#include <cstdlib>
#include <future>
#include <limits>
#include <map>
#include <mutex>
#include <span>
#include <stdexcept>
#include <string>

namespace OpenDot::Sol::LlvmNative
{
namespace
{
using namespace llvm::orc;

template <class T> T Take(llvm::Expected<T> value)
{
    if (!value)
    {
        throw std::runtime_error(llvm::toString(value.takeError()));
    }
    return std::move(*value);
}

void Check(llvm::Error error)
{
    if (error)
    {
        throw std::runtime_error(llvm::toString(std::move(error)));
    }
}

struct Engine final
{
    SolJitOptions Options{};
    std::shared_ptr<JitCodeLifetime> Code = std::make_shared<JitCodeLifetime>();
    std::atomic<std::uint64_t> Compiled{0}, Callbacks{0}, Failures{0};
    std::mutex ErrorGate;
    std::array<char, 1024> Error{};
    // Destroy the compiler first on failed setup, while callbacks and registration state still exist.
    std::unique_ptr<LLLazyJIT> Compiler;

    void RecordError(std::string_view text) noexcept
    {
        Failures.fetch_add(1, std::memory_order_relaxed);
        try
        {
            const std::lock_guard guard(ErrorGate);
            if (Error.front() != '\0')
            {
                return;
            }
            const auto count = std::min(text.size(), Error.size() - 1);
            std::memcpy(Error.data(), text.data(), count);
            std::span{Error}.subspan(count, 1).front() = '\0';
        }
        catch (...)
        {
            std::abort();
        }
    }

    void RecordException(const std::exception &error) noexcept
    {
        try
        {
            RecordError(std::string_view(error.what()));
        }
        catch (...)
        {
            RecordError("Native compilation failed while reporting its error.");
        }
    }
};

class GcCallThrough final : public LazyCallThroughManager
{
    Engine &_engine;
    std::unique_ptr<TrampolinePool> _pool;

public:
    GcCallThrough(ExecutionSession &session, Engine &engine)
        : LazyCallThroughManager(session, ExecutorAddr(engine.Options.FailureThunk), nullptr), _engine(engine)
    {
    }

private:
    void Resolve(ExecutorAddr trampoline, TrampolinePool::NotifyLandingResolvedFunction notify) noexcept
    {
        bool entered = false;
        ExecutorAddr address(_engine.Options.FailureThunk);
        try
        {
            _engine.Options.EnterNative();
            entered = true;
            _engine.Callbacks.fetch_add(1, std::memory_order_relaxed);
            std::promise<ExecutorAddr> completion;
            auto future = completion.get_future();
            resolveTrampolineLandingAddress(trampoline, [&completion](ExecutorAddr resolved) {
                completion.set_value(resolved);
            });
            address = future.get();
        }
        catch (const std::exception &error)
        {
            _engine.RecordException(error);
        }
        catch (...)
        {
            _engine.RecordError("Unknown lazy compilation failure.");
        }
        // Both transitions run on the original calling thread, even if resolution used another worker.
        try
        {
            if (entered)
            {
                _engine.Options.LeaveNative();
            }
            notify(address);
        }
        catch (...)
        {
            std::abort();
        }
    }

public:
    static std::unique_ptr<GcCallThrough> Create(ExecutionSession &session, Engine &engine)
    {
        auto manager = std::make_unique<GcCallThrough>(session, engine);
        auto &target = *manager;
        manager->_pool = Take(LocalTrampolinePool<OrcX86_64_Win32>::Create(
            [&target](ExecutorAddr trampoline, TrampolinePool::NotifyLandingResolvedFunction notify) noexcept {
                target.Resolve(trampoline, std::move(notify));
            }));
        manager->setTrampolinePool(*manager->_pool);
        return manager;
    }
};

bool Matches(const llvm::Type &type, unsigned code)
{
    switch (code)
    {
    case 0:
        return type.isVoidTy();
    case 1:
        return type.isIntegerTy(1);
    case 2:
        return type.isIntegerTy(32);
    case 3:
        return type.isIntegerTy(64);
    case 4:
        return type.isPointerTy();
    case 5:
        return type.isFloatTy();
    case 6:
        return type.isDoubleTy();
    default:
        return false;
    }
}

void ValidateModule(const llvm::Module &module, std::span<const SolJitSymbol> symbols)
{
    Verify(module);
    if (module.getTargetTriple() != llvm::Triple("x86_64-pc-windows-msvc"))
    {
        throw std::invalid_argument("ORC bitcode target mismatch.");
    }
    const auto flag = [&](const char *name, std::uint64_t expected) {
        auto *metadata = llvm::dyn_cast_or_null<llvm::ConstantAsMetadata>(module.getModuleFlag(name));
        auto *value = metadata ? llvm::dyn_cast<llvm::ConstantInt>(metadata->getValue()) : nullptr;
        if (!value || value->getBitWidth() > 64 || value->getZExtValue() != expected)
        {
            throw std::invalid_argument("ORC bitcode compiler/runtime contract metadata mismatch.");
        }
    };
    flag("sol.runtime.contract", 0x534F4C0000000002ULL);
    flag("sol.fir.schema", 8);
    if (module.getNamedGlobal("llvm.global_ctors") || module.getNamedGlobal("llvm.global_dtors"))
    {
        throw std::invalid_argument("ORC program initialization must use explicit source initialization operations.");
    }
    std::map<std::string, const SolJitSymbol *, std::less<>> bindings;
    for (const auto &symbol : symbols)
    {
        if (!symbol.Name || !*symbol.Name || !symbol.Address || std::strlen(symbol.Name) > 4096
            || !bindings.emplace(symbol.Name, &symbol).second)
        {
            throw std::invalid_argument("Invalid or duplicate native JIT binding.");
        }
    }
    std::size_t count = 0;
    for (const auto &function : module)
    {
        if (!function.isDeclaration())
        {
            if (++count > 4096 || bindings.contains(function.getName().str()))
            {
                throw std::invalid_argument("Native function limit or reserved binding definition.");
            }
            continue;
        }
        if (function.isIntrinsic() || function.use_empty())
        {
            continue;
        }
        const auto position = bindings.find(function.getName().str());
        if (position == bindings.end())
        {
            throw std::invalid_argument("Unbound native runtime function.");
        }
        const auto signature = position->second->Prototype;
        const auto arguments = (signature >> 4) & 15;
        if (signature == std::numeric_limits<std::uint64_t>::max() || arguments > 14
            || function.isVarArg() || function.arg_size() != arguments
            || !Matches(*function.getReturnType(), CheckedInteger<unsigned>(signature & 15)))
        {
            throw std::invalid_argument("Native runtime function prototype mismatch.");
        }
        unsigned index = 0;
        for (const auto &parameter : function.args())
        {
            const auto code = CheckedInteger<unsigned>((signature >> (8 + 4 * index++)) & 15);
            if (!Matches(*parameter.getType(), code))
            {
                throw std::invalid_argument("Native runtime parameter prototype mismatch.");
            }
        }
    }
    for (const auto &global : module.globals())
    {
        if (!global.isDeclaration() || global.use_empty())
        {
            continue;
        }
        const auto position = bindings.find(global.getName().str());
        if (position == bindings.end() || position->second->Prototype != std::numeric_limits<std::uint64_t>::max())
        {
            throw std::invalid_argument("Unbound native runtime data.");
        }
    }
    const auto *entry = module.getFunction("wmain");
    if (!entry || entry->isDeclaration() || !entry->getReturnType()->isIntegerTy(32)
        || entry->arg_size() != 2 || !entry->getArg(0)->getType()->isIntegerTy(32)
        || !entry->getArg(1)->getType()->isPointerTy())
    {
        throw std::invalid_argument("ORC executable entry prototype mismatch.");
    }
}

std::unique_ptr<Engine> Open(std::span<const unsigned char> envelope,
                             std::span<const SolJitSymbol> symbols, SolJitOptions options)
{
    auto engine = std::make_unique<Engine>();
    engine->Options = options;
    const auto payload = ReadJitEnvelope(envelope, options.Flags);
    Initialize();
    auto context = std::make_unique<llvm::LLVMContext>();
    const void *data = payload.data();
    auto buffer = llvm::MemoryBuffer::getMemBufferCopy(
        llvm::StringRef(static_cast<const char *>(data), payload.size()), "sol-program.bc");
    auto module = Take(llvm::parseBitcodeFile(buffer->getMemBufferRef(), *context));
    ValidateModule(*module, symbols);
    auto process = Take(SelfExecutorProcessControl::Create());
    auto session = std::make_unique<ExecutionSession>(std::move(process));
    auto &state = *engine;
    session->setErrorReporter([&state](llvm::Error error) {
        state.RecordError(llvm::toString(std::move(error)));
    });
    auto callThrough = GcCallThrough::Create(*session, state);
    LLLazyJITBuilder builder;
    builder.setExecutionSession(std::move(session));
    builder.setLazyCallthroughManager(std::move(callThrough));
    builder.setLazyCompileFailureAddr(ExecutorAddr(options.FailureThunk));
    builder.setNumCompileThreads(0);
    builder.setPlatformSetUp(setUpInactivePlatform);
    builder.setObjectLinkingLayerCreator([&state](ExecutionSession &execution,
                                                  llvm::jitlink::JITLinkMemoryManager &memory)
        -> llvm::Expected<std::unique_ptr<ObjectLayer>> {
            auto layer = std::make_unique<ObjectLinkingLayer>(execution, memory);
            layer->addPlugin(CreateWindowsUnwindPlugin(state.Code));
            return std::unique_ptr<ObjectLayer>(std::move(layer));
        });
    engine->Compiler = Take(builder.create());
    engine->Compiler->setPartitionFunction(IRPartitionLayer::compileRequested);
    engine->Compiler->getIRTransformLayer().setTransform([&state](ThreadSafeModule input,
                                                                  MaterializationResponsibility &)
        -> llvm::Expected<ThreadSafeModule> {
            input.withModuleDo([&state](llvm::Module &partition) {
                for (const auto &function : partition)
                {
                    if (!function.isDeclaration())
                    {
                        state.Compiled.fetch_add(1, std::memory_order_relaxed);
                    }
                }
            });
            return input;
        });
    SymbolMap imports;
    for (const auto &symbol : symbols)
    {
        llvm::JITSymbolFlags flags(llvm::JITSymbolFlags::Exported);
        if (symbol.Prototype != std::numeric_limits<std::uint64_t>::max())
        {
            flags |= llvm::JITSymbolFlags::Callable;
        }
        imports[engine->Compiler->mangleAndIntern(symbol.Name)] =
            ExecutorSymbolDef(ExecutorAddr(symbol.Address), flags);
    }
    Check(engine->Compiler->getMainJITDylib().define(absoluteSymbols(std::move(imports))));
    Check(engine->Compiler->addLazyIRModule(ThreadSafeModule(std::move(module), std::move(context))));
    return engine;
}

struct Engines final
{
    std::mutex Gate;
    std::uint64_t Next = 1;
    std::map<std::uint64_t, std::unique_ptr<Engine>> Values;
};

Engines &Registry()
{
    // Code addresses may be retained by native callers until process termination, including CRT teardown.
    static auto &registry = *std::make_unique<Engines>().release();
    return registry;
}

Engine &GetEngine(std::uint64_t id)
{
    auto &registry = Registry();
    const std::lock_guard guard(registry.Gate);
    const auto position = registry.Values.find(id);
    if (position == registry.Values.end())
    {
        throw std::invalid_argument("Invalid native JIT engine handle.");
    }
    return *position->second;
}

template <class Action> bool Protect(Action action) noexcept
{
    try
    {
        action();
        ClearError();
        return true;
    }
    catch (const std::exception &error)
    {
        SetError(error);
    }
    catch (...)
    {
        SetError("Unknown native JIT failure.");
    }
    return false;
}
}
}

extern "C"
{
std::uint64_t SolJitProtocol() noexcept
{
    return 0x534F4C4A00000001ULL;
}

bool SolJitOpen(const unsigned char *envelope, std::uint64_t size, const SolJitSymbol *symbols,
                std::uint64_t symbolCount, const SolJitOptions *options, std::uint64_t *engine,
                std::uint64_t *entry) noexcept
{
    using namespace OpenDot::Sol::LlvmNative;
    return Protect([&] {
        if (!envelope || size > 64ULL * 1024 * 1024 + 88 || !symbols || symbolCount > 512 || !options
            || !engine || !entry || options->Size != sizeof(SolJitOptions) || options->RuntimeContract !=
            0x534F4C0000000002ULL
            || options->Flags > 1 || !options->FailureThunk || !options->EnterNative || !options->LeaveNative)
        {
            throw std::invalid_argument("Invalid bounded native JIT request or runtime contract.");
        }
        *engine = 0;
        *entry = 0;
        auto opened = Open(std::span{envelope, OpenDot::Sol::CheckedInteger<std::size_t>(size)},
                           std::span{symbols, OpenDot::Sol::CheckedInteger<std::size_t>(symbolCount)}, *options);
        const auto address = Take(opened->Compiler->lookup("wmain"));
        auto &registry = Registry();
        const std::lock_guard guard(registry.Gate);
        if (registry.Next == std::numeric_limits<std::uint64_t>::max())
        {
            throw std::overflow_error("Native JIT engine identity limit.");
        }
        const auto id = registry.Next++;
        opened->Code->Published.store(1, std::memory_order_release);
        registry.Values.emplace(id, std::move(opened));
        *entry = address.getValue();
        *engine = id;
    });
}

bool SolJitLookup(std::uint64_t engine, const char *name, std::uint64_t *address) noexcept
{
    using namespace OpenDot::Sol::LlvmNative;
    return Protect([&] {
        if (!name || !*name || std::strlen(name) > 4096 || !address)
        {
            throw std::invalid_argument("Invalid JIT symbol lookup.");
        }
        *address = Take(GetEngine(engine).Compiler->lookup(name)).getValue();
    });
}

bool SolJitGetStats(std::uint64_t engine, SolJitStats *stats) noexcept
{
    using namespace OpenDot::Sol::LlvmNative;
    return Protect([&] {
        if (!stats)
        {
            throw std::invalid_argument("Missing JIT statistics destination.");
        }
        const auto &value = GetEngine(engine);
        *stats = {value.Compiled.load(std::memory_order_relaxed), value.Callbacks.load(std::memory_order_relaxed),
                  value.Code->UnwindTables.load(std::memory_order_relaxed),
                  value.Failures.load(std::memory_order_relaxed)};
    });
}

bool SolJitCopyError(std::uint64_t engine, char *buffer, std::uint64_t capacity) noexcept
{
    using namespace OpenDot::Sol::LlvmNative;
    return Protect([&] {
        if (!buffer || capacity == 0 || capacity > 65536)
        {
            throw std::invalid_argument("Invalid JIT error destination.");
        }
        auto &value = GetEngine(engine);
        const std::lock_guard guard(value.ErrorGate);
        const auto output = std::span{buffer, OpenDot::Sol::CheckedInteger<std::size_t>(capacity)};
        const auto count = std::min(std::strlen(value.Error.data()), output.size() - 1);
        std::memcpy(output.data(), value.Error.data(), count);
        output.subspan(count, 1).front() = '\0';
    });
}
}
