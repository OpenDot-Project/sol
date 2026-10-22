// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;

namespace OpenDot.Sol.Lowering;

/// <summary>Immediate LLVM C++ API operations, not a serialized compiler IR or textual assembly.</summary>
public enum LlvmOperation
{
    VoidType, IntegerType, FloatType, DoubleType, PointerType, ArrayType, StructType,
    Integer, Floating, Zero, Function, Parameter, Block, Position, Alloca, Load, Store,
    Gep, StructGep, Call, Return, Branch, ConditionalBranch, Unreachable, Phi, Extract,
    Add, Sub, Mul, SDiv, UDiv, SRem, URem, And, Or, Xor, Shl, LShr, AShr,
    FAdd, FSub, FMul, FDiv, FRem, FNeg, ICmp, FCmp, ZExt, SExt, Trunc, FPExt,
}

/// <summary>Owns native LLVM objects. Handles are session-scoped opaque identities, never pointers.</summary>
public interface ILlvmModule : IDisposable
{
    long Apply(LlvmOperation operation, long type, long[] operands, long data, string text);
    long CreateString(byte[] utf8);
    void Verify();
    string Print();
    byte[] Bitcode();
    byte[] CompileObject();
}

/// <summary>Creates LLVM C++ API sessions; lowering policy remains in C#.</summary>
public interface ILlvmCompiler
{
    string Version { get; }
    ILlvmModule CreateModule();
}

/// <summary>Embedded COFF linking through the pinned LLD.COFF and LLD.Common libraries.</summary>
public interface ILldLinker
{
    int LinkWindows(string[] arguments, out string diagnostics);
}
