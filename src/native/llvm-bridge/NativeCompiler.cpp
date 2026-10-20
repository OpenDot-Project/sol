// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include <llvm-c/Analysis.h>
#include <llvm-c/Core.h>
#include <llvm-c/Error.h>
#include <llvm-c/IRReader.h>
#include <llvm-c/Target.h>
#include <llvm-c/TargetMachine.h>
#include <llvm-c/Transforms/PassBuilder.h>
#include "NativeCompiler.h"
#include <mutex>
#include <stdexcept>
#include <string>

namespace OpenDot::Sol::LlvmBridge
{

namespace
{
std::once_flag Initialization;
void InitializeTargets()
{
    LLVMInitializeX86TargetInfo();
    LLVMInitializeX86Target();
    LLVMInitializeX86TargetMC();
    LLVMInitializeX86AsmPrinter();
}

struct Session final
{
    LLVMContextRef Context = LLVMContextCreate();
    LLVMModuleRef Module = nullptr;
    LLVMTargetMachineRef Machine = nullptr;
    LLVMMemoryBufferRef Input = nullptr;
    LLVMMemoryBufferRef Output = nullptr;
    ~Session()
    {
        if (Output)
        {
            LLVMDisposeMemoryBuffer(Output);
        }
        if (Input)
        {
            LLVMDisposeMemoryBuffer(Input);
        }
        if (Machine)
        {
            LLVMDisposeTargetMachine(Machine);
        }
        if (Module)
        {
            LLVMDisposeModule(Module);
        }
        LLVMContextDispose(Context);
    }
};

void Check(LLVMBool failed, char* message)
{
    std::string text = message ? message : "LLVM operation failed.";
    if (message)
    {
        LLVMDisposeMessage(message);
    }
    if (failed)
    {
        throw std::runtime_error(text);
    }
}

void Verify(LLVMModuleRef module)
{
    char* message = nullptr;
    const auto failed = LLVMVerifyModule(module, LLVMReturnStatusAction, &message);
    Check(failed, message);
}
} // namespace

std::string NativeVersion()
{
    unsigned major = 0, minor = 0, patch = 0;
    LLVMGetVersion(&major, &minor, &patch);
    return std::to_string(major) + "." + std::to_string(minor) + "." + std::to_string(patch);
}
std::vector<unsigned char> CompileNative(const char* ir, std::size_t size)
{
    if (NativeVersion() != "23.1.3")
    {
        throw std::runtime_error("LLVM 23.1.3 is required.");
    }
    std::call_once(Initialization, InitializeTargets);
    Session session;
    session.Input = LLVMCreateMemoryBufferWithMemoryRangeCopy(ir, size, "sol-module");
    char* message = nullptr;
    auto failed = LLVMParseIRInContext2(session.Context, session.Input, &session.Module, &message);
    Check(failed, message);
    Verify(session.Module);
    LLVMTargetRef target = nullptr;
    message = nullptr;
    failed = LLVMGetTargetFromTriple("x86_64-pc-windows-msvc", &target, &message);
    Check(failed, message);
    session.Machine = LLVMCreateTargetMachine(target, "x86_64-pc-windows-msvc", "x86-64", "", LLVMCodeGenLevelDefault,
                                              LLVMRelocDefault, LLVMCodeModelDefault);
    if (!session.Machine)
    {
        throw std::runtime_error("LLVM target machine creation failed.");
    }
    LLVMSetTarget(session.Module, "x86_64-pc-windows-msvc");
    const auto layout = LLVMCreateTargetDataLayout(session.Machine);
    LLVMSetModuleDataLayout(session.Module, layout);
    LLVMDisposeTargetData(layout);
    const auto options = LLVMCreatePassBuilderOptions();
    const auto error = LLVMRunPasses(session.Module, "default<O1>", session.Machine, options);
    LLVMDisposePassBuilderOptions(options);
    if (error)
    {
        auto errorText = LLVMGetErrorMessage(error);
        std::string copy(errorText);
        LLVMDisposeErrorMessage(errorText);
        throw std::runtime_error(copy);
    }
    Verify(session.Module);
    message = nullptr;
    failed =
        LLVMTargetMachineEmitToMemoryBuffer(session.Machine, session.Module, LLVMObjectFile, &message, &session.Output);
    Check(failed, message);
    const auto start = reinterpret_cast<const unsigned char*>(LLVMGetBufferStart(session.Output));
    return {start, start + LLVMGetBufferSize(session.Output)};
}
} // namespace OpenDot::Sol::LlvmBridge