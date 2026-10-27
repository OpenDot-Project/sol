// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Fir;

public sealed record FirVerification(VerifiedFirProgram? Program, ImmutableArray<Diagnostic> Diagnostics);

/// <summary>Checks the closed native subset, exact type/slot/call identity, and CFG assignment proof.</summary>
public static partial class FirVerifier
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
    internal static bool Supported(ResolvedType type)
    {
        var pending = new Stack<(ResolvedType Type, int Depth, bool AllowsVoid)>();
        pending.Push((type, 0, true));
        while (pending.TryPop(out var current))
        {
            if (current.Depth > 128) { return false; }
            if (IsEmpty(current.Type) || current.Type.Kind == ResolvedTypeKind.Bottom)
            {
                if (current.AllowsVoid) { continue; }
                return false;
            }
            if (current.Type.Kind == ResolvedTypeKind.Primitive)
            {
                if (current.Type.Name is "half" or "quad") { return false; }
                continue;
            }
            if (IsString(current.Type)) { continue; }
            if (current.Type.Kind == ResolvedTypeKind.Array && current.Type.Arguments.Length == 1)
            {
                pending.Push((current.Type.Arguments[0], current.Depth + 1, false));
                continue;
            }
            if (current.Type.Kind == ResolvedTypeKind.Function && current.Type.Arguments.Length == current.Type.ParameterModes.Length + 1)
            {
                for (int index = 0; index < current.Type.ParameterModes.Length; index++)
                { pending.Push((current.Type.Arguments[index], current.Depth + 1, false)); }
                pending.Push((current.Type.Arguments[^1], current.Depth + 1, true));
                continue;
            }
            return false;
        }
        return true;
    }

    private sealed partial class Checker(FirProgram program, CancellationToken cancellation)
    {
        internal List<Diagnostic> Errors { get; } = [];
        internal List<FirFlowGraph> Graphs { get; } = [];
        private FirFunction _function = null!;
        private SpecializedBodyAnalysis? _sourceView;
        private Dictionary<ResolvedType, ResolvedType> _substitution = [];
        private readonly HashSet<FirNode> _active = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<int> _declared = [];
        private readonly HashSet<(object Source, string Arguments)> _genericInstances = [];
        private readonly HashSet<int> _genericInstanceRoots = [];
        private readonly Dictionary<int, HashSet<int>> _genericInstanceEdges = [];
        private readonly Dictionary<object, ExpressionBinding> _sourceExpressions = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, AstAssignment> _sourceAssignments = new(ReferenceEqualityComparer.Instance);
        private int _depth;

        internal void Check()
        {
            foreach (var assignment in program.Analysis.Sources.Declarations.Files
                .SelectMany(file => file.Nodes).OfType<AstAssignment>())
            { _sourceAssignments.TryAdd(assignment.Origin.Element, assignment); }
            if (program.IsLibrary ? program.Entry != -1 : program.Entry < 0 || program.Entry >= program.Functions.Length)
            { throw new ArgumentException("FIR entry index is invalid.", nameof(program)); }
            for (int index = 0; index < program.Functions.Length; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                _function = program.Functions[index];
                _substitution = [];
                _sourceView = null;
                _sourceExpressions.Clear();
                _defaultCallPlans.Clear();
                _defaultArgumentContext = null;
                _declared.Clear();
                if (_function.Id != index) { Fail("Function identity/index mismatch.", _function.Origin); }
                CheckExport();
                CheckSourceContract();
                foreach (var binding in _sourceView?.Expressions ?? program.Analysis.Expressions)
                { _sourceExpressions.TryAdd(binding.Expression.Origin.Element, binding); }
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
            CheckSourceCorrespondence();
                if (_function.Slots.Any(s => !s.IsParameter && !_declared.Contains(s.Id))) { Fail("Every local slot requires a declaration.", _function.Origin); }
                if (Errors.Count != 0) { continue; }
                var flow = FirFlowGraph.Create(_function, program.Functions, cancellation);
                Graphs.Add(flow);
                Errors.AddRange(flow.Diagnostics);
            }
            CheckSpecializationReachability();
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
            var genericParameters = program.Analysis.Sources.Types.GetTypeParameters(source);
            CheckObjectMethodBoundary(source, genericParameters.Length);

            if (genericParameters.Length != _function.SpecializationArguments.Length)
            {
                if (genericParameters.Length != 0 || !_function.SpecializationArguments.IsEmpty)
                { Fail("Generic function specialization arity differs from its declaration.", _function.Origin); }
            }
            else if (genericParameters.Length != 0)
            {
                for (int index = 0; index < genericParameters.Length; index++)
                {
                    var argument = _function.SpecializationArguments[index];
                    if (argument is null)
                    { Fail("Generic FIR specialization contains a null type argument.", _function.Origin); continue; }
                    if (!TryValidateOwnership(argument, _function.Origin)) { continue; }
                    CheckType(argument, _function.Origin);
                    if (ContainsParameter(argument)) { Fail("A generic FIR instance must have closed type arguments.", _function.Origin); }
                    _substitution.Add(genericParameters[index], argument);
                }
                if (!_function.SpecializationArguments.Any(argument => argument is null))
                {
                    string argumentKey = string.Join(",", _function.SpecializationArguments
                        .Select(argument => argument.Id.ToString(CultureInfo.InvariantCulture)));
                    if (!_genericInstances.Add((source.Origin.Element, argumentKey)))
                    { Fail("A generic FIR specialization identity is duplicated.", _function.Origin); }
                    try
                    {
                        var constraints = program.Analysis.Types.CheckConstraints(source, _function.SpecializationArguments, cancellation);
                        if (constraints.Status != ConstraintStatus.Satisfied)
                        { Fail("Generic FIR specialization constraints are not satisfied.", _function.Origin); }
                    }
                    catch (ArgumentException)
                    { Fail("Generic FIR specialization arguments belong to another type workspace.", _function.Origin); }
                }
            }
            else if (_function.SpecializationArguments.IsDefault)
            { Fail("FIR specialization metadata must be initialized.", _function.Origin); }

            if (genericParameters.Length != 0 && !_function.SpecializationArguments.IsDefault
                && _function.SpecializationArguments.Length == genericParameters.Length
                && !_function.SpecializationArguments.Any(argument => argument is null))
            {
                try
                {
                    _sourceView = program.Analysis.GetGenericBody(source, _function.SpecializationArguments, cancellation);
                    if (_sourceView.Summary.Status != BodyBindingStatus.Bound
                        || !_sourceView.UnverifiedNodes.IsEmpty
                        || _sourceView.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                    { Fail("Specialized function body lacks a complete source analysis proof.", _function.Origin); }
                }
                catch (Exception error) when (error is ArgumentException or TypeSpecializationException)
                { Fail("Generic source body could not be rebound for this FIR instance.", _function.Origin); }
            }

            var summary = _sourceView?.Summary ?? program.Analysis.GetBody(source);
            if (summary is null || summary.Status != BodyBindingStatus.Bound)
            { Fail("Function body lacks a completed source analysis summary.", _function.Origin); }
            else if (summary.ReturnType is null || Substitute(summary.ReturnType) != _function.ReturnType)
            { Fail("Function result type differs from source analysis.", _function.Origin); }

            var declaredEffects = source.ThrownTypes.Select(program.Analysis.Sources.Types.GetType).Select(Substitute).ToImmutableArray();
            if (_function.Effects.IsDefault || !declaredEffects.SequenceEqual(_function.Effects))
            { Fail("Function effect metadata differs from its source declaration.", _function.Origin); }
            ValidateEffects(_function.Effects, _function.Origin);
            if (summary is not null)
            {
                foreach (var escaping in summary.EscapingEffects.Select(Substitute).Where(IsChecked))
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
                var expectedParameterType = sourceParameter.Type is null ? null
                    : Substitute(program.Analysis.Sources.Types.GetType(sourceParameter.Type));
                var expectedParameterMode = sourceParameter.PassingMode;
                if (sourceParameter.PassingMode == AstPassingMode.Vararg)
                {
                    if (expectedParameterType is not null) { expectedParameterType = program.Analysis.Types.GetArray(expectedParameterType); }
                    expectedParameterMode = AstPassingMode.Value;
                }
                if (sourceParameter.Name != firParameter.Name || sourceParameter.Type is null
                    || expectedParameterType != firParameter.Type || expectedParameterMode != firParameter.Mode)
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
            if (!TryValidateOwnership(type, origin)) { return; }
            if (!Supported(type)) { Fail("Type is outside the verified native subset: " + type.Kind + ":" + type.Name, origin); }
            ValidateNestedFunctionEffects(type, origin);
        }

        private bool TryValidateOwnership(ResolvedType type, AstOrigin origin)
        {
            try { program.Analysis.Types.ValidateOwnership(type); return true; }
            catch (ArgumentException) { Fail("Type belongs to another workspace.", origin); return false; }
        }

        private ResolvedType Substitute(ResolvedType type)
        {
            if (_substitution.Count == 0) { return type; }
            try { return program.Analysis.Types.Substitute(type, _substitution, cancellation); }
            catch (Exception error) when (error is ArgumentException or TypeSpecializationException)
            { Fail("Source type could not be specialized for the verified FIR instance.", _function.Origin); return type; }
        }

        private ExpressionBinding? GetSourceExpression(AstExpression expression)
            => _sourceView is { } sourceView ? sourceView.GetExpression(expression) : program.Analysis.GetExpression(expression);

        private BodyCallBinding? GetSourceCall(AstCall call)
            => GetSourceCall(call.Origin.Element);

        private BodyCallBinding? GetSourceCall(object sourceOrigin)
            => _sourceView is { } sourceView
                ? sourceView.Calls.SingleOrDefault(binding => ReferenceEquals(binding.Call.Origin.Element, sourceOrigin))
                : program.Analysis.Calls.SingleOrDefault(binding => ReferenceEquals(binding.Call.Origin.Element, sourceOrigin));

        private BodyOperatorBinding? GetSourceOperator(object sourceOrigin)
        {
            var bindings = _sourceView is { } sourceView
                ? sourceView.OperatorBindings : program.Analysis.OperatorBindings;
            return bindings.SingleOrDefault(binding => ReferenceEquals(binding.Site.Origin.Element, sourceOrigin));
        }

        private ResolvedType SubstituteCallTargetType(ResolvedType type, BodyCallBinding binding,
            ImmutableArray<ResolvedType> typeArguments)
        {
            var parameters = program.Analysis.Sources.Types.GetTypeParameters(binding.Target);
            if (parameters.Length != typeArguments.Length) { return Substitute(type); }
            var substitutions = new Dictionary<ResolvedType, ResolvedType>();
            for (int index = 0; index < parameters.Length; index++) { substitutions.Add(parameters[index], typeArguments[index]); }
            if (substitutions.Count == 0) { return Substitute(type); }
            try
            {
                var callerSubstitution = program.Analysis.Types.Substitute(type, _substitution, cancellation);
                return program.Analysis.Types.Substitute(callerSubstitution, substitutions, cancellation);
            }
            catch (Exception error) when (error is ArgumentException or TypeSpecializationException)
            { Fail("Selected generic call effects could not be specialized.", binding.Call.Origin); return type; }
        }

        private static bool ContainsParameter(ResolvedType type)
        {
            var pending = new Stack<ResolvedType>();
            var seen = new HashSet<ResolvedType>();
            pending.Push(type);
            while (pending.TryPop(out var current))
            {
                if (!seen.Add(current)) { continue; }
                if (current.Kind == ResolvedTypeKind.Parameter) { return true; }
                foreach (var argument in current.Arguments) { pending.Push(argument); }
                foreach (var effect in current.Effects) { pending.Push(effect); }
            }
            return false;
        }

        private void CheckSpecializationReachability()
        {
            var pending = new Queue<int>(_genericInstanceRoots);
            var reachable = new HashSet<int>();
            while (pending.TryDequeue(out int current))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!reachable.Add(current)) { continue; }
                if (_genericInstanceEdges.TryGetValue(current, out var nested))
                { foreach (int target in nested) { pending.Enqueue(target); } }
            }
            foreach (var function in program.Functions.Where(function => !function.SpecializationArguments.IsEmpty))
            {
                if (!reachable.Contains(function.Id))
                { Fail("Generic FIR instance is not reachable from a nongeneric source function.", function.Origin); }
            }
            CheckGenericMethodOwnerInstanceGraph();
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

                var sourceType = GetSourceExpression(sourceIterableExpression);
                if (sourceType is not { Status: BodyBindingStatus.Bound, Type: { } sourceArrayType }
                    || Substitute(sourceArrayType) != iteration.Array.Type)
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
                bool syntheticCompoundAssignment = expression is FirBinary compound
                    && _sourceAssignments.TryGetValue(expression.Origin.Element, out var assignment)
                    && assignment.Location is { } location
                    && assignment.Value is { } value
                    && assignment.Operation.Length > 1
                    && assignment.Operation[^1] == '='
                    && compound.Operation == assignment.Operation[..^1]
                    && ReferenceEquals(compound.Left.Origin.Element, location.Origin.Element)
                    && ReferenceEquals(compound.Right.Origin.Element, value.Origin.Element);
                bool syntheticVarargArray = expression is FirArrayLiteral packed && IsSyntheticVarargArray(packed);
                bool sourceBoundDefaultArgument = _defaultArgumentContext is not null;
                if (!syntheticCompoundAssignment && !syntheticVarargArray && !sourceBoundDefaultArgument
                    && (!_sourceExpressions.TryGetValue(expression.Origin.Element, out var sourceExpression)
                    || sourceExpression.Status != BodyBindingStatus.Bound || sourceExpression.Type is null
                    || Substitute(sourceExpression.Type) != expression.Type))
                { Fail("FIR expression type or source binding differs from the selected generic instance.", expression.Origin); }
                switch (expression)
                {
                    case FirConstant constant: Constant(constant); break;
                    case FirRead read: Exact(read.Type, Slot(read.Slot, read.Origin)?.Type, read.Origin); break;
                    case FirFunctionReference reference: CheckFunctionReference(reference); break;
                    case FirCallValueReference reference: CheckCallValueReference(reference); break;
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
                    case FirIndirectCall call: CheckIndirectCall(call); break;
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
            var sourceBinding = GetSourceCall(call.Origin.Element);
            var operatorBinding = sourceBinding is null ? GetSourceOperator(call.Origin.Element) : null;
            DefaultCallPlan? defaultPlan = null;
            if (sourceBinding is null && operatorBinding is null)
            {
                Fail("Call is not anchored to a resolved source call.", call.Origin);
            }
            else if (sourceBinding is { } selectedCall)
            {
                var targetSource = program.Analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
                    .OfType<AstFunction>().SingleOrDefault(function => ReferenceEquals(function.Origin.Element, target.Origin.Element));
                if (targetSource is null || !ReferenceEquals(selectedCall.Target.Origin.Element, targetSource.Origin.Element))
                { Fail("Call target differs from overload resolution.", call.Origin); }
                var selectedTypeArguments = selectedCall.TypeArguments.Select(Substitute).ToImmutableArray();
                if (call.TypeArguments.IsDefault || !selectedTypeArguments.SequenceEqual(call.TypeArguments))
                { Fail("Call specialization arguments differ from source overload resolution.", call.Origin); }
                if (target.SpecializationArguments.IsDefault || !selectedTypeArguments.SequenceEqual(target.SpecializationArguments))
                { Fail("Call target is not the selected closed generic instance.", call.Origin); }
                if (!target.SpecializationArguments.IsEmpty)
                {
                    if (_function.SpecializationArguments.IsEmpty) { _genericInstanceRoots.Add(target.Id); }
                    else
                    {
                        if (!_genericInstanceEdges.TryGetValue(_function.Id, out var nested))
                        { nested = []; _genericInstanceEdges.Add(_function.Id, nested); }
                        nested.Add(target.Id);
                    }
                }
                if (selectedCall.ReturnType is null || Substitute(selectedCall.ReturnType) != call.Type)
                { Fail("Call result type differs from overload resolution.", call.Origin); }
                if (target.RuntimeFunction == FirRuntimeFunction.None
                    && selectedCall.ParameterTypes.Length != target.Parameters.Length)
                { Fail("Call parameter signature differs from the selected specialization.", call.Origin); }
                else if (target.RuntimeFunction == FirRuntimeFunction.None)
                {
                    for (int index = 0; index < selectedCall.ParameterTypes.Length; index++)
                    {
                        var expectedParameterType = Substitute(selectedCall.ParameterTypes[index]);
                        if (selectedCall.Target.Parameters[index].PassingMode == AstPassingMode.Vararg)
                        { expectedParameterType = program.Analysis.Types.GetArray(expectedParameterType); }
                        if (expectedParameterType != target.Parameters[index].Type)
                        { Fail("Call parameter type differs from overload resolution.", call.Origin); }
                    }
                }
                var selectedEffects = selectedCall.Target.ThrownTypes
                    .Select(program.Analysis.Sources.Types.GetType)
                    .Select(effect => SubstituteCallTargetType(effect, selectedCall, selectedTypeArguments)).ToImmutableArray();
                if (!selectedEffects.SequenceEqual(target.Effects))
                { Fail("Call effect metadata differs from the selected source target.", call.Origin); }
                defaultPlan = ValidateCallArguments(call, selectedCall);
            }
            else if (operatorBinding is { } selectedOperator)
            {
                var targetSource = program.Analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
                    .OfType<AstFunction>().SingleOrDefault(function => ReferenceEquals(function.Origin.Element, target.Origin.Element));
                if (targetSource is null || !ReferenceEquals(selectedOperator.Target.Origin.Element, targetSource.Origin.Element))
                { Fail("Operator call target differs from the selected source operator.", call.Origin); }
                if (!selectedOperator.TypeArguments.IsEmpty || !call.TypeArguments.IsEmpty
                    || !target.SpecializationArguments.IsEmpty)
                { Fail("This FIR operator call must target a nongeneric operator declaration.", call.Origin); }
                if (selectedOperator.ReturnType is null || Substitute(selectedOperator.ReturnType) != call.Type)
                { Fail("Operator call result type differs from overload resolution.", call.Origin); }
                if (selectedOperator.ParameterTypes.Length != target.Parameters.Length)
                { Fail("Operator call parameter signature differs from overload resolution.", call.Origin); }
                for (int index = 0; index < Math.Min(selectedOperator.ParameterTypes.Length, target.Parameters.Length); index++)
                {
                    if (target.Parameters[index].Mode != AstPassingMode.Value
                        || Substitute(selectedOperator.ParameterTypes[index]) != target.Parameters[index].Type)
                    { Fail("Operator call parameter type or mode differs from overload resolution.", call.Origin); }
                }
                if (!selectedOperator.Effects.Select(Substitute).SequenceEqual(target.Effects))
                { Fail("Operator call effects differ from the selected source operator.", call.Origin); }
            }
            Exact(call.Type, target.ReturnType, call.Origin);
            if (target.RuntimeFunction == FirRuntimeFunction.None
                && (call.Arguments.Length != target.Parameters.Length || call.Arguments.Select(a => a.Parameter).Distinct().Count() != target.Parameters.Length))
            { Fail("Call argument mapping must fill every parameter exactly once.", call.Origin); }
            for (int argumentIndex = 0; argumentIndex < call.Arguments.Length; argumentIndex++)
            {
                var argument = call.Arguments[argumentIndex];
                var previousDefaultArgument = _defaultArgumentContext;
                _defaultArgumentContext = sourceBinding is null || defaultPlan is null
                    ? null : defaultPlan.ForArgument(call, sourceBinding, argumentIndex);
                try
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
                finally { _defaultArgumentContext = previousDefaultArgument; }
            }
            foreach (var effect in target.Effects.Where(IsChecked))
            {
                if (!_function.Effects.Any(b => program.Analysis.Types.ClassifyConversion(effect, b, cancellation) != IntrinsicConversion.None))
                { Fail("Call exceeds the function's checked effect budget.", call.Origin); }
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
