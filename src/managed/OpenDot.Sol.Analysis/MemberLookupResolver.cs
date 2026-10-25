// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    bool FromTypeParameterBound);

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
        var visited = new Dictionary<int, int>();
        pending.Enqueue((receiver, 0, [receiver], receiver.Kind == ResolvedTypeKind.Parameter));
        while (pending.TryDequeue(out var current))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (current.Depth > MaximumDepth)
            {
                return Limit("Member inheritance exceeds the 128-level depth budget.");
            }
            if (visited.TryGetValue(current.Type.Id, out int priorDepth) && priorDepth <= current.Depth) { continue; }
            visited[current.Type.Id] = current.Depth;
            if (visited.Count > MaximumVisitedTypes)
            {
                return Limit("Member inheritance exceeds the 8192-type visit budget.");
            }

            foreach (var symbol in current.Type.Declaration?.FindMembers(name) ?? [])
            {
                var candidate = new ResolvedMemberCandidate(symbol, current.Type, current.Depth,
                    current.Path, current.Bound);
                collected.TryAdd((symbol.Id, current.Type.Id), candidate);
            }

            foreach (var parent in DirectBases(current.Type))
            {
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
        var nonFunctions = candidates.Where(candidate => candidate.Symbol.Kind != SymbolKind.Function).ToArray();
        if (nonFunctions.Length == 0) { return candidates; }

        int nearestNonFunction = nonFunctions.Min(candidate => candidate.InheritanceDepth);
        int nearestFunction = candidates.Where(candidate => candidate.Symbol.Kind == SymbolKind.Function)
            .Select(candidate => candidate.InheritanceDepth).DefaultIfEmpty(int.MaxValue).Min();
        if (nearestFunction < nearestNonFunction)
        {
            // A more-derived function group hides an inherited property/type value with the same
            // name. Methods from farther ancestors remain candidates for overload resolution.
            return candidates.Where(candidate => candidate.Symbol.Kind == SymbolKind.Function).ToImmutableArray();
        }

        // A property, nested type, indexer, or variant at the nearest value depth owns the name.
        // Returning only that depth prevents an inaccessible or unrelated base declaration from
        // leaking back into lookup as a fallback.
        return candidates.Where(candidate => candidate.InheritanceDepth == nearestNonFunction).ToImmutableArray();
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
                if (derivedCandidate.InheritanceDepth >= baseCandidate.InheritanceDepth) { continue; }
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
