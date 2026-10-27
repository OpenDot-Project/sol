// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class GenericInferenceSolver
{
    private const int MaximumEffectSetMembers = 256;
    private const int MaximumEffectMatchingStates = 4096;

    private sealed class EffectMatchingBudget
    {
        internal int VisitedStates { get; set; }
    }

    private sealed record EffectSetConstraint(
        ImmutableArray<ResolvedType> Templates,
        ImmutableArray<ResolvedType> Actuals,
        string Origin,
        int Depth);

    private void ResolveEffectSets(ImmutableArray<ResolvedType> parameters,
        HashSet<ResolvedType> parameterSet,
        IReadOnlyDictionary<ResolvedType, ResolvedType> fixedMap,
        Dictionary<ResolvedType, Evidence> evidence,
        List<EffectSetConstraint> constraints)
    {
        if (constraints.Count == 0) { return; }
        while (true)
        {
            _cancellation.ThrowIfCancellationRequested();
            bool changed = false;
            foreach (var constraint in constraints)
            {
                _cancellation.ThrowIfCancellationRequested();
                if (!TryResolveEffectSet(constraint, parameters, parameterSet, fixedMap, evidence,
                        out var uniqueEvidence))
                { continue; }
                changed |= MergeEvidence(evidence, uniqueEvidence, parameters);
            }
            if (!changed) { return; }
        }
    }

    private bool TryResolveEffectSet(EffectSetConstraint constraint,
        ImmutableArray<ResolvedType> parameters,
        HashSet<ResolvedType> parameterSet,
        IReadOnlyDictionary<ResolvedType, ResolvedType> fixedMap,
        Dictionary<ResolvedType, Evidence> evidence,
        out Dictionary<ResolvedType, Evidence> uniqueEvidence,
        EffectMatchingBudget? sharedBudget = null)
    {
        uniqueEvidence = [];
        var budget = sharedBudget ?? new EffectMatchingBudget();
        var known = KnownSubstitution(parameters, fixedMap, evidence, out bool initialConflict);
        if (initialConflict) { return false; }

        var templates = constraint.Templates.Select(type => _types.Substitute(type, known, _cancellation))
            .Distinct().OrderBy(type => type.Id).ToArray();
        var actuals = constraint.Actuals.Select(type => _types.Substitute(type, known, _cancellation))
            .Distinct().OrderBy(type => type.Id).ToArray();
        if (templates.Length > MaximumEffectSetMembers || actuals.Length > MaximumEffectSetMembers)
        {
            throw new TypeSpecializationException(SpecializationFailure.ResourceLimit,
                "Generic effect inference exceeds the 256-member set budget.");
        }

        var open = templates.Where(type => ContainsInferenceParameter(type, parameterSet)).ToArray();
        if (open.Length == 0 || templates.Length != actuals.Length) { return false; }

        var remaining = actuals.ToList();
        foreach (var fixedEffect in templates.Where(type => !ContainsInferenceParameter(type, parameterSet)))
        {
            _cancellation.ThrowIfCancellationRequested();
            int index = remaining.FindIndex(actual => actual == fixedEffect);
            if (index < 0) { return false; }
            remaining.RemoveAt(index);
        }
        if (open.Length != remaining.Count) { return false; }

        var currentEvidence = CloneEvidence(evidence, parameters);
        var used = new bool[remaining.Count];
        Dictionary<ResolvedType, Evidence>? unique = null;
        int validMappings = 0;

        void Search(int patternIndex, Dictionary<ResolvedType, Evidence> current,
            List<EffectSetConstraint> pendingNested)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (++budget.VisitedStates > MaximumEffectMatchingStates)
            {
                throw new TypeSpecializationException(SpecializationFailure.ResourceLimit,
                    "Generic effect matching exceeds the 4096-state search budget.");
            }
            if (validMappings > 1) { return; }
            if (patternIndex == open.Length)
            {
                var completed = CloneEvidence(current, parameters);
                if (!TryResolveNestedEffectSets(pendingNested, parameters, parameterSet,
                        fixedMap, completed, budget)) { return; }
                var completeMap = KnownSubstitution(parameters, fixedMap, completed, out bool conflict);
                if (conflict || HasUnresolvedConstraint(parameters, completeMap)) { return; }
                validMappings++;
                if (validMappings == 1) { unique = completed; }
                return;
            }

            for (int index = 0; index < remaining.Count && validMappings < 2; index++)
            {
                _cancellation.ThrowIfCancellationRequested();
                if (used[index]) { continue; }
                var branch = CloneEvidence(current, parameters);
                var branchMap = KnownSubstitution(parameters, fixedMap, branch, out bool branchConflict);
                if (branchConflict) { continue; }
                var discoveredNested = new List<EffectSetConstraint>();
                if (!Collect(open[patternIndex], remaining[index], GenericInferenceRelation.Exact,
                        constraint.Origin, parameterSet, branch, branchMap, discoveredNested,
                        constraint.Depth + 1))
                { continue; }
                branchMap = KnownSubstitution(parameters, fixedMap, branch, out branchConflict);
                if (branchConflict || HasKnownUnsatisfiedConstraint(parameters, branchMap)
                    || HasUnresolvedConstraint(parameters, branchMap)) { continue; }
                var childConstraints = pendingNested.Count == 0
                    ? new List<EffectSetConstraint>()
                    : new List<EffectSetConstraint>(pendingNested);
                childConstraints.AddRange(discoveredNested);
                used[index] = true;
                Search(patternIndex + 1, branch, childConstraints);
                used[index] = false;
            }
        }

        Search(0, currentEvidence, []);
        if (validMappings != 1 || unique is null) { return false; }
        uniqueEvidence = unique;
        return true;
    }

    private bool TryResolveNestedEffectSets(List<EffectSetConstraint> constraints,
        ImmutableArray<ResolvedType> parameters,
        HashSet<ResolvedType> parameterSet,
        IReadOnlyDictionary<ResolvedType, ResolvedType> fixedMap,
        Dictionary<ResolvedType, Evidence> evidence,
        EffectMatchingBudget budget)
    {
        if (constraints.Count == 0) { return true; }
        var pending = new List<EffectSetConstraint>(constraints);
        while (pending.Count != 0)
        {
            _cancellation.ThrowIfCancellationRequested();
            bool changed = false;
            for (int index = pending.Count - 1; index >= 0; index--)
            {
                _cancellation.ThrowIfCancellationRequested();
                var constraint = pending[index];
                var known = KnownSubstitution(parameters, fixedMap, evidence, out bool conflict);
                if (conflict) { return false; }
                bool hasOpenPattern = constraint.Templates.Any(type =>
                    ContainsInferenceParameter(_types.Substitute(type, known, _cancellation), parameterSet));
                if (!hasOpenPattern)
                {
                    // No inference fact remains here; the closed function-type conversion is
                    // checked by candidate applicability after all generic arguments are known.
                    pending.RemoveAt(index);
                    changed = true;
                    continue;
                }

                if (!TryResolveEffectSet(constraint, parameters, parameterSet, fixedMap,
                        evidence, out var nestedEvidence, budget)) { continue; }
                changed |= MergeEvidence(evidence, nestedEvidence, parameters);
                pending.RemoveAt(index);
                changed = true;
            }
            if (!changed) { return false; }
        }
        return true;
    }

    private Dictionary<ResolvedType, ResolvedType> KnownSubstitution(
        ImmutableArray<ResolvedType> parameters,
        IReadOnlyDictionary<ResolvedType, ResolvedType> fixedMap,
        Dictionary<ResolvedType, Evidence> evidence,
        out bool conflict)
    {
        var known = new Dictionary<ResolvedType, ResolvedType>(fixedMap);
        conflict = false;
        foreach (var parameter in parameters)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (!TryChoose(evidence[parameter], fixedMap, out var selected, out bool currentConflict))
            {
                conflict |= currentConflict;
                continue;
            }
            known[parameter] = selected!;
        }
        return known;
    }

    private bool HasUnresolvedConstraint(ImmutableArray<ResolvedType> parameters,
        IReadOnlyDictionary<ResolvedType, ResolvedType> substitution)
    {
        var owners = parameters.Select(parameter => parameter.Parameter?.Owner)
            .OfType<AstDeclaration>().Distinct();
        foreach (var owner in owners)
        {
            _cancellation.ThrowIfCancellationRequested();
            var ownerParameters = _analysis.GetTypeParameters(owner);
            var arguments = ownerParameters.Select(parameter => substitution.GetValueOrDefault(parameter, parameter))
                .ToArray();
            if (arguments.Any(ContainsAnyParameter)) { continue; }
            if (_types.CheckConstraints(owner, arguments, _cancellation).Status != ConstraintStatus.Satisfied)
            { return true; }
        }
        return false;
    }

    private bool HasKnownUnsatisfiedConstraint(ImmutableArray<ResolvedType> parameters,
        IReadOnlyDictionary<ResolvedType, ResolvedType> substitution)
    {
        var owners = parameters.Select(parameter => parameter.Parameter?.Owner)
            .OfType<AstDeclaration>().Distinct();
        foreach (var owner in owners)
        {
            _cancellation.ThrowIfCancellationRequested();
            var ownerParameters = _analysis.GetTypeParameters(owner);
            var arguments = ownerParameters.Select(parameter =>
                substitution.GetValueOrDefault(parameter, parameter)).ToArray();
            if (_types.HasUnsatisfiedClosedConstraints(owner, arguments, substitution, _cancellation))
            { return true; }
        }
        return false;
    }

    private bool ContainsAnyParameter(ResolvedType type)
    {
        var pending = new Stack<ResolvedType>();
        var visited = new HashSet<int>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (!visited.Add(current.Id)) { continue; }
            if (current.Kind == ResolvedTypeKind.Parameter) { return true; }
            foreach (var child in current.Arguments.Concat(current.Effects)) { pending.Push(child); }
        }
        return false;
    }

    private static Dictionary<ResolvedType, Evidence> CloneEvidence(
        Dictionary<ResolvedType, Evidence> evidence, ImmutableArray<ResolvedType> parameters)
    {
        var copy = new Dictionary<ResolvedType, Evidence>();
        foreach (var parameter in parameters)
        {
            var source = evidence[parameter];
            var target = new Evidence();
            target.Lower.AddRange(source.Lower);
            target.Exact.AddRange(source.Exact);
            target.Upper.AddRange(source.Upper);
            target.Facts.AddRange(source.Facts);
            copy.Add(parameter, target);
        }
        return copy;
    }

    private bool MergeEvidence(Dictionary<ResolvedType, Evidence> target,
        Dictionary<ResolvedType, Evidence> source, ImmutableArray<ResolvedType> parameters)
    {
        bool changed = false;
        foreach (var parameter in parameters)
        {
            var destination = target[parameter];
            int oldFacts = destination.Facts.Count;
            int oldLower = destination.Lower.Count;
            int oldExact = destination.Exact.Count;
            int oldUpper = destination.Upper.Count;
            foreach (var fact in source[parameter].Facts)
            {
                AddEvidence(destination, fact.Parameter, fact.Relation, fact.Type, fact.Origin);
            }
            changed |= oldFacts != destination.Facts.Count || oldLower != destination.Lower.Count
                || oldExact != destination.Exact.Count || oldUpper != destination.Upper.Count;
        }
        return changed;
    }
}
