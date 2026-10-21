// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Session.h"
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
#include <cstdlib>
#include <stdexcept>

namespace OpenDot::Sol::LlvmNative
{
thread_local std::string Error;

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
        OpenDot::Sol::LlvmNative::Error.clear();
        return true;
    }
    catch (const std::exception &error)
    {
        OpenDot::Sol::LlvmNative::Error = error.what();
        return false;
    }
    catch (...)
    {
        OpenDot::Sol::LlvmNative::Error = "Unknown native LLVM failure.";
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
        result = new OpenDot::Sol::LlvmNative::Session();
    });
    return result;
}

void SolLlvmDestroy(void *session) noexcept
{
    delete static_cast<OpenDot::Sol::LlvmNative::Session *>(session);
}

const char *SolLlvmError() noexcept
{
    return OpenDot::Sol::LlvmNative::Error.c_str();
}

const char *SolLlvmVersion() noexcept
{
    return LLVM_VERSION_STRING;
}

bool SolLlvmApply(void *pointer, int operation, std::int64_t type, const std::int64_t *operands, std::uint64_t count,
                  std::int64_t data, const char *text, std::int64_t *result) noexcept
{
    return Protect([&] {
        if (!result || !text || count > 1000000 || (count && !operands) || operation < 0 || operation > static_cast<int>
            (OpenDot::Sol::LlvmNative::Operation::FPExt))
        {
            throw std::invalid_argument("Malformed LLVM operation request.");
        }
        std::vector<std::int64_t> args;
        if (count)
        {
            args.assign(operands, operands + count);
        }
        *result = Get(pointer).Apply(static_cast<OpenDot::Sol::LlvmNative::Operation>(operation), type, args, data,
                                     text);
    });
}

bool SolLlvmString(void *pointer, const unsigned char *bytes, std::uint64_t size, std::int64_t *result) noexcept
{
    return Protect([&] {
        if (!result || size > 64 * 1024 * 1024 || (size && !bytes))
        {
            throw std::invalid_argument("Invalid LLVM String literal bytes.");
        }
        auto &session = Get(pointer);
        session.CheckThread();
        auto &context = session.Context;
        auto *data = llvm::ConstantDataArray::get(context, llvm::ArrayRef(bytes, static_cast<std::size_t>(size)));
        auto *i32 = llvm::Type::getInt32Ty(context);
        auto *ptr = llvm::PointerType::getUnqual(context);
        auto *type = llvm::StructType::get(context, {ptr, i32, i32, i32, i32, data->getType()});
        auto *constant = llvm::ConstantStruct::get(type, {llvm::Constant::getNullValue(ptr),
                                                          llvm::ConstantInt::get(i32, 0),
                                                          llvm::ConstantInt::get(i32, 0),
                                                          llvm::ConstantInt::get(i32, size),
                                                          llvm::ConstantInt::getSigned(i32, -1), data});
        auto *global = new llvm::GlobalVariable(*session.Module, type, false, llvm::GlobalValue::PrivateLinkage,
                                                constant, "sol_str");
        global->setAlignment(llvm::Align(16));
        *result = session.Add({nullptr, global});
    });
}

bool SolLlvmVerify(void *pointer) noexcept
{
    return Protect([&] {
        auto &session = Get(pointer);
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
        auto *memory = std::malloc(data.size() ? data.size() : 1);
        if (!memory)
        {
            throw std::bad_alloc();
        }
        if (!data.empty())
        {
            std::memcpy(memory, data.data(), data.size());
        }
        *bytes = memory;
        *size = data.size();
    });
}

void SolLlvmFree(void *bytes) noexcept
{
    std::free(bytes);
}
}
