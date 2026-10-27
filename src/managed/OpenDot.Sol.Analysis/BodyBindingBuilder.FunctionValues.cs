// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private sealed record FunctionValueCandidate(DeclarationSymbol Symbol, AstFunction Function,
        ResolvedType Type, ImmutableArray<ResolvedType> TypeArguments,
        IntrinsicConversion Conversion, int Rank);

    private Value OverloadedFunctionValue(AstNode location,
        System.Collections.Immutable.ImmutableArray<DeclarationSymbol> symbols, ResolvedType? expected)
    {
        if (expected is null || expected.Kind != ResolvedTypeKind.Function
            && !(expected.IsNullable && expected.Arguments[0].Kind == ResolvedTypeKind.Function))
        {
            Defer(location);
            return new(null);
        }
        var expectedFunction = expected.IsNullable ? expected.Arguments[0] : expected;
        if (symbols.Select(symbol => symbol.ModuleName).Distinct(System.StringComparer.Ordinal).Take(2).Count() > 1)
        {
            Report("SOL2212", "Ambiguous imported function value; declarations from separate modules do not form one overload set.", location);
            return new(null);
        }

        var visible = symbols.Where(symbol => Accessible(symbol, location)).ToArray();
        if (visible.Length == 0)
        {
            Report("SOL2214", "No function overload is accessible in this context.", location);
            return new(null);
        }

        // A generic value candidate needs a separately specified closure rule. An incomplete
        // signature could change the winner, so neither is silently removed from this overload set.
        foreach (var symbol in visible)
        {
            if (symbol.Kind != SymbolKind.Function
                || symbol.Parts[0] is not AstFunction function
                || Ancestors(function).OfType<AstTypeDeclaration>().FirstOrDefault() is { } owner
                    && (owner.SyntaxKind != NodeKind.ObjectDeclaration
                        || !GenericMethodSourcePolicy.IsSupportedStatelessObjectMethod(function, Sources))
                || function.Parameters.Any(parameter => parameter.Type is null))
            {
                Defer(location);
                return new(null);
            }
        }

        var candidates = new List<FunctionValueCandidate>(visible.Length);
        bool hasDeferredCandidate = false;
        foreach (var symbol in visible)
        {
            _cancellation.ThrowIfCancellationRequested();
            var function = (AstFunction)symbol.Parts[0];
            var typeParameters = Sources.Types.GetTypeParameters(function);
            var functionType = FunctionValueSignature(function);
            if (functionType is null)
            {
                Defer(location);
                hasDeferredCandidate = true;
                continue;
            }
            ImmutableArray<ResolvedType> typeArguments = [];
            if (!typeParameters.IsEmpty)
            {
                var inference = _genericInference.Solve(typeParameters,
                    ImmutableDictionary<ResolvedType, ResolvedType>.Empty,
                    [new(functionType, expectedFunction, GenericInferenceRelation.Expected, "expected function value")]);
                if (inference.Status is GenericInferenceStatus.MissingEvidence or GenericInferenceStatus.ResourceLimit)
                {
                    Defer(location);
                    hasDeferredCandidate = true;
                    continue;
                }
                if (inference.Status is GenericInferenceStatus.ConflictingEvidence or GenericInferenceStatus.IncompatibleShape)
                { continue; }
                if (inference.Status != GenericInferenceStatus.Solved
                    || typeParameters.Any(parameter => !inference.Substitution.ContainsKey(parameter)))
                {
                    Defer(location);
                    hasDeferredCandidate = true;
                    continue;
                }

                typeArguments = typeParameters.Select(parameter => inference.Substitution[parameter]).ToImmutableArray();
                var constraints = Types.CheckConstraints(function, typeArguments, _cancellation);
                if (constraints.Status == ConstraintStatus.Unsatisfied) { continue; }
                if (constraints.Status == ConstraintStatus.Indeterminate)
                {
                    Defer(location);
                    hasDeferredCandidate = true;
                    continue;
                }
                var substitution = typeParameters.Zip(typeArguments).ToDictionary(pair => pair.First, pair => pair.Second);
                functionType = Types.Substitute(functionType, substitution, _cancellation);
            }
            var conversion = Types.ClassifyConversion(functionType, expected, _cancellation);
            if (conversion == IntrinsicConversion.None) { continue; }
            int rank = conversion switch
            {
                IntrinsicConversion.Identity => 0,
                IntrinsicConversion.Reference => 1,
                IntrinsicConversion.Boxing => 2,
                _ => int.MaxValue,
            };
            candidates.Add(new(symbol, function, functionType, typeArguments, conversion, rank));
        }

        // An unresolved candidate can still beat an otherwise viable candidate after it is
        // closed. Keep the overload group deferred instead of silently deleting that candidate.
        if (hasDeferredCandidate)
        {
            Defer(location);
            return new(null);
        }

        if (candidates.Count == 0)
        {
            Report("SOL2206", "No function overload converts to the expected function type.", location);
            return new(null);
        }

        int bestRank = candidates.Min(candidate => candidate.Rank);
        var best = candidates.Where(candidate => candidate.Rank == bestRank).ToArray();
        if (best.Length > 1)
        {
            var nongeneric = best.Where(candidate => candidate.TypeArguments.IsEmpty).ToArray();
            if (nongeneric.Length != 0) { best = nongeneric; }
        }
        if (best.Length != 1)
        {
            Report("SOL2212", "Ambiguous function value: multiple overloads have the same conversion rank.", location);
            return new(null);
        }

        var selected = best[0];
        ValidateFunctionEffects(selected.Type, location);
        return new(selected.Type, Symbol: selected.Symbol, FunctionValueTypeArguments: selected.TypeArguments);
    }

    private ResolvedType? FunctionValueSignature(AstFunction function)
    {
        var result = function.ReturnType is { } returnType
            ? Specialize(returnType, receiver: null)
            : CheckBody(function).ReturnType;
        if (result is null) { return null; }
        var parameters = function.Parameters.Select(parameter => parameter.PassingMode == AstPassingMode.Vararg
            ? Types.GetArray(Specialize(parameter.Type!, receiver: null))
            : Specialize(parameter.Type!, receiver: null));
        var modes = function.Parameters.Select(parameter => parameter.PassingMode == AstPassingMode.Vararg
            ? AstPassingMode.Value : parameter.PassingMode);
        return Types.GetFunction(parameters, result, modes,
            function.ThrownTypes.Select(effect => Specialize(effect, receiver: null)));
    }
}
