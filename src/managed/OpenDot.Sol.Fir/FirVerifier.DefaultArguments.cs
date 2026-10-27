// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Fir;

public static partial class FirVerifier
{
    private sealed partial class Checker
    {
        private sealed record DefaultCallParameter(int ParameterIndex, AstExpression Expression);

        private sealed record DefaultCallPlan(AstFunction Target,
            ImmutableArray<CallArgumentBinding> ExplicitArguments,
            ImmutableArray<DefaultCallParameter> Defaults,
            ImmutableArray<ResolvedType> TypeArguments,
            SpecializedBodyAnalysis? TargetSourceView)
        {
            internal DefaultCallArgumentContext? ForArgument(FirCall call, BodyCallBinding binding, int argumentIndex)
            {
                int defaultIndex = argumentIndex - ExplicitArguments.Length;
                return defaultIndex < 0 || defaultIndex >= Defaults.Length ? null
                    : new(this, call, binding, argumentIndex, Defaults[defaultIndex]);
            }
        }

        private sealed record DefaultCallArgumentContext(DefaultCallPlan Plan, FirCall Call,
            BodyCallBinding Binding, int ArgumentIndex, DefaultCallParameter Parameter);

        private readonly Dictionary<FirCall, DefaultCallPlan> _defaultCallPlans = new(ReferenceEqualityComparer.Instance);
        private DefaultCallArgumentContext? _defaultArgumentContext;

        private DefaultCallPlan? ValidateDefaultCallPlan(FirCall call, BodyCallBinding binding)
        {
            _defaultCallPlans.Remove(call);
            if (binding.DefaultParameters.IsDefaultOrEmpty) { return null; }
            if (binding.Target is not AstFunction function)
            { Fail("Native default arguments require a source function target.", call.Origin); return null; }

            var file = program.Analysis.Sources.Declarations.Files.SingleOrDefault(candidate =>
                candidate.Nodes.OfType<AstFunction>().Any(source => ReferenceEquals(source.Origin.Element, function.Origin.Element)));
            bool hasTypeOwner = file is not null && HasTypeOwner(file, function);
            if (file is null
                || hasTypeOwner && !GenericMethodSourcePolicy.IsSupportedGenericObjectMethod(function, program.Analysis.Sources)
                || HasFunctionOwner(file, function)
                || function.Parameters.Any(parameter => parameter.PassingMode == AstPassingMode.Vararg))
            { Fail("Native default arguments require a non-vararg module function or eligible stateless-object method.", call.Origin); return null; }

            var defaults = program.Analysis.Sources.Defaults.GetDefaults(function);
            if (!defaults.IsComplete || defaults.Parameters.Length != function.Parameters.Length
                || binding.ParameterTypes.Length != function.Parameters.Length
                || binding.Arguments.IsDefault || binding.DefaultParameters.IsDefault)
            { Fail("Default call metadata is incomplete for the selected source function.", call.Origin); return null; }

            var explicitArguments = binding.Arguments.OrderBy(mapping => mapping.ArgumentIndex).ToImmutableArray();
            if (explicitArguments.Select(mapping => mapping.ArgumentIndex).Distinct().Count() != explicitArguments.Length
                || explicitArguments.Any(mapping => mapping.ArgumentIndex < 0
                    || mapping.ArgumentIndex >= binding.Call.Arguments.Length
                    || mapping.ParameterIndex < 0 || mapping.ParameterIndex >= function.Parameters.Length
                    || mapping.IsArrayArgument || mapping.IsSpread || mapping.IsExpandedVararg || mapping.IsTrailingLambda))
            { Fail("Explicit arguments do not form a supported source-ordered default-call plan.", call.Origin); return null; }

            var defaultIndices = binding.DefaultParameters.Order().ToArray();
            if (defaultIndices.Distinct().Count() != binding.DefaultParameters.Length
                || defaultIndices.Any(index => index < 0 || index >= function.Parameters.Length)
                || binding.EmptyVarargParameters.Any())
            { Fail("Default parameter indices are duplicated or outside the native call plan.", call.Origin); return null; }

            var explicitlySupplied = explicitArguments.Select(mapping => mapping.ParameterIndex).ToHashSet();
            if (explicitlySupplied.Count != explicitArguments.Length
                || defaultIndices.Any(explicitlySupplied.Contains)
                || explicitArguments.Length + defaultIndices.Length != function.Parameters.Length
                || Enumerable.Range(0, function.Parameters.Length)
                    .Any(index => !explicitlySupplied.Contains(index) && !defaultIndices.Contains(index)))
            { Fail("Explicit and omitted parameters do not fill the target signature exactly once.", call.Origin); return null; }

            var defaultsForPlan = ImmutableArray.CreateBuilder<DefaultCallParameter>(defaultIndices.Length);
            foreach (int parameterIndex in defaultIndices)
            {
                var contract = defaults.Parameters[parameterIndex];
                var parameter = function.Parameters[parameterIndex];
                if (parameter.PassingMode == AstPassingMode.Vararg || contract is null
                    || contract.ParameterIndex != parameterIndex || !ReferenceEquals(contract.DeclaredBy, function)
                    || contract.Expression is not AstExpression expression
                    || parameter.DefaultValue is not AstExpression declaredDefault
                    || !ReferenceEquals(expression.Origin.Element, declaredDefault.Origin.Element))
                { Fail("Omitted default does not match the target parameter's own source default.", call.Origin); return null; }
                defaultsForPlan.Add(new(parameterIndex, expression));
            }

            if (call.Arguments.Length != explicitArguments.Length + defaultsForPlan.Count)
            { Fail("FIR argument count differs from explicit and omitted source arguments.", call.Origin); return null; }
            for (int index = 0; index < explicitArguments.Length; index++)
            {
                var mapping = explicitArguments[index];
                var argument = call.Arguments[index];
                if (argument.Parameter != mapping.ParameterIndex
                    || argument.Mode != function.Parameters[mapping.ParameterIndex].PassingMode)
                { Fail("FIR explicit arguments must retain source evaluation order.", argument.Value.Origin); }
            }
            for (int index = 0; index < defaultsForPlan.Count; index++)
            {
                var omitted = defaultsForPlan[index];
                var argument = call.Arguments[explicitArguments.Length + index];
                if (argument.Parameter != omitted.ParameterIndex || argument.Mode != AstPassingMode.Value)
                { Fail("FIR omitted defaults must follow explicit arguments in formal parameter order.", argument.Value.Origin); }
            }

            SpecializedBodyAnalysis? targetSourceView = null;
            var typeParameters = program.Analysis.Sources.Types.GetTypeParameters(function);
            var typeArguments = call.TypeArguments;
            if (typeParameters.Length != typeArguments.Length)
            { Fail("Default call type arguments differ from the target generic declaration.", call.Origin); return null; }
            if (!typeParameters.IsEmpty)
            {
                try
                {
                    targetSourceView = program.Analysis.GetGenericBody(function, typeArguments, cancellation);
                }
                catch (Exception error) when (error is ArgumentException or TypeSpecializationException)
                { Fail("Default source expressions could not be rebound for the selected closed call.", call.Origin); return null; }
                if (targetSourceView.Summary.Status != BodyBindingStatus.Bound
                    || !targetSourceView.UnverifiedNodes.IsEmpty
                    || targetSourceView.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                { Fail("Default source function lacks a complete specialized analysis proof.", call.Origin); return null; }
            }

            var plan = new DefaultCallPlan(function, explicitArguments, defaultsForPlan.ToImmutable(),
                typeArguments, targetSourceView);
            _defaultCallPlans.Add(call, plan);
            return plan;
        }

        private bool TryGetDefaultCallPlan(FirCall call, BodyCallBinding binding,
            out ImmutableArray<DefaultCallParameter> defaults)
        {
            if (_defaultCallPlans.TryGetValue(call, out var plan)
                && ReferenceEquals(plan.Target.Origin.Element, binding.Target.Origin.Element))
            {
                defaults = plan.Defaults;
                return true;
            }
            defaults = [];
            if (!binding.DefaultParameters.IsDefaultOrEmpty)
            { Fail("Default FIR call has no independently validated argument plan.", call.Origin); }
            return false;
        }

        private void CheckCallValueReference(FirCallValueReference reference)
        {
            var context = _defaultArgumentContext;
            if (context is null)
            { Fail("Call-value references are valid only inside an omitted default argument.", reference.Origin); return; }
            if (context.ArgumentIndex < context.Plan.ExplicitArguments.Length
                || context.ArgumentIndex >= context.Call.Arguments.Length
                || context.Call.Arguments[context.ArgumentIndex].Parameter != context.Parameter.ParameterIndex
                || context.Call.Arguments[context.ArgumentIndex].Mode != AstPassingMode.Value)
            { Fail("Call-value reference is outside the selected default argument.", reference.Origin); return; }

            var sourceAtom = context.Parameter.Expression.Descendants().Prepend(context.Parameter.Expression)
                .OfType<AstAtom>().SingleOrDefault(atom => ReferenceEquals(atom.Origin.Element, reference.Origin.Element));
            if (sourceAtom is null || sourceAtom.SyntaxKind != NodeKind.NameExpression)
            { Fail("Call-value reference has no matching source parameter name.", reference.Origin); return; }
            SameOrigin(reference, sourceAtom);
            if (!TryResolveDefaultParameter(context.Plan.Target, sourceAtom, out int parameterIndex))
            { Fail("Default name does not bind to one parameter of its target.", reference.Origin); return; }
            if (parameterIndex >= context.Parameter.ParameterIndex
                || context.Plan.Target.Parameters[parameterIndex].PassingMode != AstPassingMode.Value)
            { Fail("A default may reference only an earlier value parameter.", reference.Origin); }
            if (reference.EvaluationIndex < 0 || reference.EvaluationIndex >= context.ArgumentIndex)
            { Fail("Default call-value references must point to an earlier captured argument.", reference.Origin); return; }
            var captured = context.Call.Arguments[reference.EvaluationIndex];
            if (captured.Parameter != parameterIndex || captured.Mode != AstPassingMode.Value)
            { Fail("Default call-value reference does not select its earlier value argument.", reference.Origin); }

            var sourceBinding = context.Plan.TargetSourceView?.GetExpression(sourceAtom)
                ?? program.Analysis.GetExpression(sourceAtom);
            if (sourceBinding?.Type is null || sourceBinding.Status != BodyBindingStatus.Bound
                || Substitute(sourceBinding.Type) != reference.Type)
            { Fail("Call-value reference type differs from the substituted source parameter.", reference.Origin); }
        }

        private bool TryResolveDefaultParameter(AstFunction target, AstAtom source, out int parameterIndex)
        {
            parameterIndex = -1;
            var locals = program.Analysis.Sources.Lexical.Lookup(source, source.Spelling).Locals;
            if (locals.Length != 1 || locals[0].Kind != LocalSymbolKind.Parameter) { return false; }
            var matches = target.Parameters.Select((parameter, index) => (parameter, index))
                .Where(pair => ReferenceEquals(pair.parameter.NameOrigin.Element, locals[0].Origin.Element)).ToArray();
            if (matches.Length != 1) { return false; }
            parameterIndex = matches[0].index;
            return true;
        }

        private void MatchDefaultExpression(FirExpression actual, AstExpression source, FirCall call,
            BodyCallBinding binding, int defaultParameterIndex, int argumentIndex)
        {
            while (source is AstSequence { IsGrouping: true } grouping)
            {
                if (grouping.Elements.ToArray() is not [AstExpression nested]) { break; }
                source = nested;
            }
            if (!Enter(actual)) { return; }
            try
            {
                SameOrigin(actual, source);
                var expressionBinding = _defaultCallPlans.TryGetValue(call, out var plan)
                    ? plan.TargetSourceView?.GetExpression(source) ?? program.Analysis.GetExpression(source)
                    : program.Analysis.GetExpression(source);
                ResolvedType? expectedType = expressionBinding?.Type;
                if (expectedType is null || expressionBinding!.Status != BodyBindingStatus.Bound
                    || !expressionBinding.Effects.IsEmpty || !Supported(expectedType)
                    || Substitute(expectedType) != actual.Type)
                { Fail("Default FIR expression type or effects differ from its specialized source binding.", actual.Origin); }

                switch (source)
                {
                    case AstAtom { SyntaxKind: NodeKind.LiteralExpression } literal when actual is FirConstant constant:
                        if (constant.Spelling != literal.Spelling)
                        { Fail("Default FIR literal differs from its source token.", actual.Origin); }
                        break;
                    case AstAtom { SyntaxKind: NodeKind.NameExpression } name when actual is FirCallValueReference reference:
                        MatchDefaultReference(reference, name, call, binding, defaultParameterIndex, argumentIndex);
                        break;
                    case AstOperation { Operation: "-", Operands: var operands }
                        when operands.ToArray() is [AstAtom { SyntaxKind: NodeKind.LiteralExpression } literal]
                            && actual is FirConstant negative:
                        if (negative.Spelling != "-" + literal.Spelling)
                        { Fail("Default FIR negative literal differs from its source token.", actual.Origin); }
                        break;
                    case AstOperation operation when actual is FirUnary unary
                        && operation.TargetType is null && operation.Operands.ToArray() is [AstExpression operand]:
                        if (unary.Operation != operation.Operation)
                        { Fail("Default FIR unary operator differs from its source.", actual.Origin); }
                        MatchDefaultExpression(unary.Value, operand, call, binding, defaultParameterIndex, argumentIndex);
                        break;
                    case AstOperation operation when actual is FirBinary binary
                        && operation.TargetType is null && operation.Operands.ToArray() is [AstExpression left, AstExpression right]:
                        if (binary.Operation != operation.Operation)
                        { Fail("Default FIR binary operator differs from its source.", actual.Origin); }
                        MatchDefaultExpression(binary.Left, left, call, binding, defaultParameterIndex, argumentIndex);
                        MatchDefaultExpression(binary.Right, right, call, binding, defaultParameterIndex, argumentIndex);
                        break;
                    default:
                        Fail("Default FIR node shape differs from the supported source expression.", actual.Origin);
                        break;
                }
            }
            finally { Leave(actual); }
        }

        private void MatchDefaultReference(FirCallValueReference reference, AstAtom source, FirCall call,
            BodyCallBinding binding, int defaultParameterIndex, int argumentIndex)
        {
            if (!_defaultCallPlans.TryGetValue(call, out var plan)
                || argumentIndex < plan.ExplicitArguments.Length
                || argumentIndex >= call.Arguments.Length
                || call.Arguments[argumentIndex].Parameter != defaultParameterIndex
                || call.Arguments[argumentIndex].Mode != AstPassingMode.Value)
            { Fail("Default FIR reference is not attached to its omitted target parameter.", reference.Origin); return; }
            if (!TryResolveDefaultParameter(plan.Target, source, out int parameterIndex)
                || parameterIndex >= defaultParameterIndex
                || plan.Target.Parameters[parameterIndex].PassingMode != AstPassingMode.Value)
            { Fail("Default source name must identify an earlier value parameter.", reference.Origin); return; }
            if (reference.EvaluationIndex < 0 || reference.EvaluationIndex >= argumentIndex)
            { Fail("Default reference must read an earlier captured call argument.", reference.Origin); return; }
            var captured = call.Arguments[reference.EvaluationIndex];
            if (captured.Parameter != parameterIndex || captured.Mode != AstPassingMode.Value)
            { Fail("Default reference points to a different or non-value argument.", reference.Origin); }
            var sourceBinding = plan.TargetSourceView?.GetExpression(source) ?? program.Analysis.GetExpression(source);
            if (sourceBinding?.Type is null || sourceBinding.Status != BodyBindingStatus.Bound
                || Substitute(sourceBinding.Type) != reference.Type)
            { Fail("Default reference type differs from the selected generic substitution.", reference.Origin); }
        }

        private static bool HasFunctionOwner(AstSnapshot file, AstFunction function)
        {
            for (var parent = file.GetParent(function); parent is not null; parent = file.GetParent(parent))
            { if (parent is AstFunction) { return true; } }
            return false;
        }
    }
}
