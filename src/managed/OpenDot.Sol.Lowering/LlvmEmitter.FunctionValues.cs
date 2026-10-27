// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;

namespace OpenDot.Sol.Lowering;

public sealed partial class LlvmEmitter
{
    private long FunctionSignature(ResolvedType function)
    {
        if (function.Kind != ResolvedTypeKind.Function
            || function.Arguments.Length != function.ParameterModes.Length + 1)
        { throw new InvalidOperationException("Verified FIR contains an invalid function signature."); }
        var parameters = function.ParameterModes.Select((mode, index) => mode is AstPassingMode.Ref or AstPassingMode.Out
            ? T("ptr") : T(Type(function.Arguments[index]))).ToArray();
        return Op(LlvmOperation.FunctionType, T(Type(function.Arguments[^1])), parameters);
    }

    private Value EmitIndirectCall(FirIndirectCall call)
    {
        var callee = Expression(call.Function);
        long signature = FunctionSignature(call.Function.Type);
        var operands = new long[call.Arguments.Length + 1];
        operands[0] = callee.Operand;
        for (int index = 0; index < call.Arguments.Length; index++)
        {
            _cancellation.ThrowIfCancellationRequested();
            var argument = call.Arguments[index];
            if (argument.Mode is AstPassingMode.Ref or AstPassingMode.Out)
            {
                if (argument.Value is not FirRead read)
                { throw new InvalidOperationException("Verified indirect ref/out argument has no local address."); }
                operands[index + 1] = Address(read.Slot);
            }
            else
            {
                operands[index + 1] = Expression(argument.Value).Operand;
            }
        }
        long result = Op(LlvmOperation.IndirectCall, signature, operands);
        if (call.Type.Kind == ResolvedTypeKind.Bottom)
        { Op(LlvmOperation.Unreachable); _terminated = true; }
        return new(Type(call.Type), result, call.Type);
    }
}
