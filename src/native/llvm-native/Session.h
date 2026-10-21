// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include "Api.h"
// Upstream LLVM SDK templates trigger these MSVC diagnostics; first-party code keeps /W4 /WX.
#pragma warning(push)
#pragma warning(disable: 4244 4702)
#include <llvm/IR/IRBuilder.h>
#include <llvm/IR/NoFolder.h>
#include <llvm/IR/Module.h>
#include <llvm/Target/TargetMachine.h>
#pragma warning(pop)
#include <memory>
#include <thread>
#include <vector>

namespace OpenDot::Sol::LlvmNative
{
enum class Operation
{
    VoidType, IntegerType, FloatType, DoubleType, PointerType, ArrayType, StructType,
    Integer, Floating, Zero, Function, Parameter, Block, Position, Alloca, Load, Store,
    Gep, StructGep, Call, Return, Branch, ConditionalBranch, Unreachable, Phi, Extract,
    Add, Sub, Mul, SDiv, UDiv, SRem, URem, And, Or, Xor, Shl, LShr, AShr,
    FAdd, FSub, FMul, FDiv, FRem, FNeg, ICmp, FCmp, ZExt, SExt, Trunc, FPExt,
};

struct Entry final
{
    llvm::Type *Type = nullptr;
    llvm::Value *Value = nullptr;
    llvm::BasicBlock *Block = nullptr;
};

struct Session final
{
    llvm::LLVMContext Context;
    std::unique_ptr<llvm::Module> Module;
    llvm::IRBuilder<llvm::NoFolder> Builder;
    std::unique_ptr<llvm::TargetMachine> Machine;
    std::vector<Entry> Entries{{}};
    std::thread::id Thread = std::this_thread::get_id();
    std::uint32_t Identity;
    Session();
    void CheckThread() const;
    Entry &Get(std::int64_t handle);
    llvm::Type *Type(std::int64_t handle);
    llvm::Value *Value(std::int64_t handle);
    llvm::BasicBlock *Block(std::int64_t handle);
    std::int64_t Add(Entry entry);
    std::int64_t Apply(Operation op, std::int64_t type, const std::vector<std::int64_t> &args, std::int64_t data,
                       const char *text);
    std::vector<unsigned char> Output(int kind);
};

void Verify(llvm::Module &module);
void Initialize();
extern thread_local std::string Error;
}
