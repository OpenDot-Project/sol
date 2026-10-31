// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private sealed record ResolvedOperatorCandidate(
        AstFunction Function,
        ImmutableDictionary<ResolvedType, ResolvedType> Substitution,
        ImmutableArray<ResolvedType> ParameterTypes,
        ImmutableArray<int> Ranks,
        bool IsGeneric);

    private bool TryCloseOperatorCandidate(AstNode location,
        (AstFunction Function, ResolvedType Owner) candidate,
        Value[] operands,
        bool exactOperands,
        ResolvedType? expected,
        out ResolvedOperatorCandidate? closed,
        out bool deferred)
    {
        closed = null;
        deferred = false;
        var function = candidate.Function;
        var ownerSubstitution = Substitution(candidate.Owner);
        var typeParameters = Sources.Types.GetTypeParameters(function);
        IReadOnlyDictionary<ResolvedType, ResolvedType> substitution = ownerSubstitution;
        if (!typeParameters.IsEmpty)
        {
            var evidence = ImmutableArray.CreateBuilder<GenericInferenceConstraint>(operands.Length);
            for (int index = 0; index < operands.Length; index++)
            {
                _cancellation.ThrowIfCancellationRequested();
                evidence.Add(new(Written(function.Parameters[index].Type!), operands[index].Type!,
                    exactOperands ? GenericInferenceRelation.Exact : GenericInferenceRelation.Input,
                    "operator operand " + index));
            }
            if (expected is not null && function.ReturnType is { } returnType)
            {
                evidence.Add(new(Written(returnType), expected, GenericInferenceRelation.Expected,
                    "expected operator result"));
            }

            var result = GenericInference.Solve(typeParameters, ownerSubstitution, evidence);
            if (result.Status == GenericInferenceStatus.MissingEvidence)
            {
                deferred = true;
                return false;
            }
            if (result.Status == GenericInferenceStatus.ResourceLimit)
            {
                Report("SOL2290", result.Reason ?? "Generic operator inference exceeded its resource budget.", location);
                Defer(location);
                deferred = true;
                return false;
            }
            if (result.Status != GenericInferenceStatus.Solved) { return false; }

            var arguments = typeParameters.Select(parameter => result.Substitution.GetValueOrDefault(parameter, parameter)).ToArray();
            var constraints = Types.CheckConstraintsWithSubstitution(function, arguments, ownerSubstitution, _cancellation);
            if (constraints.Status == ConstraintStatus.Unsatisfied) { return false; }
            if (constraints.Status == ConstraintStatus.Indeterminate)
            {
                deferred = true;
                return false;
            }
            substitution = result.Substitution;
        }

        var parameterTypes = function.Parameters
            .Select(parameter => Types.Substitute(Written(parameter.Type!), substitution, _cancellation))
            .ToImmutableArray();
        var ranks = ImmutableArray.CreateBuilder<int>(operands.Length);
        for (int index = 0; index < operands.Length; index++)
        {
            _cancellation.ThrowIfCancellationRequested();
            int rank = exactOperands
                ? operands[index].Type == parameterTypes[index] ? 0 : -1
                : ConversionRank(operands[index].Type!, parameterTypes[index]);
            if (rank < 0) { return false; }
            ranks.Add(rank);
        }

        closed = new(function, substitution.ToImmutableDictionary(), parameterTypes, ranks.ToImmutable(), !typeParameters.IsEmpty);
        return true;
    }

    private static bool BetterOperatorCandidate(ResolvedOperatorCandidate left, ResolvedOperatorCandidate right)
    {
        if (BetterOperatorRanks(left.Ranks.ToArray(), right.Ranks.ToArray())) { return true; }
        return left.Ranks.SequenceEqual(right.Ranks) && !left.IsGeneric && right.IsGeneric;
    }
}
