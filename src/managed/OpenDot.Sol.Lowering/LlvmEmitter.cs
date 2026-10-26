// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using SolLiteral = OpenDot.Sol.Fir.FirLiterals;

namespace OpenDot.Sol.Lowering;

/// <summary>Lowers verified FIR directly through the LLVM C++ operation bridge.</summary>
public sealed partial class LlvmEmitter
{
    private readonly VerifiedFirProgram _verified;
    private readonly ILlvmModule _module;
    private readonly CancellationToken _cancellation;
    private readonly Dictionary<string, long> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<int, long> _addresses = [];
    private readonly Dictionary<int, long> _parameters = [];
    private FirFunction _function = null!;
    private long _nativeFunction, _block;
    private bool _terminated;
    private readonly Stack<(long Break, long Continue)> _loops = [];
    private readonly record struct Value(string Type, long Operand, ResolvedType SourceType);

    private LlvmEmitter(VerifiedFirProgram program, ILlvmModule module, CancellationToken cancellation)
    { _verified = program; _module = module; _cancellation = cancellation; }

    public static ILlvmModule Build(VerifiedFirProgram program, ILlvmCompiler compiler,
        CancellationToken cancellationToken = default, bool cExports = false)
    {
        ArgumentNullException.ThrowIfNull(program); ArgumentNullException.ThrowIfNull(compiler);
        cancellationToken.ThrowIfCancellationRequested();
        if (compiler.Version != "23.1.3") { throw new InvalidOperationException("LLVM 23.1.3 is required."); }
        var module = compiler.CreateModule();
        try { new LlvmEmitter(program, module, cancellationToken).Build(cExports); module.Verify(); return module; }
        catch { module.Dispose(); throw; }
    }

    /// <summary>Inspection only: the LLVM printer renders an already constructed native module.</summary>
    public static string Emit(VerifiedFirProgram program, bool staticRuntime = false,
        CancellationToken cancellationToken = default, bool cExports = false)
    {
        ArgumentNullException.ThrowIfNull(program);
        cancellationToken.ThrowIfCancellationRequested();
        using var module = Build(program, NativeCompiler.LoadBridge(NativeCompiler.BackendRoot(null)), cancellationToken, cExports);
        return module.Print();
    }

    private long Op(LlvmOperation op, long type = 0, long[]? args = null, long data = 0, string text = "")
    { _cancellation.ThrowIfCancellationRequested(); return _module.Apply(op, type, args ?? [], data, text); }
    private long T(string type)
    {
        if (_types.TryGetValue(type, out long result)) { return result; }
        result = type switch
        {
            "void" => Op(LlvmOperation.VoidType), "ptr" => Op(LlvmOperation.PointerType),
            "float" => Op(LlvmOperation.FloatType), "double" => Op(LlvmOperation.DoubleType),
            _ => Op(LlvmOperation.IntegerType, data: int.Parse(type.AsSpan(1), CultureInfo.InvariantCulture)),
        };
        _types.Add(type, result); return result;
    }
    private long Int(long value, string type = "i32") => Integer(value.ToString(CultureInfo.InvariantCulture), type);
    private long Integer(string value, string type) => Op(LlvmOperation.Integer, T(type), text: value);
    private long Declare(string name, string result, string[] parameters, int linkage = 0)
        => Declare(name, T(result), parameters.Select(T).ToArray(), linkage);
    private long Declare(string name, long result, long[] parameters, int linkage = 0)
    {
        long function = Op(LlvmOperation.Function, result, parameters, linkage, name);
        _functions.Add(name, function); return function;
    }
    private long Call(string name, params long[] values) => Op(LlvmOperation.Call, args: [_functions[name], .. values]);
    private long NewBlock(string name = "") => Op(LlvmOperation.Block, args: [_nativeFunction], text: name);
    private void Start(long block) { Op(LlvmOperation.Position, args: [block]); _block = block; _terminated = false; }
    private void Branch(long target) => Op(LlvmOperation.Branch, args: [target]);
    private void Conditional(long condition, long yes, long no) => Op(LlvmOperation.ConditionalBranch, args: [condition, yes, no]);
    private long Gep(long type, long pointer, params long[] indices) => Op(LlvmOperation.Gep, type, [pointer, .. indices]);
    private long Field(long type, long pointer, int index) => Op(LlvmOperation.StructGep, type, [pointer], index);

    private void Build(bool cExports)
    {
        DeclareRuntime();
        foreach (var function in _verified.Program.Functions.Where(f => f.RuntimeFunction == FirRuntimeFunction.None))
        {
            Declare("sol_fn_" + function.Id, Type(function.ReturnType), function.Parameters.Select(p =>
                p.Mode is AstPassingMode.Ref or AstPassingMode.Out ? "ptr" : Type(p.Type)).ToArray(), cExports ? 1 : 0);
        }
        foreach (var function in _verified.Program.Functions.Where(f => f.RuntimeFunction == FirRuntimeFunction.None)) { EmitFunction(function); }
        if (cExports) { EmitExports(); }
        if (!_verified.Program.IsLibrary)
        {
            _nativeFunction = Declare("wmain", "i32", ["i32", "ptr"]); Start(NewBlock("entry"));
            Call("sol_rt_check_abi", Int(unchecked((long)RuntimeContract.Id), "i64"));
            long args = Call("sol_rt_arguments", Op(LlvmOperation.Parameter, args: [_nativeFunction], data: 0), Op(LlvmOperation.Parameter, args: [_nativeFunction], data: 1));
            Call("sol_fn_" + _verified.Program.Entry, args); Call("sol_rt_shutdown"); Op(LlvmOperation.Return, args: [Int(0)]);
        }
    }

    private void EmitFunction(FirFunction function)
    {
        _function = function; _nativeFunction = _functions["sol_fn_" + function.Id]; _addresses.Clear(); _parameters.Clear(); Start(NewBlock("entry"));
        for (int index = 0; index < function.Parameters.Length; ++index)
        { _parameters.Add(function.Parameters[index].Id, Op(LlvmOperation.Parameter, args: [_nativeFunction], data: index)); }
        BeginRoots(function);
        foreach (var slot in function.Slots)
        {
            if (slot.IsParameter && slot.Mode is AstPassingMode.Ref or AstPassingMode.Out) { _addresses.Add(slot.Id, _parameters[slot.Id]); }
            else if (!_addresses.ContainsKey(slot.Id)) { _addresses.Add(slot.Id, Op(LlvmOperation.Alloca, T(Type(slot.Type)))); }
        }
        AllocateArrayIterationSlots(function.Body!);
        foreach (var parameter in function.Parameters.Where(p => p.Mode is not (AstPassingMode.Ref or AstPassingMode.Out)))
        { Store(new(Type(parameter.Type), _parameters[parameter.Id], parameter.Type), Address(parameter.Id)); }
        Call("sol_gc_poll"); Statement(function.Body!);
        if (!_terminated) { Op(LlvmOperation.Unreachable); }
    }

    private void Statement(FirStatement statement)
    {
        _cancellation.ThrowIfCancellationRequested(); if (_terminated) { return; }
        switch (statement)
        {
            case FirBlock block: foreach (var child in block.Statements) { Statement(child); } break;
            case FirDeclare { Initializer: not null } declaration: Store(Expression(declaration.Initializer), Address(declaration.Slot)); break;
            case FirDeclare: break;
            case FirStore store: Store(Expression(store.Value), Address(store.Slot)); break;
            case FirEvaluate evaluation: Expression(evaluation.Value); break;
            case FirReturn ret:
                if (ret.Value is null || FirVerifier.IsEmpty(ret.Value.Type))
                { if (ret.Value is not null) { Expression(ret.Value); } EndRoots(); Op(LlvmOperation.Return); }
                else { var returned = Expression(ret.Value); EndRoots(); Op(LlvmOperation.Return, args: [returned.Operand]); }
                _terminated = true; break;
            case FirIf conditional:
                var condition = Expression(conditional.Condition); long yes = NewBlock(), no = NewBlock(), join = NewBlock();
                Conditional(condition.Operand, yes, no);
                Start(yes); Statement(conditional.Then); bool left = !_terminated; if (left) { Branch(join); }
                Start(no); if (conditional.Else is not null) { Statement(conditional.Else); } bool right = !_terminated; if (right) { Branch(join); }
                Start(join); if (!left && !right) { Op(LlvmOperation.Unreachable); _terminated = true; } break;
            case FirLoop loop:
                if (loop.Initializer is not null) { Statement(loop.Initializer); }
                long test = NewBlock(), body = NewBlock(), update = NewBlock(), after = NewBlock(); Branch(test); Start(test);
                if (loop.Condition is null) { Branch(body); } else { Conditional(Expression(loop.Condition).Operand, body, after); }
                Start(body); _loops.Push((after, update)); Statement(loop.Body); _loops.Pop(); if (!_terminated) { Branch(update); }
                Start(update); if (loop.Update is not null) { Statement(loop.Update); }
                if (!_terminated) { Call("sol_gc_poll"); Branch(test); } Start(after); break;
            case FirArrayForEach iteration: ArrayForEach(iteration); break;
            case FirBreak: Branch(_loops.Peek().Break); _terminated = true; break;
            case FirContinue: Branch(_loops.Peek().Continue); _terminated = true; break;
            default: throw new InvalidOperationException("Unverified FIR statement.");
        }
    }

    private Value Expression(FirExpression expression) => KeepAlive(ExpressionCore(expression));
    private Value ExpressionCore(FirExpression expression)
    {
        _cancellation.ThrowIfCancellationRequested(); string type = Type(expression.Type);
        switch (expression)
        {
            case FirConstant constant: return Constant(constant);
            case FirRead read: return new(type, Op(LlvmOperation.Load, T(type), [Address(read.Slot)], type == "ptr" ? 1 : 0), read.Type);
            case FirUnary unary:
                var operand = Expression(unary.Value);
                if (unary.Operation == "+") { return operand; }
                if (unary.Operation == "-")
                { return Floating(unary.Type) ? new(type, Op(LlvmOperation.FNeg, args: [operand.Operand]), unary.Type) : Arithmetic("-", new(type, Int(0, type), unary.Type), operand, unary.Type); }
                return new(type, Op(LlvmOperation.Xor, args: [operand.Operand, Int(unary.Operation == "!" ? 1 : -1, type)]), unary.Type);
            case FirBinary { Operation: "&&" or "||" } shortCircuit: return ShortCircuit(shortCircuit);
            case FirBinary binary: return Arithmetic(binary.Operation, Expression(binary.Left), Expression(binary.Right), binary.Type);
            case FirArrayLength length:
                return new("i32", Call(FirVerifier.IsString(length.Array.Type) ? "sol_rt_string_length" : "sol_rt_array_length", Expression(length.Array).Operand), length.Type);
            case FirArrayIndex index:
                var indexed = Expression(index.Array); var subscript = Expression(index.Index);
                long location = Call("sol_rt_array_address", indexed.Operand, subscript.Operand, Int(ElementWidth(index.Type)));
                return new(type, Op(LlvmOperation.Load, T(type), [location], type == "ptr" ? 1 : 0), index.Type);
            case FirArrayLiteral array: return ArrayLiteral(array);
            case FirCall call: return EmitCall(call);
            case FirIntrinsic intrinsic:
                var inputs = intrinsic.Arguments.Select(Expression).ToArray();
                if (!intrinsic.ParameterOrder.IsEmpty) { inputs = inputs.Zip(intrinsic.ParameterOrder).OrderBy(p => p.Second).Select(p => p.First).ToArray(); }
                string symbol = intrinsic.Operation switch
                {
                    FirIntrinsicKind.StringSubstring => "sol_rt_string_substring", FirIntrinsicKind.StringIndexOf => "sol_rt_string_index_of",
                    FirIntrinsicKind.StringCompare => "sol_rt_string_compare", FirIntrinsicKind.StringHash => "sol_rt_string_hash",
                    FirIntrinsicKind.GcCollect => "sol_gc_collect", _ => throw new InvalidOperationException("Unverified intrinsic.")
                };
                return new(type, Call(symbol, inputs.Select(v => v.Operand).ToArray()), intrinsic.Type);
            default: throw new InvalidOperationException("Unverified FIR expression.");
        }
    }

    private Value EmitCall(FirCall call)
    {
        var target = _verified.Program.Functions[call.Target]; var arguments = new long[target.Parameters.Length]; var values = new List<Value>();
        foreach (var argument in call.Arguments)
        {
            if (target.RuntimeFunction != FirRuntimeFunction.None) { values.Add(Expression(argument.Value)); continue; }
            arguments[argument.Parameter] = argument.Mode is AstPassingMode.Ref or AstPassingMode.Out ? Address(((FirRead)argument.Value).Slot) : Expression(argument.Value).Operand;
        }
        if (target.RuntimeFunction != FirRuntimeFunction.None)
        {
            int channel = target.RuntimeFunction is FirRuntimeFunction.Error or FirRuntimeFunction.Errorln ? 1 : 0;
            foreach (var value in values) { Print(value, channel); }
            if (target.RuntimeFunction is FirRuntimeFunction.Println or FirRuntimeFunction.Errorln) { Call("sol_rt_newline", Int(channel)); }
            return new("void", 0, call.Type);
        }
        long result = Call("sol_fn_" + target.Id, arguments);
        if (call.Type.Kind == ResolvedTypeKind.Bottom) { Op(LlvmOperation.Unreachable); _terminated = true; }
        return new(Type(call.Type), result, call.Type);
    }

    private void Print(Value value, int channel)
    {
        if (FirVerifier.IsString(value.SourceType)) { Call("sol_rt_write_string", value.Operand, Int(channel)); return; }
        if (Floating(value.SourceType))
        {
            long data = value.Type == "float" ? Op(LlvmOperation.FPExt, T("double"), [value.Operand]) : value.Operand;
            Call("sol_rt_write_float", data, Int(value.Type == "float" ? 32 : 64), Int(channel)); return;
        }
        long integer = value.Type == "i64" ? value.Operand : Op(Unsigned(value.SourceType) || value.SourceType.Name == "bool" ? LlvmOperation.ZExt : LlvmOperation.SExt, T("i64"), [value.Operand]);
        Call("sol_rt_write_integer", integer, Int(value.SourceType.Name == "bool" ? 2 : Unsigned(value.SourceType) ? 1 : 0), Int(channel));
    }
    private Value Constant(FirConstant constant)
    {
        string type = Type(constant.Type);
        if (FirVerifier.IsString(constant.Type)) { return new(type, Call("sol_rt_literal", _module.CreateString(SolLiteral.DecodeUtf8(constant.Spelling))), constant.Type); }
        if (type == "void") { return new(type, 0, constant.Type); }
        if (constant.Type.Name == "bool") { return new(type, Int(constant.Spelling == "true" ? 1 : 0, type), constant.Type); }
        if (Floating(constant.Type))
        {
            double value = double.Parse(constant.Spelling.Replace("_", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
            if (type == "float") { value = (float)value; }
            return new(type, Op(LlvmOperation.Floating, T(type), data: BitConverter.DoubleToInt64Bits(value)), constant.Type);
        }
        return new(type, Integer(SolLiteral.ParseInteger(constant.Spelling).ToString(CultureInfo.InvariantCulture), type), constant.Type);
    }
    private long Address(int slot) => _addresses[slot];
    public static string Type(ResolvedType type) => FirVerifier.IsEmpty(type) || type.Kind == ResolvedTypeKind.Bottom ? "void"
        : type.Kind != ResolvedTypeKind.Primitive ? "ptr" : type.Name switch
        { "bool" => "i1", "sbyte" or "byte" => "i8", "short" => "i16", "int" or "uint" => "i32", "long" or "ulong" => "i64", "single" => "float", "double" => "double", _ => throw new InvalidOperationException("Unsupported native type.") };
    private static bool Unsigned(ResolvedType type) => type.Name is "byte" or "uint" or "ulong";
    private static bool Floating(ResolvedType type) => type.Name is "single" or "double";
}
