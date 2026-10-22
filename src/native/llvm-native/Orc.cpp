// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Session.h"
// Upstream LLVM SDK templates trigger these MSVC diagnostics; first-party code keeps /W4 /WX.
#pragma warning(push)
#pragma warning(disable: 4244 4702)
#include <llvm/Bitcode/BitcodeReader.h>
#include <llvm/ExecutionEngine/Orc/LLJIT.h>
#include <llvm/Support/MemoryBuffer.h>
#pragma warning(pop)
#include <stdexcept>

extern "C" bool SolLlvmExecute(const unsigned char *bitcode, std::uint64_t size,
                               const SolLlvmSymbol *symbols, std::uint64_t symbolCount, int argc, wchar_t **argv,
                               int *exitCode) noexcept
{
    using namespace OpenDot::Sol::LlvmNative;
    try
    {
        if (!bitcode || size < 4 || size > 64 * 1024 * 1024 || !symbols || symbolCount > 256 || !exitCode || argc < 1 ||
            !argv)
        {
            throw std::invalid_argument("Invalid bounded ORC bitcode request.");
        }
        Initialize();
        auto context = std::make_unique<llvm::LLVMContext>();
        auto buffer = llvm::MemoryBuffer::getMemBufferCopy(
            llvm::StringRef(reinterpret_cast<const char *>(bitcode), static_cast<std::size_t>(size)), "sol-program.bc");
        auto parsed = llvm::parseBitcodeFile(buffer->getMemBufferRef(), *context);
        if (!parsed)
        {
            throw std::runtime_error(llvm::toString(parsed.takeError()));
        }
        auto module = std::move(*parsed);
        if (module->getTargetTriple() != llvm::Triple("x86_64-pc-windows-msvc"))
        {
            throw std::runtime_error("ORC target mismatch.");
        }
        Verify(*module);
        auto created = llvm::orc::LLJITBuilder().create();
        if (!created)
        {
            throw std::runtime_error(llvm::toString(created.takeError()));
        }
        auto jit = std::move(*created);
        module->setDataLayout(jit->getDataLayout());
        llvm::orc::SymbolMap bindings;
        for (std::uint64_t index = 0; index < symbolCount; ++index)
        {
            const auto &symbol = symbols[index];
            if (!symbol.Name || !*symbol.Name || !symbol.Address)
            {
                throw std::invalid_argument("Invalid ORC binding.");
            }
            auto name = jit->mangleAndIntern(symbol.Name);
            if (!bindings.try_emplace(name, llvm::orc::ExecutorSymbolDef(
                                          llvm::orc::ExecutorAddr(symbol.Address),
                                          llvm::JITSymbolFlags::Exported | llvm::JITSymbolFlags::Callable)).second)
            {
                throw std::invalid_argument("Duplicate ORC binding.");
            }
        }
        if (auto error = jit->getMainJITDylib().define(llvm::orc::absoluteSymbols(std::move(bindings))))
        {
            throw std::runtime_error(llvm::toString(std::move(error)));
        }
        if (auto error = jit->addIRModule(llvm::orc::ThreadSafeModule(std::move(module), std::move(context))))
        {
            throw std::runtime_error(llvm::toString(std::move(error)));
        }
        auto address = jit->lookup("wmain");
        if (!address)
        {
            throw std::runtime_error(llvm::toString(address.takeError()));
        }
        auto entry = address->toPtr<int(int, wchar_t **)>();
        *exitCode = entry(argc, argv);
        Error.clear();
        return true;
    }
    catch (const std::exception &error)
    {
        Error = error.what();
        return false;
    }
    catch (...)
    {
        Error = "Unknown native ORC failure.";
        return false;
    }
}
