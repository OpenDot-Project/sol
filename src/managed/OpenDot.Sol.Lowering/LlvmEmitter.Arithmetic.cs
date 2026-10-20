// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Fir;

namespace OpenDot.Sol.Lowering;

public sealed partial class LlvmEmitter
{
    private Value Arithmetic(string operation, Value left, Value right, ResolvedType resultType)
    {
        string result = Temp();
        bool floating = Floating(left.SourceType);
        bool unsigned = Unsigned(left.SourceType);
        string type = left.Type;
        if (operation is "==" or "!=" or "<" or ">" or "<=" or ">=")
        {
            string predicate = operation switch
            {
                "==" => floating ? "oeq" : "eq", "!=" => floating ? "une" : "ne",
                "<" => floating ? "olt" : unsigned ? "ult" : "slt", ">" => floating ? "ogt" : unsigned ? "ugt" : "sgt",
                "<=" => floating ? "ole" : unsigned ? "ule" : "sle", _ => floating ? "oge" : unsigned ? "uge" : "sge",
            };
            Line($"{result} = {(floating ? "fcmp" : "icmp")} {predicate} {type} {left.Operand}, {right.Operand}");
            return new("i1", result, resultType);
        }
        string opcode = operation switch { "+" => "add", "-" => "sub", "*" => "mul", "/" => "div", "%" => "rem",
            "&" => "and", "|" => "or", "^" => "xor", "<<" => "shl", ">>" => unsigned ? "lshr" : "ashr", _ => throw new InvalidOperationException("Invalid arithmetic opcode.") };
        if (floating)
        {
            Line($"{result} = f{opcode} {type} {left.Operand}, {right.Operand}");
            return new(type, result, resultType);
        }
        if (operation is "+" or "-" or "*")
        {
            string pair = Temp(), overflow = Temp();
            Line($"{pair} = call {{ {type}, i1 }} @llvm.{(unsigned ? "u" : "s")}{opcode}.with.overflow.{type}({type} {left.Operand}, {type} {right.Operand})");
            Line($"{result} = extractvalue {{ {type}, i1 }} {pair}, 0");
            Line($"{overflow} = extractvalue {{ {type}, i1 }} {pair}, 1");
            Guard(overflow, 1);
        }
        else if (operation is "/" or "%")
        {
            string zero = Temp(); Line($"{zero} = icmp eq {type} {right.Operand}, 0"); Guard(zero, 2);
            if (!unsigned)
            {
                int width = int.Parse(type.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
                string minimum = (-(System.Numerics.BigInteger.One << (width - 1))).ToString(System.Globalization.CultureInfo.InvariantCulture);
                string first = Temp(), second = Temp(), overflow = Temp();
                Line($"{first} = icmp eq {type} {left.Operand}, {minimum}");
                Line($"{second} = icmp eq {type} {right.Operand}, -1");
                Line($"{overflow} = and i1 {first}, {second}"); Guard(overflow, 1);
            }
            Line($"{result} = {(unsigned ? "u" : "s")}{opcode} {type} {left.Operand}, {right.Operand}");
        }
        else if (operation is "<<" or ">>")
        {
            int width = int.Parse(type.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
            string invalid = Temp(); Line($"{invalid} = icmp uge {right.Type} {right.Operand}, {width}"); Guard(invalid, 3);
            string count = right.Operand;
            if (right.Type != type)
            {
                count = Temp(); Line($"{count} = {(width > 32 ? "zext" : "trunc")} {right.Type} {right.Operand} to {type}");
            }
            Line($"{result} = {opcode} {type} {left.Operand}, {count}");
        }
        else { Line($"{result} = {opcode} {type} {left.Operand}, {right.Operand}"); }
        return new(type, result, resultType);
    }

    private Value ShortCircuit(FirBinary binary)
    {
        var left = Expression(binary.Left);
        string initialBlock = _block, rhs = Label(), after = Label();
        bool and = binary.Operation == "&&";
        Line($"br i1 {left.Operand}, label %{(and ? rhs : after)}, label %{(and ? after : rhs)}");
        Start(rhs); var right = Expression(binary.Right); string rightBlock = _block; Branch(after);
        Start(after); string result = Temp();
        Line($"{result} = phi i1 [ {(and ? "false" : "true")}, %{initialBlock} ], [ {right.Operand}, %{rightBlock} ]");
        return new("i1", result, binary.Type);
    }

    private void Guard(string failed, int code)
    {
        string error = Label(), resume = Label();
        Line($"br i1 {failed}, label %{error}, label %{resume}");
        Start(error); Line($"call void @sol_rt_fail(i32 {code})"); Line("unreachable");
        Start(resume);
    }
}
