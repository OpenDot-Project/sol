// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once

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
}
