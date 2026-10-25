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

internal enum GenericInferenceRelation
{
    Input,
    Exact,
    Expected,
}

internal enum GenericInferenceStatus
{
    Solved,
    MissingEvidence,
    ConflictingEvidence,
    IncompatibleShape,
    ResourceLimit,
}

internal sealed record GenericInferenceConstraint(
    ResolvedType Template,
    ResolvedType Actual,
    GenericInferenceRelation Relation,
    string Origin);

internal sealed record GenericInferenceFact(
    ResolvedType Parameter,
    ResolvedType Type,
    GenericInferenceRelation Relation,
    string Origin);

internal sealed record GenericInferenceResult(
    GenericInferenceStatus Status,
    ImmutableDictionary<ResolvedType, ResolvedType> Substitution,
    ImmutableArray<ResolvedType> UnresolvedParameters,
    ImmutableArray<ResolvedType> ConflictingParameters,
    ImmutableArray<GenericInferenceFact> Evidence,
    string? Reason);

/// <summary>
/// Solves the source generic method/type arguments from argument and expected-result evidence.
/// This component is deliberately a bounded source-analysis service: it does not specialize a
/// callable body, create FIR, or choose an overload.
/// </summary>
internal sealed class GenericInferenceSolver
{
    private const int MaximumDepth = 128;
    private const int MaximumGraphNodes = 4096;
    private const int MaximumBoundsPerParameter = 256;

    private sealed class Evidence
    {
        internal List<ResolvedType> Lower { get; } = [];
        internal List<ResolvedType> Exact { get; } = [];
        internal List<ResolvedType> Upper { get; } = [];
        internal List<GenericInferenceFact> Facts { get; } = [];
    }

    private readonly TypeSpecialization _types;
    private readonly TypeAnalysis _analysis;
    private readonly CancellationToken _cancellation;

    internal GenericInferenceSolver(TypeSpecialization types, CancellationToken cancellationToken)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _analysis = types.Analysis;
        _cancellation = cancellationToken;
    }

    internal GenericInferenceResult Solve(
        ImmutableArray<ResolvedType> parameters,
        IReadOnlyDictionary<ResolvedType, ResolvedType> fixedSubstitution,
        IEnumerable<GenericInferenceConstraint> constraints)
    {
        ArgumentNullException.ThrowIfNull(fixedSubstitution);
        ArgumentNullException.ThrowIfNull(constraints);

        var parameterSet = new HashSet<ResolvedType>();
        var evidence = new Dictionary<ResolvedType, Evidence>();
        foreach (var parameter in parameters)
        {
            _types.ValidateOwnership(parameter);
            if (parameter.Kind != ResolvedTypeKind.Parameter || !parameterSet.Add(parameter))
            {
                throw new ArgumentException("Inference inputs must be unique generic parameters.", nameof(parameters));
            }
            evidence.Add(parameter, new Evidence());
        }
        foreach (var pair in fixedSubstitution)
        {
            _types.ValidateOwnership(pair.Key);
            _types.ValidateOwnership(pair.Value);
            if (pair.Key.Kind != ResolvedTypeKind.Parameter)
            {
                throw new ArgumentException("Substitution keys must be generic parameters.", nameof(fixedSubstitution));
            }
        }

        var fixedMap = new Dictionary<ResolvedType, ResolvedType>(fixedSubstitution);
        var missingShape = false;
        foreach (var constraint in constraints)
        {
            _cancellation.ThrowIfCancellationRequested();
            _types.ValidateOwnership(constraint.Template);
            _types.ValidateOwnership(constraint.Actual);
            var template = fixedMap.Count == 0 ? constraint.Template
                : _types.Substitute(constraint.Template, fixedMap, _cancellation);
            var actual = fixedMap.Count == 0 ? constraint.Actual
                : _types.Substitute(constraint.Actual, fixedMap, _cancellation);
            if (!Collect(template, actual, constraint.Relation, constraint.Origin,
                    parameterSet, evidence, fixedMap, 0))
            {
                missingShape = true;
            }
        }

        var solved = new Dictionary<ResolvedType, ResolvedType>(fixedMap);
        var unresolved = ImmutableArray.CreateBuilder<ResolvedType>();
        var conflicts = ImmutableArray.CreateBuilder<ResolvedType>();
        foreach (var parameter in parameters)
        {
            _cancellation.ThrowIfCancellationRequested();
            var bucket = evidence[parameter];
            if (!TryChoose(bucket, fixedMap, out var chosen, out bool conflict))
            {
                if (conflict) { conflicts.Add(parameter); }
                else { unresolved.Add(parameter); }
                continue;
            }
            solved.Add(parameter, chosen!);
        }

        if (conflicts.Count != 0)
        {
            return Result(GenericInferenceStatus.ConflictingEvidence, solved, unresolved, conflicts, evidence,
                "The argument and expected-result bounds do not have one most-specific type.");
        }
        if (missingShape)
        {
            return Result(GenericInferenceStatus.IncompatibleShape, solved, unresolved, conflicts, evidence,
                "A nested generic type shape could not be matched invariantly.");
        }
        if (unresolved.Count != 0)
        {
            return Result(GenericInferenceStatus.MissingEvidence, solved, unresolved, conflicts, evidence,
                "At least one generic parameter has no argument or expected-result evidence.");
        }
        return Result(GenericInferenceStatus.Solved, solved, unresolved, conflicts, evidence, null);
    }

    private GenericInferenceResult Result(GenericInferenceStatus status,
        Dictionary<ResolvedType, ResolvedType> substitution,
        ImmutableArray<ResolvedType>.Builder unresolved,
        ImmutableArray<ResolvedType>.Builder conflicts,
        Dictionary<ResolvedType, Evidence> evidence,
        string? reason)
    {
        var facts = evidence.Values.SelectMany(bucket => bucket.Facts)
            .OrderBy(fact => fact.Parameter.Id)
            .ThenBy(fact => fact.Relation)
            .ThenBy(fact => fact.Type.Id)
            .ThenBy(fact => fact.Origin, StringComparer.Ordinal)
            .ToImmutableArray();
        return new(status, substitution.ToImmutableDictionary(),
            unresolved.ToImmutable(), conflicts.ToImmutable(), facts, reason);
    }

    private bool Collect(ResolvedType template, ResolvedType actual, GenericInferenceRelation relation,
        string origin, HashSet<ResolvedType> parameters, Dictionary<ResolvedType, Evidence> buckets,
        IReadOnlyDictionary<ResolvedType, ResolvedType> fixedMap, int depth)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (depth > MaximumDepth) { throw new TypeSpecializationException(SpecializationFailure.ResourceLimit,
            "Generic inference nesting exceeds the 128-level budget."); }
        if (template.IsError || actual.IsError) { return false; }
        if (template.Kind == ResolvedTypeKind.Bottom || actual.Kind == ResolvedTypeKind.Bottom) { return true; }
        if (parameters.Contains(template))
        {
            AddEvidence(buckets[template], template, relation, actual, origin);
            return true;
        }
        if (!ContainsInferenceParameter(template, parameters)) { return true; }

        if (template.IsNullable)
        {
            var payload = actual.IsNullable ? actual.Arguments[0] : actual;
            return Collect(template.Arguments[0], payload, relation, origin, parameters, buckets, fixedMap, depth + 1);
        }
        if (actual.IsNullable) { return false; }

        if (template.Kind == ResolvedTypeKind.Nominal && template.Declaration is { } expectedDefinition
            && (actual.Kind != ResolvedTypeKind.Nominal || actual.Declaration != expectedDefinition))
        {
            if (relation == GenericInferenceRelation.Exact) { return false; }
            var projectionTarget = relation == GenericInferenceRelation.Expected
                ? actual.Declaration : expectedDefinition;
            if (projectionTarget is null) { return false; }
            var projections = relation == GenericInferenceRelation.Expected
                ? Project(template, projectionTarget, fixedMap)
                : Project(actual, projectionTarget, fixedMap);
            if (projections.IsEmpty) { return false; }
            foreach (var projection in projections)
            {
                var left = relation == GenericInferenceRelation.Expected ? projection : template;
                var right = relation == GenericInferenceRelation.Expected ? actual : projection;
                if (left.Declaration != right.Declaration || left.Arguments.Length != right.Arguments.Length) { return false; }
                for (int index = 0; index < left.Arguments.Length; index++)
                {
                    // Nominal applications are invariant. Projection changes only the nominal
                    // constructor; it never turns T in Box<T> into a covariant bound.
                    if (!Collect(left.Arguments[index], right.Arguments[index], GenericInferenceRelation.Exact,
                            origin, parameters, buckets, fixedMap, depth + 1)) { return false; }
                }
            }
            return true;
        }

        if (template.Kind != actual.Kind || template.Name != actual.Name
            || template.Declaration != actual.Declaration
            || template.Arguments.Length != actual.Arguments.Length
            || !template.ParameterModes.SequenceEqual(actual.ParameterModes))
        {
            return false;
        }

        for (int index = 0; index < template.Arguments.Length; index++)
        {
            if (!Collect(template.Arguments[index], actual.Arguments[index], GenericInferenceRelation.Exact,
                    origin, parameters, buckets, fixedMap, depth + 1)) { return false; }
        }

        return CollectEffectEvidence(template.Effects, actual.Effects, relation, origin,
            parameters, buckets, fixedMap, depth + 1);
    }

    private bool CollectEffectEvidence(ImmutableArray<ResolvedType> templates,
        ImmutableArray<ResolvedType> actuals, GenericInferenceRelation relation, string origin,
        HashSet<ResolvedType> parameters, Dictionary<ResolvedType, Evidence> buckets,
        IReadOnlyDictionary<ResolvedType, ResolvedType> fixedMap, int depth)
    {
        // Effect sets are unordered. Infer the single open effect pattern only when every fixed
        // member has an exact counterpart and exactly one actual member remains. Otherwise leave
        // effect-only parameters unresolved; a positional guess could bind the wrong exception.
        var open = templates.Where(effect => ContainsInferenceParameter(effect, parameters)).ToArray();
        if (open.Length != 1 || templates.Length != actuals.Length) { return true; }

        var remaining = actuals.ToList();
        foreach (var fixedEffect in templates.Where(effect => !ContainsInferenceParameter(effect, parameters)))
        {
            int index = remaining.FindIndex(actual => ReferenceEquals(actual, fixedEffect));
            if (index < 0) { return true; }
            remaining.RemoveAt(index);
        }
        if (remaining.Count != 1) { return true; }
        return Collect(open[0], remaining[0], GenericInferenceRelation.Exact, origin,
            parameters, buckets, fixedMap, depth);
    }

    private static void AddEvidence(Evidence evidence, ResolvedType parameter,
        GenericInferenceRelation relation, ResolvedType type, string origin)
    {
        var fact = new GenericInferenceFact(parameter, type, relation, origin);
        if (!evidence.Facts.Contains(fact)) { evidence.Facts.Add(fact); }
        var target = relation switch
        {
            GenericInferenceRelation.Input => evidence.Lower,
            GenericInferenceRelation.Exact => evidence.Exact,
            GenericInferenceRelation.Expected => evidence.Upper,
            _ => throw new ArgumentOutOfRangeException(nameof(relation)),
        };
        if (!target.Contains(type)) { target.Add(type); }
        if (target.Count > MaximumBoundsPerParameter)
        {
            throw new TypeSpecializationException(SpecializationFailure.ResourceLimit,
                "Generic inference exceeds the 256-bound budget for one type parameter.");
        }
    }

    private bool TryChoose(Evidence evidence, IReadOnlyDictionary<ResolvedType, ResolvedType> fixedMap,
        out ResolvedType? chosen, out bool conflict)
    {
        chosen = null;
        conflict = false;
        if (evidence.Exact.Count > 1)
        {
            conflict = true;
            return false;
        }
        if (evidence.Exact.Count == 1)
        {
            var exact = evidence.Exact[0];
            chosen = exact;
            conflict = !evidence.Lower.All(lower => Converts(lower, exact))
                || !evidence.Upper.All(upper => Converts(exact, upper));
            return !conflict;
        }

        var candidates = evidence.Lower.Count != 0
            ? TypeClosure(evidence.Lower[0], fixedMap)
            : evidence.Upper.Distinct().ToImmutableArray();
        var valid = candidates.Where(candidate =>
            evidence.Lower.All(lower => Converts(lower, candidate))
            && evidence.Upper.All(upper => Converts(candidate, upper)))
            .Distinct().ToArray();
        if (valid.Length == 0)
        {
            conflict = evidence.Lower.Count != 0 || evidence.Upper.Count != 0;
            return false;
        }

        var minimal = valid.Where(candidate => !valid.Any(other => other != candidate
            && Converts(other, candidate) && !Converts(candidate, other))).ToArray();
        if (minimal.Length != 1)
        {
            conflict = minimal.Length > 1;
            return false;
        }
        chosen = minimal[0];
        return true;
    }

    private ImmutableArray<ResolvedType> TypeClosure(ResolvedType start,
        IReadOnlyDictionary<ResolvedType, ResolvedType> fixedMap)
    {
        var result = ImmutableArray.CreateBuilder<ResolvedType>();
        var pending = new Queue<(ResolvedType Type, int Depth)>();
        var visited = new HashSet<int>();
        pending.Enqueue((start, 0));
        while (pending.TryDequeue(out var item))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (item.Depth > MaximumDepth) { throw new TypeSpecializationException(SpecializationFailure.ResourceLimit,
                "Generic inference inheritance exceeds the 128-level budget."); }
            if (!visited.Add(item.Type.Id)) { continue; }
            if (visited.Count > MaximumGraphNodes) { throw new TypeSpecializationException(SpecializationFailure.ResourceLimit,
                "Generic inference inheritance exceeds the 4096-node budget."); }
            result.Add(item.Type);
            foreach (var parent in DirectBases(item.Type, fixedMap))
            { pending.Enqueue((parent, item.Depth + 1)); }
        }
        return result.ToImmutable();
    }

    private ImmutableArray<ResolvedType> Project(ResolvedType source, DeclarationSymbol target,
        IReadOnlyDictionary<ResolvedType, ResolvedType> fixedMap)
    {
        var matches = ImmutableArray.CreateBuilder<ResolvedType>();
        var pending = new Queue<(ResolvedType Type, int Depth)>();
        var visited = new HashSet<int>();
        pending.Enqueue((source, 0));
        while (pending.TryDequeue(out var item))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (item.Depth > MaximumDepth) { throw new TypeSpecializationException(SpecializationFailure.ResourceLimit,
                "Generic projection exceeds the 128-level inheritance budget."); }
            if (!visited.Add(item.Type.Id)) { continue; }
            if (visited.Count > MaximumGraphNodes) { throw new TypeSpecializationException(SpecializationFailure.ResourceLimit,
                "Generic projection exceeds the 4096-node budget."); }
            if (item.Type.Declaration == target)
            {
                matches.Add(item.Type);
                continue;
            }
            foreach (var parent in DirectBases(item.Type, fixedMap)) { pending.Enqueue((parent, item.Depth + 1)); }
        }
        return matches.Distinct().OrderBy(type => type.Id).ToImmutableArray();
    }

    private ImmutableArray<ResolvedType> DirectBases(ResolvedType type,
        IReadOnlyDictionary<ResolvedType, ResolvedType> fixedMap)
    {
        if (type.IsNullable)
        {
            return DirectBases(type.Arguments[0], fixedMap).Select(_types.GetNullable)
                .Distinct().OrderBy(parent => parent.Id).ToImmutableArray();
        }
        if (type.Kind == ResolvedTypeKind.Nominal)
        {
            var bases = _types.GetDirectBases(type, _cancellation).ToBuilder();
            bool valueType = type.Declaration?.Parts[0].SyntaxKind is NodeKind.StructDeclaration
                or NodeKind.RefStructDeclaration or NodeKind.EnumDeclaration;
            if (valueType && !type.IsRefLike && CoreObject() is { } objectType && !bases.Contains(objectType))
            { bases.Add(objectType); }
            return bases.ToImmutable();
        }
        if (type.Kind is ResolvedTypeKind.Primitive or ResolvedTypeKind.Array)
        {
            return CoreObject() is { } objectType ? [objectType] : [];
        }
        if (type.Kind != ResolvedTypeKind.Parameter || type.Parameter is not { } identity) { return []; }

        var bounds = ImmutableArray.CreateBuilder<ResolvedType>();
        foreach (var syntax in ParameterBounds(identity))
        {
            var bound = _analysis.GetType(syntax);
            bounds.Add(fixedMap.Count == 0 ? bound : _types.Substitute(bound, fixedMap, _cancellation));
        }
        return bounds.Distinct().OrderBy(bound => bound.Id).ToImmutableArray();
    }

    private ResolvedType? CoreObject()
    {
        var declarations = _analysis.Declarations.Modules.GetValueOrDefault("std.core")?.Declarations
            .Where(symbol => symbol.Kind == SymbolKind.Type && symbol.Name == "Object" && symbol.Arity == 0)
            .Take(2).ToArray() ?? [];
        return declarations.Length == 1 ? _types.Nominal(declarations[0], []) : null;
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

    private bool ContainsInferenceParameter(ResolvedType type, HashSet<ResolvedType> parameters)
    {
        var pending = new Stack<ResolvedType>();
        var visited = new HashSet<int>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (!visited.Add(current.Id)) { continue; }
            if (parameters.Contains(current)) { return true; }
            foreach (var child in current.Arguments.Concat(current.Effects)) { pending.Push(child); }
        }
        return false;
    }

    private bool Converts(ResolvedType source, ResolvedType target) =>
        _types.ClassifyConversion(source, target, _cancellation) != IntrinsicConversion.None;
}
