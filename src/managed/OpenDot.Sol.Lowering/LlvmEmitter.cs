// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using SolLiteral = OpenDot.Sol.Fir.FirLiterals;

namespace OpenDot.Sol.Lowering;

/// <summary>Emits LLVM IR exclusively from verified, closed native FIR.</summary>
public sealed partial class LlvmEmitter
{
    private readonly VerifiedFirProgram _verified;
    private readonly bool _staticRuntime;
    private readonly CancellationToken _cancellation;
    private readonly StringBuilder _text = new();
    private readonly List<string> _strings = [];
    private FirFunction _function = null!;
    private int _next;
    private string _block = "entry";
    private bool _terminated;
    private readonly Stack<(string Break, string Continue)> _loops = [];
    private readonly record struct Value(string Type, string Operand, ResolvedType SourceType);

    private LlvmEmitter(VerifiedFirProgram program, bool staticRuntime, CancellationToken cancellation) { _verified = program; _staticRuntime = staticRuntime; _cancellation = cancellation; }
    public static string Emit(VerifiedFirProgram program, bool staticRuntime = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        var emitter = new LlvmEmitter(program, staticRuntime, cancellationToken);
        return emitter.Build();
    }

    private string Build()
    {
        _text.AppendLine("target triple = \"x86_64-pc-windows-msvc\"");
        _text.AppendLine($"; SolRT linkage: {(_staticRuntime ? "static" : "dynamic")}");
        _text.AppendLine("declare ptr @sol_rt_literal(ptr)");
        _text.AppendLine("declare ptr @sol_rt_arguments(i32, ptr)");
        _text.AppendLine("declare void @sol_rt_shutdown()");
        _text.AppendLine("declare void @sol_rt_fail(i32) noreturn");
        _text.AppendLine("declare void @sol_rt_write_string(ptr, i32)");
        _text.AppendLine("declare void @sol_rt_write_integer(i64, i32, i32)");
        _text.AppendLine("declare void @sol_rt_write_float(double, i32, i32)");
        _text.AppendLine("declare void @sol_rt_newline(i32)");
        _text.AppendLine("declare i32 @sol_rt_array_length(ptr)");
        _text.AppendLine("declare ptr @sol_rt_array_get(ptr, i32)");
        foreach (int width in new[] { 8, 16, 32, 64 })
        {
            foreach (string sign in new[] { "s", "u" })
            {
                foreach (string operation in new[] { "add", "sub", "mul" })
                { _text.AppendLine($"declare {{ i{width}, i1 }} @llvm.{sign}{operation}.with.overflow.i{width}(i{width}, i{width})"); }
            }
        }
        foreach (var function in _verified.Program.Functions.Where(f => f.RuntimeFunction == FirRuntimeFunction.None)) { EmitFunction(function); }
        _text.AppendLine("define i32 @wmain(i32 %argc, ptr %argv) {");
        _text.AppendLine("entry:");
        _text.AppendLine("  %args = call ptr @sol_rt_arguments(i32 %argc, ptr %argv)");
        _text.AppendLine($"  call void @sol_fn_{_verified.Program.Entry}(ptr %args)");
        _text.AppendLine("  call void @sol_rt_shutdown()");
        _text.AppendLine("  ret i32 0\n}");
        foreach (string literal in _strings) { _text.AppendLine(literal); }
        return _text.ToString();
    }

    private void EmitFunction(FirFunction function)
    {
        _function = function; _next = 0; _block = "entry"; _terminated = false;
        string parameters = string.Join(", ", function.Parameters.Select((p, i) => $"{(p.Mode is AstPassingMode.Ref or AstPassingMode.Out ? "ptr" : Type(p.Type))} %p{i}"));
        _text.AppendLine($"define {Type(function.ReturnType)} @sol_fn_{function.Id}({parameters}) {{\nentry:");
        foreach (var slot in function.Slots)
        {
            if (slot.IsParameter && slot.Mode is AstPassingMode.Ref or AstPassingMode.Out) { continue; }
            Line($"%s{slot.Id} = alloca {Type(slot.Type)}");
        }
        for (int index = 0; index < function.Parameters.Length; index++)
        {
            var parameter = function.Parameters[index];
            if (parameter.Mode is AstPassingMode.Ref or AstPassingMode.Out) { continue; }
            Line($"store {Type(parameter.Type)} %p{index}, ptr %s{parameter.Id}");
        }
        Statement(function.Body!);
        if (!_terminated) { Line("unreachable"); }
        _text.AppendLine("}");
    }

    private void Statement(FirStatement statement)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (_terminated) { return; }
        switch (statement)
        {
            case FirBlock block: foreach (var child in block.Statements) { Statement(child); } break;
            case FirDeclare { Initializer: not null } declaration:
                var initialized = Expression(declaration.Initializer); Line($"store {initialized.Type} {initialized.Operand}, ptr {Address(declaration.Slot)}"); break;
            case FirDeclare: break;
            case FirStore store:
                var value = Expression(store.Value); Line($"store {value.Type} {value.Operand}, ptr {Address(store.Slot)}"); break;
            case FirEvaluate evaluation: Expression(evaluation.Value); break;
            case FirReturn ret:
                if (ret.Value is null || FirVerifier.IsEmpty(ret.Value.Type)) { if (ret.Value is not null) { Expression(ret.Value); } Line("ret void"); }
                else { var returned = Expression(ret.Value); Line($"ret {returned.Type} {returned.Operand}"); }
                _terminated = true; break;
            case FirIf conditional:
                var condition = Expression(conditional.Condition);
                string thenLabel = Label(), elseLabel = Label(), join = Label();
                Line($"br i1 {condition.Operand}, label %{thenLabel}, label %{elseLabel}");
                Start(thenLabel); Statement(conditional.Then); bool left = !_terminated; if (left) { Branch(join); }
                Start(elseLabel); if (conditional.Else is not null) { Statement(conditional.Else); } bool right = !_terminated; if (right) { Branch(join); }
                if (left || right) { Start(join); } else { _terminated = true; } break;
            case FirLoop loop:
                if (loop.Initializer is not null) { Statement(loop.Initializer); }
                string test = Label(), body = Label(), update = Label(), after = Label(); Branch(test); Start(test);
                if (loop.Condition is null) { Branch(body); }
                else { var tested = Expression(loop.Condition); Line($"br i1 {tested.Operand}, label %{body}, label %{after}"); }
                Start(body); _loops.Push((after, update)); Statement(loop.Body); _loops.Pop(); if (!_terminated) { Branch(update); }
                Start(update); if (loop.Update is not null) { Statement(loop.Update); } if (!_terminated) { Branch(test); }
                Start(after); break;
            case FirBreak: Branch(_loops.Peek().Break); _terminated = true; break;
            case FirContinue: Branch(_loops.Peek().Continue); _terminated = true; break;
            default: throw new InvalidOperationException("Unverified FIR statement reached LLVM lowering.");
        }
    }

    private Value Expression(FirExpression expression)
    {
        _cancellation.ThrowIfCancellationRequested();
        string type = Type(expression.Type);
        switch (expression)
        {
            case FirConstant constant: return Constant(constant);
            case FirRead read:
                string loaded = Temp(); Line($"{loaded} = load {type}, ptr {Address(read.Slot)}"); return new(type, loaded, read.Type);
            case FirUnary unary:
                var operand = Expression(unary.Value);
                if (unary.Operation == "+") { return operand; }
                if (unary.Operation == "-")
                {
                    if (Floating(unary.Type))
                    { string negative = Temp(); Line($"{negative} = fneg {type} {operand.Operand}"); return new(type, negative, unary.Type); }
                    return Arithmetic("-", new(type, "0", unary.Type), operand, unary.Type);
                }
                string negated = Temp(); Line($"{negated} = xor {type} {operand.Operand}, {(unary.Operation == "!" ? "true" : "-1")}"); return new(type, negated, unary.Type);
            case FirBinary { Operation: "&&" or "||" } shortCircuit: return ShortCircuit(shortCircuit);
            case FirBinary binary:
                return Arithmetic(binary.Operation, Expression(binary.Left), Expression(binary.Right), binary.Type);
            case FirArrayLength length:
                var array = Expression(length.Array); string size = Temp(); Line($"{size} = call i32 @sol_rt_array_length(ptr {array.Operand})"); return new("i32", size, length.Type);
            case FirArrayIndex index:
                var indexed = Expression(index.Array); var subscript = Expression(index.Index); string item = Temp();
                Line($"{item} = call ptr @sol_rt_array_get(ptr {indexed.Operand}, i32 {subscript.Operand})"); return new("ptr", item, index.Type);
            case FirCall call: return Call(call);
            default: throw new InvalidOperationException("Unverified FIR expression reached LLVM lowering.");
        }
    }

    private Value Call(FirCall call)
    {
        var target = _verified.Program.Functions[call.Target];
        var arguments = new string[target.Parameters.Length];
        var values = new List<Value>();
        foreach (var argument in call.Arguments)
        {
            if (target.RuntimeFunction != FirRuntimeFunction.None) { values.Add(Expression(argument.Value)); continue; }
            if (argument.Mode is AstPassingMode.Ref or AstPassingMode.Out) { arguments[argument.Parameter] = "ptr " + Address(((FirRead)argument.Value).Slot); }
            else { var value = Expression(argument.Value); arguments[argument.Parameter] = value.Type + " " + value.Operand; }
        }
        if (target.RuntimeFunction != FirRuntimeFunction.None)
        {
            int channel = target.RuntimeFunction is FirRuntimeFunction.Error or FirRuntimeFunction.Errorln ? 1 : 0;
            foreach (var value in values) { Print(value, channel); }
            if (target.RuntimeFunction is FirRuntimeFunction.Println or FirRuntimeFunction.Errorln) { Line($"call void @sol_rt_newline(i32 {channel})"); }
            return new("void", "", call.Type);
        }
        string result = Type(call.Type) == "void" ? "" : Temp();
        Line($"{(result.Length == 0 ? "" : result + " = ")}call {Type(call.Type)} @sol_fn_{target.Id}({string.Join(", ", arguments)})");
        if (call.Type.Kind == ResolvedTypeKind.Bottom) { Line("unreachable"); _terminated = true; }
        return new(Type(call.Type), result, call.Type);
    }

    private void Print(Value value, int channel)
    {
        if (FirVerifier.IsString(value.SourceType)) { Line($"call void @sol_rt_write_string(ptr {value.Operand}, i32 {channel})"); return; }
        if (Floating(value.SourceType))
        {
            string data = value.Operand;
            if (value.Type == "float") { data = Temp(); Line($"{data} = fpext float {value.Operand} to double"); }
            Line($"call void @sol_rt_write_float(double {data}, i32 {(value.Type == "float" ? 32 : 64)}, i32 {channel})"); return;
        }
        string integer = value.Operand;
        if (value.Type != "i64") { integer = Temp(); Line($"{integer} = {(Unsigned(value.SourceType) || value.SourceType.Name == "bool" ? "zext" : "sext")} {value.Type} {value.Operand} to i64"); }
        int kind = value.SourceType.Name == "bool" ? 2 : Unsigned(value.SourceType) ? 1 : 0;
        Line($"call void @sol_rt_write_integer(i64 {integer}, i32 {kind}, i32 {channel})");
    }

    private Value Constant(FirConstant constant)
    {
        string type = Type(constant.Type);
        if (FirVerifier.IsString(constant.Type))
        {
            byte[] bytes = SolLiteral.DecodeUtf8(constant.Spelling);
            string global = "@sol_str_" + _strings.Count;
            string escaped = string.Concat(bytes.Select(b => "\\" + b.ToString("X2", CultureInfo.InvariantCulture)));
            _strings.Add($"{global} = private global {{ ptr, i32, i32, i32, i32, [{bytes.Length} x i8] }} {{ ptr null, i32 0, i32 0, i32 {bytes.Length}, i32 -1, [{bytes.Length} x i8] c\"{escaped}\" }}, align 16");
            string literal = Temp(); Line($"{literal} = call ptr @sol_rt_literal(ptr {global})");
            return new(type, literal, constant.Type);
        }
        if (type == "void") { return new(type, "", constant.Type); }
        if (constant.Type.Name == "bool") { return new(type, constant.Spelling, constant.Type); }
        if (Floating(constant.Type))
        {
            double value = double.Parse(constant.Spelling.Replace("_", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
            if (type == "float") { value = (float)value; }
            return new(type, "0x" + BitConverter.DoubleToUInt64Bits(value).ToString("X16", CultureInfo.InvariantCulture), constant.Type);
        }
        return new(type, SolLiteral.ParseInteger(constant.Spelling).ToString(CultureInfo.InvariantCulture), constant.Type);
    }

    private string Address(int slot)
    {
        var local = _function.Slots[slot];
        return local.IsParameter && local.Mode is AstPassingMode.Ref or AstPassingMode.Out ? "%p" + _function.Parameters.IndexOf(local) : "%s" + slot;
    }
    public static string Type(ResolvedType type) => FirVerifier.IsEmpty(type) || type.Kind == ResolvedTypeKind.Bottom ? "void"
        : type.Kind != ResolvedTypeKind.Primitive ? "ptr" : type.Name switch
        { "bool" => "i1", "sbyte" or "byte" => "i8", "short" => "i16", "int" or "uint" => "i32", "long" or "ulong" => "i64", "single" => "float", "double" => "double", _ => throw new InvalidOperationException("Unsupported native type.") };
    private static bool Unsigned(ResolvedType type) => type.Name is "byte" or "uint" or "ulong";
    private static bool Floating(ResolvedType type) => type.Name is "single" or "double";
    private string Temp() => "%v" + _next++;
    private string Label() => "b" + _next++;
    private void Line(string text) => _text.AppendLine("  " + text);
    private void Start(string label) { _text.AppendLine(label + ":"); _block = label; _terminated = false; }
    private void Branch(string label) => Line($"br label %{label}");
}
