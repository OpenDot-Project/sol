// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Fir;

internal sealed partial class FirBuilder
{
    // While an omitted default is lowered, parameter names refer to already evaluated values in
    // the containing FirCall argument plan, rather than to caller locals or re-evaluated syntax.
    private Dictionary<LocalSymbol, int>? _callValueReferences;

    private FirExpression CallWithDefaults(AstCall call, BodyCallBinding selected, int target,
        ImmutableArray<ResolvedType> typeArguments, ResolvedType resultType)
    {
        if (selected.Target is not AstFunction function
            || Ancestors(function).Any(parent => parent is AstFunction)
            || Ancestors(function).Any(parent => parent is AstTypeDeclaration)
                && !GenericMethodSourcePolicy.IsSupportedGenericObjectMethod(function, _analysis.Sources)
            || function.Parameters.Any(parameter => parameter.PassingMode == AstPassingMode.Vararg))
        { return Reject(call, "Default lowering is limited to non-vararg module functions and eligible stateless-object methods"); }

        var locals = ParameterLocals(function);
        var parameterOrdinals = locals.ToDictionary(pair => pair.Value, pair => pair.Key);
        var defaults = _analysis.Sources.Defaults.GetDefaults(function);
        var defaultParameters = selected.DefaultParameters.Order().ToArray();
        if (!defaults.IsComplete || defaultParameters.Length == 0
            || defaultParameters.Distinct().Count() != defaultParameters.Length
            || defaultParameters.Any(index => index < 0 || index >= function.Parameters.Length
                || defaults.Parameters[index] is null || function.Parameters[index].PassingMode == AstPassingMode.Vararg))
        { return Reject(call, "Default mapping is incomplete or outside the native subset"); }

        // Ref/out formals are aliases, not values captured at argument-evaluation time. Until FIR
        // models that alias lifetime in a call plan, defaults may follow only value parameters.
        foreach (int defaultIndex in defaultParameters)
        {
            if (function.Parameters.Take(defaultIndex).Any(parameter => parameter.PassingMode != AstPassingMode.Value))
            { return Reject(call, "Default expressions cannot follow ref/out parameters in the native subset"); }
        }

        var arguments = ImmutableArray.CreateBuilder<FirArgument>();
        var evaluationByParameter = new Dictionary<int, int>();
        foreach (var mapping in selected.Arguments.OrderBy(mapping => mapping.ArgumentIndex))
        {
            if (mapping.ArgumentIndex < 0 || mapping.ArgumentIndex >= call.Arguments.Length
                || mapping.ParameterIndex < 0 || mapping.ParameterIndex >= function.Parameters.Length
                || mapping.IsArrayArgument || mapping.IsSpread || mapping.IsExpandedVararg
                || mapping.IsTrailingLambda || evaluationByParameter.ContainsKey(mapping.ParameterIndex))
            { return Reject(call, "Explicit argument mapping is outside the default-lowering subset"); }
            var sourceArgument = call.Arguments[mapping.ArgumentIndex];
            if (sourceArgument.Value is not AstExpression sourceValue)
            { return Reject(call, "Explicit default-call argument has no expression"); }
            var parameter = function.Parameters[mapping.ParameterIndex];
            if (parameter.PassingMode is not (AstPassingMode.Value or AstPassingMode.Ref or AstPassingMode.Out))
            { return Reject(call, "Explicit default-call argument mode is outside the native subset"); }
            int evaluationIndex = arguments.Count;
            arguments.Add(new(mapping.ParameterIndex, parameter.PassingMode, Expression(sourceValue)));
            evaluationByParameter.Add(mapping.ParameterIndex, evaluationIndex);
        }

        var previousSubstitution = _typeSubstitution;
        var previousReferences = _callValueReferences;
        var previousSpecializedBody = _specializedBody;
        try
        {
            _typeSubstitution = CreateSubstitution(function, typeArguments);
            _specializedBody = function.TypeParameters.Any()
                ? _analysis.GetGenericBody(function, typeArguments, _cancellation) : null;
            foreach (int parameterIndex in defaultParameters)
            {
                if (evaluationByParameter.ContainsKey(parameterIndex))
                { return Reject(call, "A supplied argument is also marked as defaulted"); }
                var contract = defaults.Parameters[parameterIndex]!;
                if (!ReferenceEquals(contract.DeclaredBy, function) || contract.Expression is not AstExpression expression
                    || !IsSupportedDefaultExpression(expression, parameterIndex, parameterOrdinals))
                { return Reject(contract.Expression, "Default expression shape"); }

                var references = new Dictionary<LocalSymbol, int>();
                for (int earlier = 0; earlier < parameterIndex; earlier++)
                {
                    if (!evaluationByParameter.TryGetValue(earlier, out int earlierEvaluation)
                        || function.Parameters[earlier].PassingMode != AstPassingMode.Value
                        || !locals.TryGetValue(earlier, out var local))
                    { return Reject(expression, "An earlier default parameter has no captured value"); }
                    references.Add(local, earlierEvaluation);
                }
                _callValueReferences = references;
                var value = Expression(expression);
                var expected = Specialize(selected.ParameterTypes[parameterIndex]);
                if (value.Type is null || !FirVerifier.IsAssignable(value.Type, expected))
                { return Reject(expression, "Default expression requires an unsupported native conversion"); }
                int evaluationIndex = arguments.Count;
                arguments.Add(new(parameterIndex, AstPassingMode.Value, value));
                evaluationByParameter.Add(parameterIndex, evaluationIndex);
            }
        }
        finally
        {
            _typeSubstitution = previousSubstitution;
            _callValueReferences = previousReferences;
            _specializedBody = previousSpecializedBody;
        }

        if (evaluationByParameter.Count != function.Parameters.Length
            || Enumerable.Range(0, function.Parameters.Length).Any(index => !evaluationByParameter.ContainsKey(index)))
        { return Reject(call, "Native call plan does not fill every formal exactly once"); }
        return new FirCall(target, arguments.ToImmutable(), resultType, call.Origin)
        { TypeArguments = function.TypeParameters.Any() ? typeArguments : [] };
    }

    private bool IsSupportedDefaultExpression(AstExpression expression, int parameterIndex,
        IReadOnlyDictionary<LocalSymbol, int> parameterOrdinals)
    {
        var binding = BoundExpression(expression);
        if (binding is not { Status: BodyBindingStatus.Bound, Type: not null, Effects.IsEmpty: true }) { return false; }
        var type = Specialize(binding.Type);
        if (!FirVerifier.Supported(type)) { return false; }
        switch (expression)
        {
            case AstAtom { SyntaxKind: NodeKind.LiteralExpression }:
                return type.Kind == ResolvedTypeKind.Primitive || FirVerifier.IsString(type)
                    || FirVerifier.IsNullSentinel(type) || FirVerifier.IsNullableFunction(type);
            case AstAtom { SyntaxKind: NodeKind.NameExpression } atom:
                var matches = _analysis.Sources.Lexical.Lookup(atom, atom.Spelling).Locals;
                return matches.Length == 1 && parameterOrdinals.TryGetValue(matches[0], out int ordinal)
                    && ordinal < parameterIndex && matches[0].Kind == LocalSymbolKind.Parameter
                    && type == Specialize(BoundExpression(atom)!.Type!);
            case AstSequence { IsGrouping: true } grouping:
                return grouping.Elements.OfType<AstExpression>().ToArray() is [var inner]
                    && IsSupportedDefaultExpression(inner, parameterIndex, parameterOrdinals);
            case AstOperation operation:
                if (operation.TargetType is not null || operation.Operation is "++" or "--" or "!!" or "?:"
                    || binding.Type.Kind != ResolvedTypeKind.Primitive || type.Kind != ResolvedTypeKind.Primitive) { return false; }
                var operands = operation.Operands.ToArray();
                if (operands.Length is not (1 or 2)) { return false; }
                return operands.All(operand => BoundExpression(operand)?.Type is { } operandType
                    && operandType.Kind == ResolvedTypeKind.Primitive
                    && Specialize(operandType).Kind == ResolvedTypeKind.Primitive
                    && IsSupportedDefaultExpression(operand, parameterIndex, parameterOrdinals));
            default:
                return false;
        }
    }

    private Dictionary<int, LocalSymbol> ParameterLocals(AstFunction function)
    {
        var scopeLocals = _analysis.Sources.Lexical.Scopes.Where(scope => ReferenceEquals(scope.Owner, function))
            .SelectMany(scope => scope.Symbols).ToArray();
        var result = new Dictionary<int, LocalSymbol>();
        for (int index = 0; index < function.Parameters.Length; index++)
        {
            var parameter = function.Parameters[index];
            var local = scopeLocals.SingleOrDefault(candidate => candidate.Kind == LocalSymbolKind.Parameter
                && ReferenceEquals(candidate.Origin.Element, parameter.NameOrigin.Element));
            if (local is not null) { result.Add(index, local); }
        }
        return result;
    }

    private FirExpression Reject(AstNode origin, string subject)
    {
        Unsupported(origin, subject);
        return new FirConstant("()", Empty, origin.Origin);
    }
}
