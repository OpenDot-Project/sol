// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

internal enum MemberLookupStatus
{
    Missing,
    Single,
    OverloadSet,
    Ambiguous,
    LimitExceeded,
}

internal sealed record ResolvedMemberCandidate(
    DeclarationSymbol Symbol,
    ResolvedType DeclaringType,
    int InheritanceDepth,
    ImmutableArray<ResolvedType> InheritancePath,
    bool FromTypeParameterBound)
{
    internal ImmutableArray<ImmutableArray<ResolvedType>> InheritancePaths { get; init; } = [InheritancePath];
}

internal sealed record MemberLookupResult(
    MemberLookupStatus Status,
    ImmutableArray<ResolvedMemberCandidate> Candidates,
    string? Reason);

/// <summary>
/// Resolves a member name across a constructed receiver and its substituted base graph. It keeps
/// every source path needed to diagnose ambiguity, while collapsing duplicate visits to the same
/// declaration on the same constructed owner.
/// </summary>
internal sealed class MemberLookupResolver
{
    private const int MaximumDepth = 128;
    private const int MaximumVisitedTypes = 8192;
    private const int MaximumPathsPerCandidate = 128;

    private readonly SourceAnalysis _sources;
    private readonly TypeSpecialization _types;
    private readonly CancellationToken _cancellation;
    private readonly HashSet<AstNode> _ownedNodes = new(ReferenceEqualityComparer.Instance);

    internal MemberLookupResolver(SourceAnalysis sources, TypeSpecialization types,
        CancellationToken cancellationToken)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _cancellation = cancellationToken;
        foreach (var file in sources.Declarations.Files)
        {
            foreach (var node in file.Nodes) { _ownedNodes.Add(node); }
        }
    }

    internal MemberLookupResult Resolve(ResolvedType receiver, string name, AstNode location)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(location);
        _types.ValidateOwnership(receiver);
        if (!_ownedNodes.Contains(location))
        {
            throw new ArgumentException("The lookup location is not owned by this source analysis.", nameof(location));
        }
        _cancellation.ThrowIfCancellationRequested();
        if (receiver.IsNullable || receiver.IsError || receiver.Kind is ResolvedTypeKind.Bottom or ResolvedTypeKind.Primitive)
        {
            return new(MemberLookupStatus.Missing, [], "The receiver has no nominal member table.");
        }

        var collected = new Dictionary<(int Symbol, int Owner), ResolvedMemberCandidate>();
        var pending = new Queue<(ResolvedType Type, int Depth, ImmutableArray<ResolvedType> Path, bool Bound)>();
        var visitedPaths = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue((receiver, 0, [receiver], receiver.Kind == ResolvedTypeKind.Parameter));
        int visitedCount = 0;
        while (pending.TryDequeue(out var current))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (current.Depth > MaximumDepth)
            {
                return Limit("Member inheritance exceeds the 128-level depth budget.");
            }
            if (!visitedPaths.Add(PathKey(current.Path))) { continue; }
            visitedCount++;
            if (visitedCount > MaximumVisitedTypes)
            {
                return Limit("Member inheritance exceeds the 8192-path visit budget.");
            }

            foreach (var symbol in current.Type.Declaration?.FindMembers(name) ?? [])
            {
                var candidate = new ResolvedMemberCandidate(symbol, current.Type, current.Depth,
                    current.Path, current.Bound);
                var key = (symbol.Id, current.Type.Id);
                if (!collected.TryGetValue(key, out var prior))
                {
                    collected.Add(key, candidate);
                    continue;
                }

                if (prior.InheritancePaths.Any(path => SamePath(path, current.Path))) { continue; }
                if (prior.InheritancePaths.Length >= MaximumPathsPerCandidate)
                {
                    return Limit("One declaration is reachable through more than 128 distinct member paths.");
                }
                var paths = prior.InheritancePaths.Add(current.Path)
                    .OrderBy(path => path.Length)
                    .ThenBy(PathKey, StringComparer.Ordinal)
                    .ToImmutableArray();
                collected[key] = prior with
                {
                    InheritanceDepth = Math.Min(prior.InheritanceDepth, current.Depth),
                    InheritancePath = paths[0],
                    InheritancePaths = paths,
                    FromTypeParameterBound = prior.FromTypeParameterBound || current.Bound,
                };
            }

            foreach (var parent in DirectBases(current.Type))
            {
                if (current.Path.Any(type => ReferenceEquals(type, parent))) { continue; }
                if (pending.Count + visitedCount >= MaximumVisitedTypes)
                {
                    return Limit("Member inheritance exceeds the 8192-path visit budget.");
                }
                var path = current.Path.Add(parent);
                pending.Enqueue((parent, current.Depth + 1, path,
                    current.Bound || current.Type.Kind == ResolvedTypeKind.Parameter));
            }
        }

        if (collected.Count == 0) { return new(MemberLookupStatus.Missing, [], null); }
        var merged = MergeNameGroup(collected.Values.ToImmutableArray());
        merged = SuppressOverridden(merged);
        merged = merged.OrderBy(candidate => candidate.InheritanceDepth)
            .ThenBy(candidate => candidate.Symbol.QualifiedName, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Symbol.Signature, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.DeclaringType.Id)
            .ToImmutableArray();
        return Classify(merged);
    }

    private ImmutableArray<ResolvedMemberCandidate> MergeNameGroup(
        ImmutableArray<ResolvedMemberCandidate> candidates)
    {
        // Hiding is path-sensitive. A declaration on one interface bound cannot erase a same-name
        // declaration on an unrelated bound merely because its path is shorter.
        var allDeclarationOwners = candidates.Select(candidate => candidate.DeclaringType.Id).ToHashSet();
        var valueOwners = candidates.Where(candidate => candidate.Symbol.Kind != SymbolKind.Function)
            .Select(candidate => candidate.DeclaringType.Id).ToHashSet();
        var hiddenByOwner = new Dictionary<(int Owner, bool IsFunction), bool>();
        return candidates.Where(candidate =>
        {
            var key = (candidate.DeclaringType.Id, candidate.Symbol.Kind == SymbolKind.Function);
            if (!hiddenByOwner.TryGetValue(key, out bool hidden))
            {
                hidden = HasHidingAncestor(candidate,
                    candidate.Symbol.Kind == SymbolKind.Function ? valueOwners : allDeclarationOwners);
                hiddenByOwner.Add(key, hidden);
            }
            return !hidden;
        }).ToImmutableArray();
    }

    private ImmutableArray<ResolvedMemberCandidate> SuppressOverridden(
        ImmutableArray<ResolvedMemberCandidate> candidates)
    {
        var methods = candidates.Where(candidate => candidate.Symbol.Parts[0] is AstFunction).ToArray();
        var removed = new HashSet<(int Symbol, int Owner)>();
        foreach (var baseCandidate in methods)
        {
            _cancellation.ThrowIfCancellationRequested();
            foreach (var derivedCandidate in methods)
            {
                if (!IsDeclaredBeforeOnEveryPath(baseCandidate, derivedCandidate.DeclaringType)) { continue; }
                if (SameMethodSignature(derivedCandidate, baseCandidate))
                {
                    removed.Add((baseCandidate.Symbol.Id, baseCandidate.DeclaringType.Id));
                    break;
                }
            }
        }
        return candidates.Where(candidate => !removed.Contains((candidate.Symbol.Id,
            candidate.DeclaringType.Id))).ToImmutableArray();
    }

    private static bool HasHidingAncestor(ResolvedMemberCandidate candidate, HashSet<int> hidingOwners)
    {
        return !candidate.InheritancePaths.IsEmpty
            && candidate.InheritancePaths.All(path => HasHidingAncestorOnPath(candidate, path, hidingOwners));
    }

    private static bool HasHidingAncestorOnPath(ResolvedMemberCandidate candidate,
        ImmutableArray<ResolvedType> path, HashSet<int> hidingOwners)
    {
        int ownerIndex = -1;
        for (int index = 0; index < path.Length; index++)
        {
            if (ReferenceEquals(path[index], candidate.DeclaringType))
            {
                ownerIndex = index;
                break;
            }
        }
        for (int index = 0; index < ownerIndex; index++)
        { if (hidingOwners.Contains(path[index].Id)) { return true; } }
        return false;
    }

    private static bool IsDeclaredBeforeOnEveryPath(ResolvedMemberCandidate inherited,
        ResolvedType possibleDerived)
    {
        return !inherited.InheritancePaths.IsEmpty && inherited.InheritancePaths.All(path =>
        {
            int derivedIndex = -1;
            int inheritedIndex = -1;
            for (int index = 0; index < path.Length; index++)
            {
                if (ReferenceEquals(path[index], possibleDerived)) { derivedIndex = index; }
                if (ReferenceEquals(path[index], inherited.DeclaringType)) { inheritedIndex = index; }
            }
            return derivedIndex >= 0 && inheritedIndex > derivedIndex;
        });
    }

    private static bool SamePath(ImmutableArray<ResolvedType> left, ImmutableArray<ResolvedType> right) =>
        left.Length == right.Length && left.Zip(right)
            .All(pair => ReferenceEquals(pair.First, pair.Second));

    private static string PathKey(ImmutableArray<ResolvedType> path) =>
        string.Join("/", path.Select(type => type.Id.ToString(CultureInfo.InvariantCulture)));

    private bool SameMethodSignature(ResolvedMemberCandidate left, ResolvedMemberCandidate right)
    {
        if (left.Symbol.Parts[0] is not AstFunction leftFunction
            || right.Symbol.Parts[0] is not AstFunction rightFunction
            || leftFunction.Parameters.Length != rightFunction.Parameters.Length
            || leftFunction.TypeParameters.Count() != rightFunction.TypeParameters.Count()) { return false; }

        var leftMap = OwnerSubstitution(left.DeclaringType);
        var rightMap = OwnerSubstitution(right.DeclaringType);
        var leftParameters = _sources.Types.GetTypeParameters(leftFunction);
        var rightParameters = _sources.Types.GetTypeParameters(rightFunction);
        for (int index = 0; index < leftParameters.Length; index++)
        {
            rightMap[rightParameters[index]] = leftParameters[index];
        }

        for (int index = 0; index < leftFunction.Parameters.Length; index++)
        {
            var leftParameter = leftFunction.Parameters[index];
            var rightParameter = rightFunction.Parameters[index];
            if (leftParameter.PassingMode != rightParameter.PassingMode
                || leftParameter.Type is null || rightParameter.Type is null) { return false; }
            var leftType = _sources.Types.GetType(leftParameter.Type);
            var rightType = _sources.Types.GetType(rightParameter.Type);
            if (leftMap.Count != 0) { leftType = _types.Substitute(leftType, leftMap, _cancellation); }
            if (rightMap.Count != 0) { rightType = _types.Substitute(rightType, rightMap, _cancellation); }
            if (!ReferenceEquals(leftType, rightType)) { return false; }
        }
        return true;
    }

    private Dictionary<ResolvedType, ResolvedType> OwnerSubstitution(ResolvedType owner)
    {
        var result = new Dictionary<ResolvedType, ResolvedType>();
        if (owner.Declaration is not { } symbol) { return result; }
        var parameters = _sources.Types.GetTypeParameters(symbol.Parts[0]);
        if (parameters.Length != owner.Arguments.Length) { return result; }
        for (int index = 0; index < parameters.Length; index++)
        {
            result.Add(parameters[index], owner.Arguments[index]);
        }
        return result;
    }

    private ImmutableArray<ResolvedType> DirectBases(ResolvedType type)
    {
        if (type.Kind == ResolvedTypeKind.Nominal) { return _types.GetDirectBases(type, _cancellation); }
        if (type.Kind != ResolvedTypeKind.Parameter || type.Parameter is not { } identity) { return []; }

        return ParameterBounds(identity).Select(_sources.Types.GetType)
            .Distinct().OrderBy(bound => bound.Id).ToImmutableArray();
    }

    private static IEnumerable<AstType> ParameterBounds(GenericParameterIdentity identity)
    {
        var parameters = identity.Owner switch
        {
            AstCallable callable => callable.TypeParameters.ToArray(),
            AstTypeDeclaration type => type.TypeParameters.ToArray(),
            _ => [],
        };
        var parameter = identity.Ordinal >= 0 && identity.Ordinal < parameters.Length
            ? parameters[identity.Ordinal] : null;
        if (parameter?.Constraint is { } direct) { yield return direct; }
        if (parameter?.Name is not { } name) { yield break; }
        foreach (var clause in identity.Owner.Children.Where(node => node.SyntaxKind == NodeKind.WhereClause))
        {
            foreach (var entry in clause.Children.Where(child => child.Children.Any(token =>
                         token.SyntaxKind == NodeKind.Name && token.Origin.Text == name)))
            {
                foreach (var type in entry.Children.OfType<AstType>()) { yield return type; }
            }
        }
    }

    private static MemberLookupResult Classify(ImmutableArray<ResolvedMemberCandidate> candidates)
    {
        if (candidates.IsEmpty) { return new(MemberLookupStatus.Missing, [], null); }
        if (candidates.Any(candidate => candidate.Symbol.Kind != SymbolKind.Function)
            && candidates.Any(candidate => candidate.Symbol.Kind == SymbolKind.Function))
        {
            return new(MemberLookupStatus.Ambiguous, candidates, "A member name resolves to both callable and value declarations.");
        }
        if (candidates.Any(candidate => candidate.Symbol.Kind != SymbolKind.Function))
        {
            return candidates.Length == 1
                ? new(MemberLookupStatus.Single, candidates, null)
                : new(MemberLookupStatus.Ambiguous, candidates, "Multiple non-callable declarations provide the same member name.");
        }
        return new(candidates.Length == 1 ? MemberLookupStatus.Single : MemberLookupStatus.OverloadSet,
            candidates, null);
    }

    private static MemberLookupResult Limit(string reason) =>
        new(MemberLookupStatus.LimitExceeded, [], reason);
}
