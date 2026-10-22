// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Globalization;
using System.Numerics;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Fir;

namespace OpenDot.Sol.Lowering;

public sealed partial class LlvmEmitter
{
    private Value Arithmetic(string operation, Value left, Value right, ResolvedType resultType)
    {
        if (FirVerifier.IsString(left.SourceType))
        {
            if (operation == "+") { return new("ptr", Call("sol_rt_string_concat", left.Operand, right.Operand), resultType); }
            long comparison = Call("sol_rt_string_compare", left.Operand, right.Operand);
            return new("i1", Compare(operation, false, false, comparison, Int(0)), resultType);
        }
        bool floating = Floating(left.SourceType), unsigned = Unsigned(left.SourceType); string type = left.Type;
        if (operation is "==" or "!=" or "<" or ">" or "<=" or ">=")
        { return new("i1", Compare(operation, floating, unsigned, left.Operand, right.Operand), resultType); }
        LlvmOperation opcode = operation switch
        {
            "+" => floating ? LlvmOperation.FAdd : LlvmOperation.Add,
            "-" => floating ? LlvmOperation.FSub : LlvmOperation.Sub,
            "*" => floating ? LlvmOperation.FMul : LlvmOperation.Mul,
            "/" => floating ? LlvmOperation.FDiv : unsigned ? LlvmOperation.UDiv : LlvmOperation.SDiv,
            "%" => floating ? LlvmOperation.FRem : unsigned ? LlvmOperation.URem : LlvmOperation.SRem,
            "&" => LlvmOperation.And, "|" => LlvmOperation.Or, "^" => LlvmOperation.Xor,
            "<<" => LlvmOperation.Shl, ">>" => unsigned ? LlvmOperation.LShr : LlvmOperation.AShr,
            _ => throw new InvalidOperationException("Invalid verified operation."),
        };
        long result;
        if (!floating && operation is "+" or "-" or "*")
        {
            string name = "llvm." + (unsigned ? "u" : "s") + (operation == "+" ? "add" : operation == "-" ? "sub" : "mul") + ".with.overflow." + type;
            long pair = Call(name, left.Operand, right.Operand);
            result = Op(LlvmOperation.Extract, args: [pair], data: 0);
            Guard(Op(LlvmOperation.Extract, args: [pair], data: 1), 1);
        }
        else if (!floating && operation is "/" or "%")
        {
            Guard(Compare("==", false, false, right.Operand, Int(0, type)), 2);
            if (!unsigned)
            {
                int width = int.Parse(type.AsSpan(1), CultureInfo.InvariantCulture);
                long minimum = Integer((-(BigInteger.One << (width - 1))).ToString(CultureInfo.InvariantCulture), type);
                Guard(Op(LlvmOperation.And, args: [Compare("==", false, false, left.Operand, minimum), Compare("==", false, false, right.Operand, Int(-1, type))]), 1);
            }
            result = Op(opcode, args: [left.Operand, right.Operand]);
        }
        else if (operation is "<<" or ">>")
        {
            int width = int.Parse(type.AsSpan(1), CultureInfo.InvariantCulture);
            Guard(Compare(">=", false, true, right.Operand, Int(width, right.Type)), 3);
            long count = right.Type == type ? right.Operand : Op(width > 32 ? LlvmOperation.ZExt : LlvmOperation.Trunc, T(type), [right.Operand]);
            result = Op(opcode, args: [left.Operand, count]);
        }
        else { result = Op(opcode, args: [left.Operand, right.Operand]); }
        return new(type, result, resultType);
    }
    private long Compare(string relation, bool floating, bool unsigned, long left, long right)
    {
        int predicate = relation switch { "==" => 0, "!=" => 1, "<" => 2, ">" => 3, "<=" => 4, ">=" => 5, _ => throw new InvalidOperationException("Invalid comparison.") };
        return Op(floating ? LlvmOperation.FCmp : LlvmOperation.ICmp, args: [left, right], data: predicate + (unsigned ? 6 : 0));
    }
    private Value ShortCircuit(FirBinary binary)
    {
        var left = Expression(binary.Left); long initial = _block, rhs = NewBlock(), after = NewBlock(); bool and = binary.Operation == "&&";
        Conditional(left.Operand, and ? rhs : after, and ? after : rhs);
        Start(rhs); var right = Expression(binary.Right); long rightBlock = _block; Branch(after); Start(after);
        return new("i1", Op(LlvmOperation.Phi, T("i1"), [Int(and ? 0 : 1, "i1"), initial, right.Operand, rightBlock]), binary.Type);
    }
    private void Guard(long failed, int code)
    {
        long error = NewBlock(), resume = NewBlock(); Conditional(failed, error, resume);
        Start(error); Call("sol_rt_fail", Int(code)); Op(LlvmOperation.Unreachable); Start(resume);
    }
}
