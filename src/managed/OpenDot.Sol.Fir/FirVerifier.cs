// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Fir;

public sealed record FirVerification(VerifiedFirProgram? Program, ImmutableArray<Diagnostic> Diagnostics);

/// <summary>Checks the closed native subset, exact type/slot/call identity, and CFG assignment proof.</summary>
public static class FirVerifier
{
    public static FirVerification Verify(FirProgram program, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        cancellationToken.ThrowIfCancellationRequested();
        var checker = new Checker(program, cancellationToken);
        checker.Check();
        return new(checker.Errors.Count == 0 ? new VerifiedFirProgram(program, checker.Graphs.ToImmutableArray()) : null, checker.Errors.ToImmutableArray());
    }

    internal static bool IsEmpty(ResolvedType type) => type.Kind == ResolvedTypeKind.Tuple && type.Arguments.IsEmpty;
    internal static bool IsString(ResolvedType type) => type.Declaration is { ModuleName: "std.core", Name: "String", Arity: 0 };
    internal static bool Supported(ResolvedType type) => type.Kind == ResolvedTypeKind.Primitive && type.Name is not ("half" or "quad")
        || IsEmpty(type) || IsString(type)
        || type.Kind == ResolvedTypeKind.Array && type.Arguments.Length == 1
            && (IsString(type.Arguments[0]) || type.Arguments[0].Kind == ResolvedTypeKind.Primitive && type.Arguments[0].Name is not ("half" or "quad"));

    private sealed class Checker(FirProgram program, CancellationToken cancellation)
    {
        internal List<Diagnostic> Errors { get; } = [];
        internal List<FirFlowGraph> Graphs { get; } = [];
        private FirFunction _function = null!;
        private readonly HashSet<FirNode> _active = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<int> _declared = [];
        private int _depth;

        internal void Check()
        {
            if (program.IsLibrary ? program.Entry != -1 : program.Entry < 0 || program.Entry >= program.Functions.Length)
            { throw new ArgumentException("FIR entry index is invalid.", nameof(program)); }
            for (int index = 0; index < program.Functions.Length; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                _function = program.Functions[index];
                _declared.Clear();
                if (_function.Id != index) { Fail("Function identity/index mismatch.", _function.Origin); }
                CheckExport();
                CheckSourceContract();
                if (_function.RuntimeFunction != FirRuntimeFunction.None) { CheckRuntime(); continue; }
                CheckType(_function.ReturnType, _function.Origin);
                for (int slot = 0; slot < _function.Slots.Length; slot++)
                {
                    var local = _function.Slots[slot];
                    if (local.Id != slot) { Fail("Slot identity/index mismatch.", local.Origin); }
                    CheckType(local.Type, local.Origin);
                    if (local.Mode == AstPassingMode.Vararg) { Fail("Expanded varargs require a qualified runtime adapter.", local.Origin); }
                    if (local.Type.Kind == ResolvedTypeKind.Bottom || IsEmpty(local.Type)) { Fail("A slot must have a storable native type.", local.Origin); }
                }
                if (!_function.Parameters.SequenceEqual(_function.Slots.Where(s => s.IsParameter))
                    || _function.Parameters.Select(p => p.Id).Distinct().Count() != _function.Parameters.Length)
                { Fail("Parameter/slot identity mismatch.", _function.Origin); }
                if (_function.Body is null) { Fail("An ordinary function requires a body.", _function.Origin); continue; }
                Statement(_function.Body, 0);
                if (_function.Slots.Any(s => !s.IsParameter && !_declared.Contains(s.Id))) { Fail("Every local slot requires a declaration.", _function.Origin); }
                if (Errors.Count != 0) { continue; }
                var flow = FirFlowGraph.Create(_function, program.Functions, cancellation);
                Graphs.Add(flow);
                Errors.AddRange(flow.Diagnostics);
            }
            if (program.IsLibrary) { return; }
            var entry = program.Functions[program.Entry];
            if (entry.RuntimeFunction != FirRuntimeFunction.None || !IsEmpty(entry.ReturnType)
                || entry.Parameters is not [{ Type.Kind: ResolvedTypeKind.Array, Mode: AstPassingMode.Value } arg] || !IsString(arg.Type.Arguments[0]))
            { Fail("Entry must accept []String and return ().", entry.Origin); }
        }

        private void CheckRuntime()
        {
            if (_function.Body is not null || !_function.Parameters.IsEmpty || !IsEmpty(_function.ReturnType))
            { Fail("Invalid runtime adapter signature.", _function.Origin); }
            string? expected = _function.RuntimeFunction switch
            {
                FirRuntimeFunction.Print => "print", FirRuntimeFunction.Println => "println",
                FirRuntimeFunction.Error => "error", FirRuntimeFunction.Errorln => "errorln", _ => null
            };
            bool trusted = expected is not null && program.Analysis.Sources.Declarations.Files.SelectMany(f => f.Nodes).OfType<AstFunction>()
                .Any(f => ReferenceEquals(f.Origin.Element, _function.Origin.Element)
                    && f.Name == expected && program.Catalog.Owns(f));
            if (!trusted) { Fail("Runtime adapter is not compiler-owned.", _function.Origin); }
        }

        private void CheckExport()
        {
            if (_function.ExportName is null) { return; }
            var file = program.Analysis.Sources.Declarations.Files.SingleOrDefault(f => f.Nodes.OfType<AstFunction>()
                .Any(n => ReferenceEquals(n.Origin.Element, _function.Origin.Element)));
            var source = file?.Nodes.OfType<AstFunction>().SingleOrDefault(n => ReferenceEquals(n.Origin.Element, _function.Origin.Element));
            if (!program.IsLibrary || source is null || program.Catalog.Owns(source) || !source.IsPublic || source.Name != _function.ExportName)
            { Fail("Export identity is not a public source-owned library function.", _function.Origin); return; }
            for (var parent = file!.GetParent(source); parent is not null; parent = file.GetParent(parent))
            { if (parent is AstTypeDeclaration) { Fail("C export requires a module-level function.", _function.Origin); break; } }
        }

        private void CheckSourceContract()
        {
            var source = program.Analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
                .OfType<AstFunction>().SingleOrDefault(function => ReferenceEquals(function.Origin.Element, _function.Origin.Element));
            if (source is null)
            {
                Fail("Function identity is not anchored to a source declaration.", _function.Origin);
                return;
            }

            if (source.Name != _function.Name) { Fail("Function name differs from its source declaration.", _function.Origin); }
            var summary = program.Analysis.GetBody(source);
            if (summary is null || summary.Status != BodyBindingStatus.Bound)
            { Fail("Function body lacks a completed source analysis summary.", _function.Origin); }
            else if (summary.ReturnType != _function.ReturnType)
            { Fail("Function result type differs from source analysis.", _function.Origin); }

            var declaredEffects = source.ThrownTypes.Select(program.Analysis.Sources.Types.GetType).ToImmutableArray();
            if (_function.Effects.IsDefault || !declaredEffects.SequenceEqual(_function.Effects))
            { Fail("Function effect metadata differs from its source declaration.", _function.Origin); }
            ValidateEffects(_function.Effects, _function.Origin);
            if (summary is not null)
            {
                foreach (var escaping in summary.EscapingEffects.Where(IsChecked))
                {
                    if (!declaredEffects.Any(budget => program.Analysis.Types.ClassifyConversion(escaping, budget, cancellation) != IntrinsicConversion.None))
                    { Fail("Source body analysis found a checked effect outside the function signature.", _function.Origin); }
                }
            }

            if (_function.RuntimeFunction != FirRuntimeFunction.None) { return; }
            if (source.Parameters.Length != _function.Parameters.Length)
            { Fail("Function parameter count differs from its source declaration.", _function.Origin); return; }
            for (int index = 0; index < source.Parameters.Length; index++)
            {
                var sourceParameter = source.Parameters[index];
                var firParameter = _function.Parameters[index];
                if (sourceParameter.Name != firParameter.Name || sourceParameter.Type is null
                    || program.Analysis.Sources.Types.GetType(sourceParameter.Type) != firParameter.Type
                    || sourceParameter.PassingMode != firParameter.Mode)
                { Fail("Function parameter metadata differs from its source declaration.", firParameter.Origin); }
            }
        }

        private void ValidateEffects(ImmutableArray<ResolvedType> effects, AstOrigin origin)
        {
            if (effects.IsDefault) { Fail("Function effect metadata is uninitialized.", origin); return; }
            var root = program.Analysis.Types.Types.SingleOrDefault(type => type.Kind == ResolvedTypeKind.Nominal
                && type.Declaration is { ModuleName: "std.core", Name: "Throwable", Arity: 0 });
            if (root is null)
            {
                Fail("The source workspace has no std.core.Throwable root.", origin);
                return;
            }
            var seen = new HashSet<ResolvedType>();
            foreach (var effect in effects)
            {
                try { program.Analysis.Types.ValidateOwnership(effect); }
                catch (ArgumentException) { Fail("Function effect belongs to another workspace.", origin); continue; }
                if (!seen.Add(effect)) { Fail("Function effect metadata contains a duplicate type.", origin); }
                if (program.Analysis.Types.ClassifyConversion(effect, root, cancellation) == IntrinsicConversion.None)
                { Fail("Function effects must derive from std.core.Throwable.", origin); }
            }
        }

        private void CheckType(ResolvedType type, AstOrigin origin)
        {
            try { program.Analysis.Types.ValidateOwnership(type); }
            catch (ArgumentException) { Fail("Type belongs to another workspace.", origin); return; }
            if (!Supported(type)) { Fail("Type is outside the verified native subset: " + type.Name, origin); }
        }

        private void Statement(FirStatement statement, int loops)
        {
            if (!Enter(statement)) { return; }
            try
            {
                switch (statement)
                {
                    case FirBlock block: foreach (var child in block.Statements) { Statement(child, loops); } break;
                    case FirDeclare declaration:
                        var declared = Slot(declaration.Slot, declaration.Origin);
                        if (declared?.IsParameter == true) { Fail("A parameter cannot be redeclared.", declaration.Origin); }
                        if (!_declared.Add(declaration.Slot)) { Fail("A local slot has multiple declaration sites.", declaration.Origin); }
                        if (declaration.Initializer is not null) { Expression(declaration.Initializer); Exact(declaration.Initializer.Type, declared?.Type, declaration.Origin); }
                        break;
                    case FirStore store:
                        var stored = Slot(store.Slot, store.Origin);
                        if (stored?.Mutable != true) { Fail("Store requires a mutable slot.", store.Origin); }
                        Expression(store.Value); Exact(store.Value.Type, stored?.Type, store.Origin); break;
                    case FirEvaluate evaluation: Expression(evaluation.Value); break;
                    case FirReturn ret:
                        if (ret.Value is not null) { Expression(ret.Value); Exact(ret.Value.Type, _function.ReturnType, ret.Origin); }
                        else if (!IsEmpty(_function.ReturnType)) { Fail("Return is missing its value.", ret.Origin); }
                        break;
                    case FirIf conditional:
                        Expression(conditional.Condition); Boolean(conditional.Condition);
                        Statement(conditional.Then, loops); if (conditional.Else is not null) { Statement(conditional.Else, loops); } break;
                    case FirLoop loop:
                        if (loop.Initializer is not null and not (FirDeclare or FirStore or FirEvaluate)
                            || loop.Update is not null and not (FirDeclare or FirStore or FirEvaluate))
                        { Fail("Loop header requires a non-branching storage/evaluation statement.", loop.Origin); }
                        if (loop.Initializer is not null) { Statement(loop.Initializer, loops); }
                        if (loop.Condition is not null) { Expression(loop.Condition); Boolean(loop.Condition); }
                        Statement(loop.Body, loops + 1); if (loop.Update is not null) { Statement(loop.Update, loops + 1); } break;
                    case FirArrayForEach iteration:
                        ArrayForEach(iteration, loops); break;
                    case FirBreak or FirContinue: if (loops == 0) { Fail("Loop control lacks a target.", statement.Origin); } break;
                    default: Fail("Unrecognized FIR statement.", statement.Origin); break;
                }
            }
            finally { Leave(statement); }
        }

        private void ArrayForEach(FirArrayForEach iteration, int loops)
        {
            Expression(iteration.Array);

            var source = program.Analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
                .OfType<AstFor>().SingleOrDefault(loop => ReferenceEquals(loop.Origin.Element, iteration.Origin.Element));
            AstProperty? binding = source?.Binding;
            var sourceIterableExpression = source?.Iterable;
            if (source is null || source.Form != AstForForm.Each || binding is null || sourceIterableExpression is null)
            {
                Fail("Array iteration is not anchored to a source for-each binding.", iteration.Origin);
            }
            else
            {
                if (source.Body is null || !ReferenceEquals(source.Body.Origin.Element, iteration.Body.Origin.Element))
                { Fail("Array iteration body differs from its source loop body.", iteration.Body.Origin); }

                var sourceIterable = UnwrapGrouping(sourceIterableExpression);
                if (!ReferenceEquals(sourceIterable.Origin.Element, iteration.Array.Origin.Element))
                { Fail("Array iteration expression differs from its source iterable.", iteration.Array.Origin); }

                var sourceType = program.Analysis.GetExpression(sourceIterableExpression);
                if (sourceType is not { Status: BodyBindingStatus.Bound, Type: var sourceArrayType }
                    || sourceArrayType != iteration.Array.Type)
                { Fail("Array iteration type differs from source analysis.", iteration.Array.Origin); }

                var names = binding.BindingNames.ToArray();
                var locals = program.Analysis.Sources.Lexical.Scopes.Where(scope => ReferenceEquals(scope.Owner, source))
                    .SelectMany(scope => scope.Symbols)
                    .Where(local => local.Kind == LocalSymbolKind.Variable
                        && ReferenceEquals(local.Origin.Element, binding.Origin.Element)).ToArray();
                if (names.Length != 1 || string.IsNullOrWhiteSpace(names[0]) || names[0] == "_"
                    || binding.Type is not null || binding.Initializer is not null || binding.Delegate is not null
                    || locals is not [var local])
                {
                    Fail("Native array iteration requires one source-owned item local.", iteration.Origin);
                }
                else
                {
                    var item = Slot(iteration.ItemSlot, iteration.Origin);
                    if (item is not null)
                    {
                        if (item.IsParameter || item.Mode != AstPassingMode.Value)
                        { Fail("An array iteration item must use a local value slot.", iteration.Origin); }
                        if (item.Name != local.Name || item.Name != names[0]
                            || item.Mutable != local.IsMutable || item.Mutable != binding.IsMutable
                            || !ReferenceEquals(item.Origin.Element, binding.Origin.Element))
                        { Fail("Array iteration item slot identity or mutability differs from its source binding.", iteration.Origin); }
                        if (iteration.Array.Type.Kind != ResolvedTypeKind.Array || iteration.Array.Type.Arguments.Length != 1)
                        { Fail("Native array iteration requires a dynamic array with one element type.", iteration.Origin); }
                        else
                        {
                            var elementType = iteration.Array.Type.Arguments[0];
                            CheckType(elementType, iteration.Origin);
                            Exact(item.Type, elementType, iteration.Origin);
                        }
                        if (!_declared.Add(item.Id))
                        { Fail("An array iteration item slot has multiple declaration sites.", iteration.Origin); }
                    }
                }
            }

            Statement(iteration.Body, loops + 1);
        }

        private void Expression(FirExpression expression)
        {
            if (!Enter(expression)) { return; }
            try
            {
                CheckType(expression.Type, expression.Origin);
                switch (expression)
                {
                    case FirConstant constant: Constant(constant); break;
                    case FirRead read: Exact(read.Type, Slot(read.Slot, read.Origin)?.Type, read.Origin); break;
                    case FirUnary unary:
                        Expression(unary.Value); Exact(unary.Type, unary.Value.Type, unary.Origin);
                        if (unary.Operation is not ("+" or "-" or "~" or "!")) { Fail("Unrecognized unary operation.", unary.Origin); }
                        if (unary.Operation == "!") { Boolean(unary.Value); }
                        else if (unary.Type.Kind != ResolvedTypeKind.Primitive || unary.Type.Name == "bool") { Fail("Numeric unary operation needs a numeric type.", unary.Origin); }
                        if (unary.Operation == "~" && unary.Type.Name is "single" or "double") { Fail("Bitwise complement needs an integer.", unary.Origin); }
                        break;
                    case FirBinary binary:
                        Expression(binary.Left); Expression(binary.Right);
                        if (binary.Left.Type != binary.Right.Type && binary.Operation is not ("<<" or ">>")) { Fail("Binary operands require exact types.", binary.Origin); }
                        bool comparison = binary.Operation is "==" or "!=" or "<" or ">" or "<=" or ">=";
                        if (comparison) { Boolean(binary); }
                        else { Exact(binary.Type, binary.Left.Type, binary.Origin); }
                        if (binary.Operation is "&&" or "||") { Boolean(binary.Left); Boolean(binary.Right); }
                        if (binary.Operation is not ("+" or "-" or "*" or "/" or "%" or "&" or "|" or "^" or "<<" or ">>" or "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||"))
                        { Fail("Unrecognized binary operation.", binary.Origin); }
                        if (binary.Left.Type.Kind != ResolvedTypeKind.Primitive
                            && !(IsString(binary.Left.Type) && binary.Operation is "+" or "==" or "!=" or "<" or ">" or "<=" or ">="))
                        { Fail("This binary operation is not qualified for the operand type.", binary.Origin); }
                        if (binary.Operation is "<<" or ">>" && binary.Right.Type.Name != "int") { Fail("Shift count must be int.", binary.Origin); }
                        if (binary.Operation is "&" or "|" or "^" or "<<" or ">>" && binary.Left.Type.Name is "single" or "double")
                        { Fail("Bit operations require integers.", binary.Origin); }
                        if (binary.Left.Type.Name == "bool" && binary.Operation is not ("&&" or "||" or "&" or "|" or "^" or "==" or "!="))
                        { Fail("Invalid Boolean arithmetic.", binary.Origin); }
                        break;
                    case FirArrayLength length:
                        Expression(length.Array);
                        if (!(length.Array.Type.Kind == ResolvedTypeKind.Array || IsString(length.Array.Type)) || length.Type.Name != "int") { Fail("Invalid length signature.", length.Origin); } break;
                    case FirArrayIndex index:
                        Expression(index.Array); Expression(index.Index);
                        if (index.Array.Type.Kind != ResolvedTypeKind.Array || index.Index.Type.Name != "int" || index.Type != index.Array.Type.Arguments[0])
                        { Fail("Invalid array-index signature.", index.Origin); } break;
                    case FirArrayLiteral array:
                        if (array.Type.Kind != ResolvedTypeKind.Array) { Fail("Array literal requires a managed array type.", array.Origin); break; }
                        foreach (var element in array.Elements) { Expression(element); Exact(element.Type, array.Type.Arguments[0], element.Origin); }
                        break;
                    case FirCall call: CheckCall(call); break;
                    case FirIntrinsic intrinsic:
                        foreach (var argument in intrinsic.Arguments) { Expression(argument); }
                        if (!intrinsic.ParameterOrder.IsEmpty && (intrinsic.ParameterOrder.Length != intrinsic.Arguments.Length
                            || !intrinsic.ParameterOrder.Order().SequenceEqual(Enumerable.Range(0, intrinsic.Arguments.Length))))
                        { Fail("Invalid intrinsic argument order.", intrinsic.Origin); break; }
                        var ordered = intrinsic.ParameterOrder.IsEmpty ? intrinsic.Arguments : intrinsic.Arguments
                            .Zip(intrinsic.ParameterOrder).OrderBy(p => p.Second).Select(p => p.First).ToImmutableArray();
                        string signature = string.Join(",", ordered.Select(a => IsString(a.Type) ? "String" : IsEmpty(a.Type) ? "()" : a.Type.Name));
                        string output = IsString(intrinsic.Type) ? "String" : IsEmpty(intrinsic.Type) ? "()" : intrinsic.Type.Name;
                        bool valid = (intrinsic.Operation, signature, output) switch
                        {
                            (FirIntrinsicKind.StringSubstring, "String,int,int", "String") => true,
                            (FirIntrinsicKind.StringIndexOf, "String,String,int", "int") => true,
                            (FirIntrinsicKind.StringCompare, "String,String", "int") => true,
                            (FirIntrinsicKind.StringHash, "String", "int") => true,
                            (FirIntrinsicKind.GcCollect, "", "()") => true,
                            _ => false,
                        };
                        if (!valid) { Fail("Invalid native intrinsic signature.", intrinsic.Origin); }
                        break;
                    default: Fail("Unrecognized FIR expression.", expression.Origin); break;
                }
            }
            finally { Leave(expression); }
        }

        private void Constant(FirConstant constant)
        {
            try
            {
                if (constant.Type.Kind is not (ResolvedTypeKind.Primitive or ResolvedTypeKind.Tuple) && !IsString(constant.Type))
                { Fail("This type has no qualified literal representation.", constant.Origin); return; }
                if (IsEmpty(constant.Type)) { if (constant.Spelling != "()") { Fail("Invalid empty tuple constant.", constant.Origin); } return; }
                if (IsString(constant.Type)) { FirLiterals.DecodeUtf8(constant.Spelling); return; }
                if (constant.Type.Name == "bool")
                { if (constant.Spelling is not ("true" or "false")) { Fail("Invalid Boolean constant.", constant.Origin); } return; }
                if (constant.Type.Name is "single" or "double")
                {
                    double value = double.Parse(constant.Spelling.Replace("_", "", StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture);
                    if (!double.IsFinite(value) || constant.Type.Name == "single" && !float.IsFinite((float)value)) { Fail("Floating constant is out of range.", constant.Origin); }
                    return;
                }
                var integer = FirLiterals.ParseInteger(constant.Spelling);
                int width = constant.Type.Name switch { "sbyte" or "byte" => 8, "short" => 16, "int" or "uint" => 32, _ => 64 };
                bool unsigned = constant.Type.Name is "byte" or "uint" or "ulong";
                var bound = System.Numerics.BigInteger.One << (unsigned ? width : width - 1);
                if (integer < (unsigned ? 0 : -bound) || integer >= bound) { Fail("Integer constant is out of range.", constant.Origin); }
            }
            catch (Exception error) when (error is FormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
            { Fail("Malformed FIR literal.", constant.Origin); }
        }

        private void CheckCall(FirCall call)
        {
            if (call.Target < 0 || call.Target >= program.Functions.Length) { Fail("Call target does not belong to this program.", call.Origin); return; }
            var target = program.Functions[call.Target];
            var sourceBinding = program.Analysis.Calls.SingleOrDefault(binding =>
                ReferenceEquals(binding.Call.Origin.Element, call.Origin.Element));
            if (sourceBinding is null)
            {
                Fail("Call is not anchored to a resolved source call.", call.Origin);
            }
            else
            {
                var targetSource = program.Analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
                    .OfType<AstFunction>().SingleOrDefault(function => ReferenceEquals(function.Origin.Element, target.Origin.Element));
                if (targetSource is null || !ReferenceEquals(sourceBinding.Target.Origin.Element, targetSource.Origin.Element))
                { Fail("Call target differs from overload resolution.", call.Origin); }
                if (sourceBinding.ReturnType != call.Type)
                { Fail("Call result type differs from overload resolution.", call.Origin); }
                var selectedEffects = sourceBinding.Target.ThrownTypes
                    .Select(program.Analysis.Sources.Types.GetType).ToImmutableArray();
                if (!selectedEffects.SequenceEqual(target.Effects))
                { Fail("Call effect metadata differs from the selected source target.", call.Origin); }
                ValidateCallArguments(call, sourceBinding);
            }
            Exact(call.Type, target.ReturnType, call.Origin);
            if (target.RuntimeFunction == FirRuntimeFunction.None
                && (call.Arguments.Length != target.Parameters.Length || call.Arguments.Select(a => a.Parameter).Distinct().Count() != target.Parameters.Length))
            { Fail("Call argument mapping must fill every parameter exactly once.", call.Origin); }
            foreach (var argument in call.Arguments)
            {
                Expression(argument.Value);
                if (target.RuntimeFunction != FirRuntimeFunction.None)
                {
                    if (argument.Mode != AstPassingMode.Value || !(IsString(argument.Value.Type) || argument.Value.Type.Kind == ResolvedTypeKind.Primitive))
                    { Fail("Print adapters require scalar or String values.", call.Origin); }
                    continue;
                }
                if (argument.Parameter < 0 || argument.Parameter >= target.Parameters.Length) { Fail("Call parameter index is invalid.", call.Origin); continue; }
                var parameter = target.Parameters[argument.Parameter];
                Exact(argument.Value.Type, parameter.Type, call.Origin);
                if (argument.Mode != parameter.Mode) { Fail("Call passing mode mismatch.", call.Origin); }
                if (argument.Mode is AstPassingMode.Ref or AstPassingMode.Out
                    && (argument.Value is not FirRead read || Slot(read.Slot, read.Origin)?.Mutable != true))
                { Fail("Ref/out needs exact writable local storage.", call.Origin); }
            }
            foreach (var effect in target.Effects.Where(IsChecked))
            {
                if (!_function.Effects.Any(b => program.Analysis.Types.ClassifyConversion(effect, b, cancellation) != IntrinsicConversion.None))
                { Fail("Call exceeds the function's checked effect budget.", call.Origin); }
            }
        }

        private void ValidateCallArguments(FirCall call, BodyCallBinding sourceBinding)
        {
            if (sourceBinding.Arguments.Length != call.Arguments.Length)
            { Fail("FIR call argument count differs from source binding.", call.Origin); return; }
            bool runtimeAdapter = program.Functions[call.Target].RuntimeFunction != FirRuntimeFunction.None;
            var firArguments = new Dictionary<int, FirArgument>();
            if (!runtimeAdapter)
            {
                foreach (var argument in call.Arguments)
                {
                    if (!firArguments.TryAdd(argument.Parameter, argument))
                    { Fail("FIR call maps a parameter more than once.", call.Origin); }
                }
            }
            for (int index = 0; index < sourceBinding.Arguments.Length; index++)
            {
                var mapping = sourceBinding.Arguments[index];
                if (mapping.IsArrayArgument || mapping.IsSpread || mapping.IsExpandedVararg && !runtimeAdapter
                    || mapping.ArgumentIndex < 0 || mapping.ArgumentIndex >= sourceBinding.Call.Arguments.Length
                    || mapping.ParameterIndex < 0 || mapping.ParameterIndex >= sourceBinding.Target.Parameters.Length)
                { Fail("Native call does not support the resolved source argument mapping.", call.Origin); continue; }
                FirArgument? argument = runtimeAdapter ? call.Arguments[index]
                    : firArguments.GetValueOrDefault(mapping.ParameterIndex);
                if (argument is null || argument.Parameter != mapping.ParameterIndex)
                { Fail("FIR call omitted a source-bound argument.", call.Origin); continue; }
                var sourceArgument = sourceBinding.Call.Arguments[mapping.ArgumentIndex];
                if (sourceArgument.Value is not AstExpression sourceValue
                    || !ReferenceEquals(UnwrapGrouping(sourceValue).Origin.Element, argument.Value.Origin.Element))
                { Fail("FIR argument value differs from the source expression.", call.Origin); }
                var expectedMode = sourceArgument.Mode switch
                {
                    AstArgumentMode.Value => AstPassingMode.Value,
                    AstArgumentMode.Ref => AstPassingMode.Ref,
                    AstArgumentMode.Out or AstArgumentMode.OutVariable => AstPassingMode.Out,
                    _ => AstPassingMode.Vararg,
                };
                if (argument.Mode != expectedMode || mapping.IsTrailingLambda != sourceArgument.IsTrailingLambda)
                { Fail("FIR argument mode differs from the source call.", call.Origin); }
            }
        }

        private static AstExpression UnwrapGrouping(AstExpression expression)
        {
            while (expression is AstSequence { IsGrouping: true } grouping)
            {
                var elements = grouping.Elements.Take(2).ToArray();
                if (elements is not [AstExpression nested]) { break; }
                expression = nested;
            }
            return expression;
        }

        private bool IsChecked(ResolvedType type) => !program.Analysis.Types.Types.Any(t => t.Declaration is { ModuleName: "std.core", Name: "RuntimeException" }
            && program.Analysis.Types.ClassifyConversion(type, t, cancellation) != IntrinsicConversion.None);
        private FirSlot? Slot(int id, AstOrigin origin)
        { if (id < 0 || id >= _function.Slots.Length) { Fail("Slot is outside its function.", origin); return null; } return _function.Slots[id]; }
        private void Exact(ResolvedType actual, ResolvedType? expected, AstOrigin origin)
        { if (actual != expected && actual.Kind != ResolvedTypeKind.Bottom) { Fail("FIR type identity mismatch.", origin); } }
        private void Boolean(FirExpression value)
        { if (value.Type.Kind != ResolvedTypeKind.Primitive || value.Type.Name != "bool") { Fail("Condition/result must be bool.", value.Origin); } }
        private void Fail(string message, AstOrigin origin)
        { if (Errors.Count < 1000) { Errors.Add(new("SOL2402", message, origin.Source, origin.Span)); } }
        private bool Enter(FirNode node)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_depth >= 128 || !_active.Add(node)) { Fail("Cyclic or excessively nested FIR.", node.Origin); return false; }
            _depth++; return true;
        }
        private void Leave(FirNode node) { _depth--; _active.Remove(node); }
    }
}
