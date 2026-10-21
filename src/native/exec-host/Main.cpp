// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "../runtime/Core.h"
#include <llvm-c/Analysis.h>
#include <llvm-c/Core.h>
#include <llvm-c/Error.h>
#include <llvm-c/IRReader.h>
#include <llvm-c/LLJIT.h>
#include <llvm-c/Orc.h>
#include <llvm-c/Target.h>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <stdexcept>
#include <string>
#include <vector>

namespace OpenDot::Sol::ExecHost
{
namespace
{
void Check(LLVMErrorRef error)
{
    if (!error)
    {
        return;
    }
    auto *message = LLVMGetErrorMessage(error);
    std::string copy(message);
    LLVMDisposeErrorMessage(message);
    throw std::runtime_error(copy);
}

struct Session final
{
    LLVMContextRef RawContext = LLVMContextCreate();
    LLVMOrcThreadSafeContextRef Context = LLVMOrcCreateNewThreadSafeContextFromLLVMContext(RawContext);
    LLVMOrcLLJITRef Jit = nullptr;
    LLVMModuleRef Module = nullptr;
    LLVMMemoryBufferRef Input = nullptr;

    ~Session()
    {
        if (Module)
        {
            LLVMDisposeModule(Module);
        }
        if (Input)
        {
            LLVMDisposeMemoryBuffer(Input);
        }
        if (Jit)
        {
            if (auto error = LLVMOrcDisposeLLJIT(Jit))
            {
                LLVMConsumeError(error);
            }
        }
        LLVMOrcDisposeThreadSafeContext(Context);
    }
};

void Verify(LLVMModuleRef module)
{
    char *message = nullptr;
    const auto failed = LLVMVerifyModule(module, LLVMReturnStatusAction, &message);
    std::string copy(message ? message : "LLVM verification failed.");
    LLVMDisposeMessage(message);
    if (failed)
    {
        throw std::runtime_error(copy);
    }
}

void BindRuntime(LLVMOrcLLJITRef jit)
{
    std::vector<LLVMOrcCSymbolMapPair> symbols;
    const auto add = [&](const char *name, std::uintptr_t address) {
        LLVMJITSymbolFlags flags{};
        flags.GenericFlags = LLVMJITSymbolGenericFlagsExported | LLVMJITSymbolGenericFlagsCallable;
        symbols.push_back({LLVMOrcLLJITMangleAndIntern(jit, name), {address, flags}});
    };
#define SOL_BIND(name) add(#name, reinterpret_cast<std::uintptr_t>(&name))
    SOL_BIND(sol_rt_literal);
    SOL_BIND(sol_rt_arguments);
    SOL_BIND(sol_rt_shutdown);
    SOL_BIND(sol_rt_fail);
    SOL_BIND(sol_rt_check_abi);
    SOL_BIND(sol_rt_write_string);
    SOL_BIND(sol_rt_write_integer);
    SOL_BIND(sol_rt_write_float);
    SOL_BIND(sol_rt_newline);
    SOL_BIND(sol_rt_array_length);
    SOL_BIND(sol_rt_array_get);
    SOL_BIND(sol_rt_string_concat);
    SOL_BIND(sol_rt_string_compare);
    SOL_BIND(sol_rt_string_length);
    SOL_BIND(sol_rt_string_substring);
    SOL_BIND(sol_rt_string_index_of);
    SOL_BIND(sol_rt_string_hash);
    SOL_BIND(sol_gc_attach);
    SOL_BIND(sol_gc_detach);
    SOL_BIND(sol_gc_push_frame);
    SOL_BIND(sol_gc_pop_frame);
    SOL_BIND(sol_gc_store_ref);
    SOL_BIND(sol_gc_poll);
    SOL_BIND(sol_gc_collect);
    SOL_BIND(sol_rt_array_new);
    SOL_BIND(sol_rt_array_address);
    SOL_BIND(memcpy);
    SOL_BIND(memset);
#undef SOL_BIND
    add("fmod", reinterpret_cast<std::uintptr_t>(static_cast<double (*)(double, double)>(&std::fmod)));
    add("fmodf", reinterpret_cast<std::uintptr_t>(static_cast<float (*)(float, float)>(&std::fmod)));
    auto *unit = LLVMOrcAbsoluteSymbols(symbols.data(), symbols.size()); // Takes ownership of interned names.
    const auto error = LLVMOrcJITDylibDefine(LLVMOrcLLJITGetMainJITDylib(jit), unit);
    if (error)
    {
        LLVMOrcDisposeMaterializationUnit(unit);
        Check(error);
    }
}
}

int Run(int argc, wchar_t **argv)
{
    if (argc < 3 || std::wcscmp(argv[2], L"--") != 0)
    {
        throw std::runtime_error("Usage: sol-exec-host MODULE.ll -- [ARG...]");
    }
    unsigned major = 0, minor = 0, patch = 0;
    LLVMGetVersion(&major, &minor, &patch);
    if (major != 23 || minor != 1 || patch != 3)
    {
        throw std::runtime_error("LLVM 23.1.3 is required.");
    }
    LLVMInitializeX86TargetInfo();
    LLVMInitializeX86Target();
    LLVMInitializeX86TargetMC();
    LLVMInitializeX86AsmPrinter();
    std::ifstream source(std::filesystem::path(argv[1]), std::ios::binary | std::ios::ate);
    if (!source || source.tellg() < 0 || source.tellg() > 64 * 1024 * 1024)
    {
        throw std::runtime_error("Cannot read the bounded LLVM module.");
    }
    const auto length = static_cast<std::size_t>(source.tellg());
    std::string text(length, '\0');
    source.seekg(0);
    source.read(text.data(), static_cast<std::streamsize>(length));
    if (!source)
    {
        throw std::runtime_error("LLVM module read failed.");
    }
    Session session;
    Check(LLVMOrcCreateLLJIT(&session.Jit, nullptr));
    session.Input = LLVMCreateMemoryBufferWithMemoryRangeCopy(text.data(), text.size(), "sol-program");
    char *message = nullptr;
    if (LLVMParseIRInContext2(session.RawContext, session.Input, &session.Module, &message))
    {
        std::string copy(message ? message : "Cannot parse LLVM IR.");
        LLVMDisposeMessage(message);
        throw std::runtime_error(copy);
    }
    if (std::strcmp(LLVMGetTarget(session.Module), "x86_64-pc-windows-msvc") != 0)
    {
        throw std::runtime_error("ORC target mismatch.");
    }
    LLVMSetDataLayout(session.Module, LLVMOrcLLJITGetDataLayoutStr(session.Jit));
    Verify(session.Module);
    BindRuntime(session.Jit);
    auto *module = LLVMOrcCreateNewThreadSafeModule(session.Module, session.Context);
    session.Module = nullptr;
    Check(LLVMOrcLLJITAddLLVMIRModule(session.Jit, LLVMOrcLLJITGetMainJITDylib(session.Jit), module));
    LLVMOrcExecutorAddress address = 0;
    Check(LLVMOrcLLJITLookup(session.Jit, &address, "wmain"));
    auto entry = reinterpret_cast<int (*)(int, wchar_t **)>(static_cast<std::uintptr_t>(address));
    std::vector<wchar_t *> arguments{argv[0]};
    for (int index = 3; index < argc; ++index)
    {
        arguments.push_back(argv[index]);
    }
    arguments.push_back(nullptr);
    return entry(static_cast<int>(arguments.size() - 1), arguments.data());
}
}

int wmain(int argc, wchar_t **argv)
{
    try
    {
        return OpenDot::Sol::ExecHost::Run(argc, argv);
    }
    catch (const std::exception &error)
    {
        std::fprintf(stderr, "Sol ORC host: %s\n", error.what());
        return 1;
    }
}