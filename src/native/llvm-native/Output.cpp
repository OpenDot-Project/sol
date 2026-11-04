// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Session.h"
#include "Api.h"
#include "Support.h"
#include "../Checked.h"
#include <span>
#include <tuple>
#include <string>
// Upstream LLVM SDK templates trigger these MSVC diagnostics; first-party code keeps /W4 /WX.
#pragma warning(push)
#pragma warning(disable: 4244 4702)
#include <llvm/Bitcode/BitcodeWriter.h>
#include <llvm/Config/llvm-config.h>
#include <llvm/IR/LegacyPassManager.h>
#include <llvm/Passes/PassBuilder.h>
#include <llvm/Support/CodeGen.h>
#include <llvm/Transforms/Utils/Cloning.h>
#pragma warning(pop)
#include <cstring>
#include <stdexcept>

namespace OpenDot::Sol::LlvmNative
{
namespace
{
thread_local std::string Error;
thread_local const char *FallbackError = nullptr;
}

void ClearError() noexcept
{
    Error.clear();
    FallbackError = nullptr;
}

void SetError(std::string_view message) noexcept
{
    try
    {
        Error.assign(message);
        FallbackError = nullptr;
    }
    catch (...)
    {
        Error.clear();
        FallbackError = "Native LLVM failure: error diagnostics allocation failed.";
    }
}

void SetError(const std::exception &error) noexcept
{
    try
    {
        SetError(error.what());
    }
    catch (...)
    {
        SetError("Native LLVM failure: exception diagnostics are unavailable.");
    }
}

const char *LastError() noexcept
{
    return FallbackError ? FallbackError : Error.c_str();
}

std::vector<unsigned char> Session::Output(int kind)
{
    CheckThread();
    Verify(*Module);
    llvm::SmallVector<char, 0> buffer;
    llvm::raw_svector_ostream output(buffer);
    if (kind == 0)
    {
        Module->print(output, nullptr);
    }
    else if (kind == 1)
    {
        llvm::WriteBitcodeToFile(*Module, output);
    }
    else if (kind == 2)
    {
        auto copy = llvm::CloneModule(*Module);
        llvm::LoopAnalysisManager loops;
        llvm::FunctionAnalysisManager functions;
        llvm::CGSCCAnalysisManager cgscc;
        llvm::ModuleAnalysisManager modules;
        llvm::PassBuilder passes(Machine.get());
        passes.registerLoopAnalyses(loops);
        passes.registerFunctionAnalyses(functions);
        passes.registerCGSCCAnalyses(cgscc);
        passes.registerModuleAnalyses(modules);
        passes.crossRegisterProxies(loops, functions, cgscc, modules);
        auto pipeline = passes.buildPerModuleDefaultPipeline(llvm::OptimizationLevel::O1);
        pipeline.run(*copy, modules);
        Verify(*copy);
        llvm::legacy::PassManager emission;
        if (Machine->addPassesToEmitFile(emission, output, nullptr, llvm::CodeGenFileType::ObjectFile))
        {
            throw std::runtime_error("LLVM COFF emission is unavailable.");
        }
        emission.run(*copy);
    }
    else
    {
        throw std::invalid_argument("Unknown LLVM output kind.");
    }
    return {buffer.begin(), buffer.end()};
}
}

namespace
{
template <class Action> bool Protect(Action action) noexcept
{
    try
    {
        action();
        OpenDot::Sol::LlvmNative::ClearError();
        return true;
    }
    catch (const std::exception &error)
    {
        OpenDot::Sol::LlvmNative::SetError(error);
        return false;
    }
    catch (...)
    {
        OpenDot::Sol::LlvmNative::SetError("Unknown native LLVM failure.");
        return false;
    }
}

OpenDot::Sol::LlvmNative::Session &Get(void *pointer)
{
    if (!pointer)
    {
        throw std::invalid_argument("LLVM session is disposed.");
    }
    return *static_cast<OpenDot::Sol::LlvmNative::Session *>(pointer);
}
}

extern "C"
{
void *SolLlvmCreate() noexcept
{
    void *result = nullptr;
    Protect([&] {
        OpenDot::Sol::LlvmNative::Initialize();
        result = std::make_unique<OpenDot::Sol::LlvmNative::Session>("sol-module").release();
    });
    return result;
}

void SolLlvmDestroy(void *session) noexcept
{
    const std::unique_ptr<OpenDot::Sol::LlvmNative::Session> owner{
        static_cast<OpenDot::Sol::LlvmNative::Session *>(session)};
}

const char *SolLlvmError() noexcept
{
    return OpenDot::Sol::LlvmNative::LastError();
}

const char *SolLlvmVersion() noexcept
{
    return LLVM_VERSION_STRING;
}

std::uint64_t SolLlvmProtocol() noexcept
{
    return 0x534F4C4C00000003ULL;
}

bool SolLlvmApply(void *pointer, int operation, std::int64_t type, const std::int64_t *operands, std::uint64_t count,
                  std::int64_t data, const char *text, std::int64_t *result) noexcept
{
    return Protect([&] {
        if (!result || !text || count > 1000000 || (count && !operands) || operation < 0 || operation > static_cast<int>
            (OpenDot::Sol::LlvmNative::Operation::IndirectInvoke))
        {
            throw std::invalid_argument("Malformed LLVM operation request.");
        }
        std::vector<std::int64_t> args;
        if (count)
        {
            const std::span input(operands, count);
            args.assign(input.begin(), input.end());
        }
        *result = Get(pointer).Apply(static_cast<OpenDot::Sol::LlvmNative::Operation>(operation), type, args, data,
                                     text);
    });
}

bool SolLlvmString(void *pointer, const unsigned char *bytes, std::uint64_t size, std::int64_t *result) noexcept
{
    return Protect([&] {
        if (!result || size > 64ULL * 1024 * 1024 || (size && !bytes))
        {
            throw std::invalid_argument("Invalid LLVM String literal bytes.");
        }
        auto &session = Get(pointer);
        session.CheckThread();
        auto &context = session.Context;
        auto &data = *llvm::ConstantDataArray::get(context,
                                                   llvm::ArrayRef(
                                                       bytes, OpenDot::Sol::CheckedInteger<std::size_t>(size)));
        auto *i32 = llvm::Type::getInt32Ty(context);
        auto *ptr = llvm::PointerType::getUnqual(context);
        auto *type = llvm::StructType::get(context, {ptr, i32, i32, i32, i32, data.getType()});
        auto *constant = llvm::ConstantStruct::get(type, {llvm::Constant::getNullValue(ptr),
                                                          llvm::ConstantInt::get(i32, 0),
                                                          llvm::ConstantInt::get(i32, 0),
                                                          llvm::ConstantInt::get(i32, size),
                                                          llvm::ConstantInt::getSigned(i32, -1), &data});
        auto global = std::make_unique<llvm::GlobalVariable>(*session.Module, type, false,
                                                             llvm::GlobalValue::PrivateLinkage,
                                                             constant, "sol_str");
        auto &registered = *global;
        // The module-taking constructor registered lifetime ownership; entries are non-owning.
        std::ignore = global.release();
        registered.setAlignment(llvm::Align(16));
        *result = session.Add({nullptr, &registered});
    });
}

bool SolLlvmData(void *pointer, const unsigned char *bytes, std::uint64_t size,
                 const char *name, std::int64_t *result) noexcept
{
    return Protect([&] {
        if (!result || !name || !*name || size > 64ULL * 1024 * 1024 + 88 || (size && !bytes))
        {
            throw std::invalid_argument("Invalid LLVM executable data.");
        }
        auto &session = Get(pointer);
        session.CheckThread();
        if (session.Module->getNamedValue(name))
        {
            throw std::invalid_argument("Duplicate LLVM executable data symbol.");
        }
        auto &data = *llvm::ConstantDataArray::get(session.Context,
                                                   llvm::ArrayRef(
                                                       bytes, OpenDot::Sol::CheckedInteger<std::size_t>(size)));
        auto global = std::make_unique<llvm::GlobalVariable>(*session.Module, data.getType(), true,
                                                             llvm::GlobalValue::ExternalLinkage, &data, name);
        auto &registered = *global;
        std::ignore = global.release();
        registered.setAlignment(llvm::Align(16));
        *result = session.Add({nullptr, &registered});
    });
}

bool SolLlvmVerify(void *pointer) noexcept
{
    return Protect([&] {
        const auto &session = Get(pointer);
        session.CheckThread();
        OpenDot::Sol::LlvmNative::Verify(*session.Module);
    });
}

bool SolLlvmOutput(void *pointer, int kind, void **bytes, std::uint64_t *size) noexcept
{
    return Protect([&] {
        if (!bytes || !size)
        {
            throw std::invalid_argument("Missing LLVM output location.");
        }
        auto data = Get(pointer).Output(kind);
        auto memory = std::make_unique<unsigned char[]>(data.empty() ? 1 : data.size());
        if (!data.empty())
        {
            std::memcpy(memory.get(), data.data(), data.size());
        }
        *bytes = memory.release();
        *size = data.size();
    });
}

void SolLlvmFree(void *bytes) noexcept
{
    const std::unique_ptr<unsigned char[]> owner{static_cast<unsigned char *>(bytes)};
}
}
