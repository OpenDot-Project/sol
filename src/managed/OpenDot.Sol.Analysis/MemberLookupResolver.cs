// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
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

internal enum OwnerProjectionStatus
{
    Unique,
    Missing,
    Ambiguous,
    LimitExceeded,
}

internal sealed record ConstructedOwnerProjectionCandidate(
    ResolvedType ConstructedOwner,
    ImmutableArray<ImmutableArray<ResolvedType>> Paths);

internal sealed record ConstructedOwnerProjection(
    OwnerProjectionStatus Status,
    ImmutableArray<ConstructedOwnerProjectionCandidate> Candidates,
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

    private readonly TypeAnalysis _analysis;
    private readonly TypeSpecialization _types;
    private readonly CancellationToken _cancellation;
    private readonly HashSet<AstNode> _ownedNodes = new(ReferenceEqualityComparer.Instance);

    internal MemberLookupResolver(TypeAnalysis analysis, TypeSpecialization types,
        IEnumerable<AstSnapshot> files, CancellationToken cancellationToken)
    {
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _types = types ?? throw new ArgumentNullException(nameof(types));
        ArgumentNullException.ThrowIfNull(files);
        _cancellation = cancellationToken;
        foreach (var file in files)
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

    /// <summary>
    /// Projects a receiver onto every reachable constructed instance of one declaration. Distinct
    /// routes to the same constructed owner are retained for later source-analysis decisions.
    /// </summary>
    internal ConstructedOwnerProjection ProjectOwner(ResolvedType receiver,
        DeclarationSymbol targetOwner, AstNode location)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(targetOwner);
        ArgumentNullException.ThrowIfNull(location);
        if (!ReferenceEquals(_types.Analysis, _analysis))
        {
            throw new ArgumentException("The type-specialization workspace does not belong to this source analysis.");
        }
        _types.ValidateOwnership(receiver);
        ValidateTargetOwner(targetOwner);
        if (!_ownedNodes.Contains(location))
        {
            throw new ArgumentException("The projection location is not owned by this source analysis.", nameof(location));
        }
        _cancellation.ThrowIfCancellationRequested();

        if (receiver.IsError)
        {
            return ProjectionLimit("The receiver type is unresolved, so its owner projection is incomplete.");
        }
        if (receiver.IsNullable || receiver.Kind is ResolvedTypeKind.Bottom or ResolvedTypeKind.Primitive
            or ResolvedTypeKind.Array or ResolvedTypeKind.FixedArray or ResolvedTypeKind.Tuple
            or ResolvedTypeKind.Function)
        {
            return new(OwnerProjectionStatus.Missing, [], null);
        }
        if (receiver.Kind is not (ResolvedTypeKind.Nominal or ResolvedTypeKind.Parameter))
        {
            return ProjectionLimit("The receiver kind cannot be projected with a complete nominal owner graph.");
        }

        var routesByOwner = new Dictionary<int, (ResolvedType Owner, List<ImmutableArray<ResolvedType>> Paths,
            HashSet<string> PathKeys)>();
        var pending = new Queue<(ResolvedType Type, int Depth, ImmutableArray<ResolvedType> Path)>();
        var knownPaths = new HashSet<string>(StringComparer.Ordinal);
        var initialPath = ImmutableArray.Create(receiver);
        pending.Enqueue((receiver, 0, initialPath));
        knownPaths.Add(PathKey(initialPath));

        while (pending.TryDequeue(out var current))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (current.Depth > MaximumDepth)
            {
                return ProjectionLimit("Constructed owner projection exceeds the 128-edge depth budget.");
            }

            if (current.Type.IsError)
            {
                return ProjectionLimit("A reachable base or type-parameter bound is unresolved.");
            }

            if (current.Type.Kind == ResolvedTypeKind.Nominal
                && ReferenceEquals(current.Type.Declaration, targetOwner))
            {
                string routeKey = PathKey(current.Path);
                if (!routesByOwner.TryGetValue(current.Type.Id, out var projection))
                {
                    projection = (current.Type, [], new(StringComparer.Ordinal));
                    routesByOwner.Add(current.Type.Id, projection);
                }
                if (projection.PathKeys.Add(routeKey))
                {
                    if (projection.Paths.Count >= MaximumPathsPerCandidate)
                    {
                        return ProjectionLimit("One constructed owner is reachable through more than 128 distinct paths.");
                    }
                    projection.Paths.Add(current.Path);
                    routesByOwner[current.Type.Id] = projection;
                }
            }

            ImmutableArray<ResolvedType> bases;
            try
            {
                bases = ProjectionBases(current.Type);
            }
            catch (TypeSpecializationException exception)
            {
                return ProjectionLimit(exception.Message);
            }

            foreach (var parent in bases)
            {
                _cancellation.ThrowIfCancellationRequested();
                if (current.Path.Any(type => ReferenceEquals(type, parent))) { continue; }
                int nextDepth = current.Depth + 1;
                if (nextDepth > MaximumDepth)
                {
                    return ProjectionLimit("Constructed owner projection exceeds the 128-edge depth budget.");
                }

                var path = current.Path.Add(parent);
                if (!knownPaths.Add(PathKey(path))) { continue; }
                if (knownPaths.Count > MaximumVisitedTypes)
                {
                    return ProjectionLimit("Constructed owner projection exceeds the 8192-path-state budget.");
                }
                pending.Enqueue((parent, nextDepth, path));
            }
        }

        var orderKeys = new Dictionary<int, string>();
        var orderedCandidates = routesByOwner.Values
            .Select(candidate =>
            {
                _cancellation.ThrowIfCancellationRequested();
                return (Candidate: candidate, Key: ConstructedTypeOrderKey(candidate.Owner, orderKeys));
            })
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.Owner.Id)
            .ToImmutableArray();
        var results = ImmutableArray.CreateBuilder<ConstructedOwnerProjectionCandidate>(orderedCandidates.Length);
        foreach (var item in orderedCandidates)
        {
            _cancellation.ThrowIfCancellationRequested();
            var orderedPaths = item.Candidate.Paths
                .Select(path => (Path: path, Key: ProjectionPathOrderKey(path, orderKeys)))
                .OrderBy(path => path.Path.Length)
                .ThenBy(path => path.Key, StringComparer.Ordinal)
                .ThenBy(path => PathKey(path.Path), StringComparer.Ordinal)
                .Select(path => path.Path)
                .ToImmutableArray();
            results.Add(new(item.Candidate.Owner, orderedPaths));
        }

        var candidates = results.ToImmutable();
        OwnerProjectionStatus status = candidates.Length switch
        {
            0 => OwnerProjectionStatus.Missing,
            1 => OwnerProjectionStatus.Unique,
            _ => OwnerProjectionStatus.Ambiguous,
        };
        return new(status, candidates, null);
    }

    private void ValidateTargetOwner(DeclarationSymbol targetOwner)
    {
        if (targetOwner.Kind != SymbolKind.Type || targetOwner.Parts.IsEmpty
            || !ReferenceEquals(_analysis.Declarations.GetDeclaredSymbol(targetOwner.Parts[0]), targetOwner))
        {
            throw new ArgumentException("The target owner is not a type declaration owned by this analysis.", nameof(targetOwner));
        }
    }

    private ImmutableArray<ResolvedType> ProjectionBases(ResolvedType type)
    {
        if (type.Kind == ResolvedTypeKind.Nominal)
        {
            return _types.GetDirectBases(type, _cancellation);
        }
        if (type.Kind != ResolvedTypeKind.Parameter || type.Parameter is not { } identity) { return []; }

        var bounds = new List<ResolvedType>();
        foreach (var syntax in ParameterBounds(identity))
        {
            _cancellation.ThrowIfCancellationRequested();
            var bound = _analysis.GetType(syntax);
            _types.ValidateOwnership(bound);
            if (bound.IsError || bound.Kind is not (ResolvedTypeKind.Nominal or ResolvedTypeKind.Parameter))
            {
                throw new TypeSpecializationException(SpecializationFailure.InvalidType,
                    "A type-parameter bound is unresolved or cannot lead to a nominal owner.");
            }
            if (!bounds.Any(existing => ReferenceEquals(existing, bound))) { bounds.Add(bound); }
        }
        var orderKeys = new Dictionary<int, string>();
        return bounds.OrderBy(bound => ConstructedTypeOrderKey(bound, orderKeys), StringComparer.Ordinal)
            .ThenBy(bound => bound.Id).ToImmutableArray();
    }

    private string ProjectionPathOrderKey(ImmutableArray<ResolvedType> path,
        Dictionary<int, string> orderKeys)
    {
        var builder = new StringBuilder();
        foreach (var type in path)
        {
            _cancellation.ThrowIfCancellationRequested();
            AppendField(builder, ConstructedTypeOrderKey(type, orderKeys));
        }
        return builder.ToString();
    }

    private string ConstructedTypeOrderKey(ResolvedType type, Dictionary<int, string> cache)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (cache.TryGetValue(type.Id, out var prior)) { return prior; }

        var builder = new StringBuilder();
        AppendInvariant(builder, (int)type.Kind);
        builder.Append(';');
        AppendField(builder, type.Name);
        if (type.Declaration is { } declaration)
        {
            AppendField(builder, declaration.ModuleName);
            AppendField(builder, declaration.QualifiedName);
            AppendField(builder, declaration.Signature);
            var source = declaration.Parts[0].Origin;
            AppendField(builder, source.Source.Path);
            AppendInvariant(builder, source.Span.Start);
            builder.Append(';');
        }
        else if (type.Parameter is { } parameter)
        {
            var owner = parameter.Owner.Origin;
            AppendField(builder, owner.Source.Path);
            AppendInvariant(builder, (int)parameter.Owner.SyntaxKind);
            builder.Append(';');
            AppendInvariant(builder, owner.Span.Start);
            builder.Append(';');
            AppendInvariant(builder, parameter.Ordinal);
            builder.Append(';');
        }

        AppendInvariant(builder, type.Arguments.Length);
        builder.Append(';');
        foreach (var argument in type.Arguments)
        {
            AppendField(builder, ConstructedTypeOrderKey(argument, cache));
        }
        AppendInvariant(builder, type.ParameterModes.Length);
        builder.Append(';');
        foreach (var mode in type.ParameterModes)
        {
            AppendInvariant(builder, (int)mode);
            builder.Append(';');
        }
        var effects = type.Effects.Select(effect => ConstructedTypeOrderKey(effect, cache))
            .OrderBy(key => key, StringComparer.Ordinal).ToArray();
        AppendInvariant(builder, effects.Length);
        builder.Append(';');
        foreach (var effect in effects) { AppendField(builder, effect); }

        var result = builder.ToString();
        cache.Add(type.Id, result);
        return result;
    }

    private static void AppendField(StringBuilder builder, string value)
    {
        AppendInvariant(builder, value.Length);
        builder.Append(':').Append(value);
    }

    private static void AppendInvariant(StringBuilder builder, int value) =>
        builder.Append(value.ToString(CultureInfo.InvariantCulture));

    private static ConstructedOwnerProjection ProjectionLimit(string reason) =>
        new(OwnerProjectionStatus.LimitExceeded, [], reason);

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
        var leftParameters = _analysis.GetTypeParameters(leftFunction);
        var rightParameters = _analysis.GetTypeParameters(rightFunction);
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
            var leftType = _analysis.GetType(leftParameter.Type);
            var rightType = _analysis.GetType(rightParameter.Type);
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
        var parameters = _analysis.GetTypeParameters(symbol.Parts[0]);
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

        return ParameterBounds(identity).Select(_analysis.GetType)
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
