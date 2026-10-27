// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Analysis;

public enum BodyBindingStatus { Bound, Invalid, Deferred }

/// <summary>A source-owned expression fact, not an executable intermediate representation.</summary>
public sealed record ExpressionBinding(AstExpression Expression, ResolvedType? Type,
    BodyBindingStatus Status, bool IsWritable, bool IsStorage, ImmutableArray<ResolvedType> Effects);

public sealed record BodySummary(AstCallable Declaration, ResolvedType? ReturnType,
    bool CanCompleteNormally, BodyBindingStatus Status, ImmutableArray<ResolvedType> EscapingEffects);

/// <summary>Receiver lookup retains the substituted declaring type for generic base members.</summary>
public sealed record MemberBinding(DeclarationSymbol Symbol, ResolvedType DeclaringType);

public sealed record BodyCallBinding(AstCall Call, AstCallable Target, ResolvedType? Receiver,
    ImmutableArray<ResolvedType> TypeArguments, ImmutableArray<ResolvedType> ParameterTypes, ResolvedType? ReturnType,
    ImmutableArray<CallArgumentBinding> Arguments, ImmutableArray<int> DefaultParameters)
{
    public ImmutableArray<int> EmptyVarargParameters { get; init; } = [];
}

/// <summary>Selected source operator target and its candidate-local closed signature.</summary>
public sealed record BodyOperatorBinding(AstNode Site, string Operator, AstFunction Target,
    ImmutableArray<ResolvedType> TypeArguments, ImmutableArray<ResolvedType> ParameterTypes,
    ResolvedType? ReturnType, ImmutableArray<ResolvedType> Effects, bool UsesNullableLifting);

/// <summary>Closed semantic facts for one generic function instance within one BodyAnalysis.</summary>
internal sealed class SpecializedBodyAnalysis
{
    private readonly ImmutableDictionary<AstExpression, ExpressionBinding> _expressions;
    private readonly ImmutableDictionary<AstCallable, BodySummary> _bodies;
    private readonly ImmutableDictionary<AstCall, BodyCallBinding> _calls;
    private readonly ImmutableDictionary<AstNode, BodyOperatorBinding> _operators;
    private readonly ImmutableHashSet<AstNode> _nodes;

    internal SpecializedBodyAnalysis(BodyBindingBuilder builder, AstFunction function,
        ImmutableArray<ResolvedType> typeArguments, BodySummary summary)
    {
        Sources = builder.Sources;
        Declaration = function;
        TypeArguments = typeArguments;
        Summary = summary;
        _expressions = builder.Expressions.ToImmutableDictionary();
        _bodies = builder.Bodies.ToImmutableDictionary();
        _calls = builder.Calls.ToImmutableDictionary();
        _operators = builder.OperatorBindings.ToImmutableDictionary();
        _nodes = builder.Files.Keys.ToImmutableHashSet();
        Expressions = _expressions.Values.OrderBy(binding => binding.Expression.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(binding => binding.Expression.Origin.Span.Start).ToImmutableArray();
        Calls = _calls.Values.OrderBy(binding => binding.Call.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(binding => binding.Call.Origin.Span.Start).ToImmutableArray();
        OperatorBindings = _operators.Values.OrderBy(binding => binding.Site.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(binding => binding.Site.Origin.Span.Start).ToImmutableArray();
        Bodies = _bodies.Values.OrderBy(body => body.Declaration.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(body => body.Declaration.Origin.Span.Start).ToImmutableArray();
        UnverifiedNodes = builder.Unverified.OrderBy(node => node.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(node => node.Origin.Span.Start).ToImmutableArray();
        Diagnostics = builder.Diagnostics.Distinct().OrderBy(diagnostic => diagnostic.Source.Path, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Span.Start).ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .Take(Sources.Declarations.Options.DiagnosticLimit).ToImmutableArray();
    }

    internal SourceAnalysis Sources { get; }
    internal AstFunction Declaration { get; }
    internal ImmutableArray<ResolvedType> TypeArguments { get; }
    internal BodySummary Summary { get; }
    internal ImmutableArray<ExpressionBinding> Expressions { get; }
    internal ImmutableArray<BodyCallBinding> Calls { get; }
    internal ImmutableArray<BodyOperatorBinding> OperatorBindings { get; }
    internal ImmutableArray<BodySummary> Bodies { get; }
    internal ImmutableArray<AstNode> UnverifiedNodes { get; }
    internal ImmutableArray<Diagnostic> Diagnostics { get; }

    internal ExpressionBinding? GetExpression(AstExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (!_nodes.Contains(expression)) { throw new ArgumentException("The expression belongs to another source analysis.", nameof(expression)); }
        return _expressions.GetValueOrDefault(expression);
    }

    internal BodySummary? GetBody(AstCallable declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (!_nodes.Contains(declaration)) { throw new ArgumentException("The declaration belongs to another source analysis.", nameof(declaration)); }
        return _bodies.GetValueOrDefault(declaration);
    }

    internal BodyCallBinding? GetCall(AstCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!_nodes.Contains(call)) { throw new ArgumentException("The call belongs to another source analysis.", nameof(call)); }
        return _calls.GetValueOrDefault(call);
    }

    internal BodyOperatorBinding? GetOperator(AstNode site)
    {
        ArgumentNullException.ThrowIfNull(site);
        if (!_nodes.Contains(site)) { throw new ArgumentException("The operator site belongs to another source analysis.", nameof(site)); }
        return _operators.GetValueOrDefault(site);
    }
}

/// <summary>
/// Optional body checking over an immutable source set. Deferred facts are explicit proof gaps;
/// HasErrors=false alone never certifies a program. No FIR or executable output is produced.
/// </summary>
public sealed class BodyAnalysis
{
    private readonly record struct GenericBodyKey(AstFunction Function, string TypeIds);
    private readonly ImmutableDictionary<AstExpression, ExpressionBinding> _expressions;
    private readonly ImmutableDictionary<AstCallable, BodySummary> _bodies;
    private readonly ImmutableDictionary<AstNode, BodyOperatorBinding> _operators;
    private readonly ImmutableHashSet<AstNode> _nodes;
    private readonly object _genericBodyGate = new();
    private readonly Dictionary<GenericBodyKey, SpecializedBodyAnalysis> _genericBodies = [];

    private BodyAnalysis(BodyBindingBuilder builder)
    {
        Sources = builder.Sources;
        Types = builder.Types;
        Calls = builder.Calls.Values.ToImmutableArray();
        OperatorBindings = builder.OperatorBindings.Values.ToImmutableArray();
        _expressions = builder.Expressions.ToImmutableDictionary();
        _bodies = builder.Bodies.ToImmutableDictionary();
        _operators = builder.OperatorBindings.ToImmutableDictionary();
        _nodes = builder.Files.Keys.ToImmutableHashSet();
        UnverifiedNodes = builder.Unverified.OrderBy(n => n.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(n => n.Origin.Span.Start).ToImmutableArray();
        Expressions = _expressions.Values.OrderBy(e => e.Expression.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(e => e.Expression.Origin.Span.Start).ToImmutableArray();
        Bodies = _bodies.Values.OrderBy(b => b.Declaration.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(b => b.Declaration.Origin.Span.Start).ToImmutableArray();
        Diagnostics = Sources.Diagnostics.Concat(builder.Diagnostics).Distinct()
            .OrderBy(d => d.Source.Path, StringComparer.Ordinal).ThenBy(d => d.Span.Start)
            .ThenBy(d => d.Code, StringComparer.Ordinal).Take(Sources.Declarations.Options.DiagnosticLimit).ToImmutableArray();
        IsComplete = !builder.Deferred && !HasErrors;
    }

    public SourceAnalysis Sources { get; }
    public TypeSpecialization Types { get; }
    public ImmutableArray<BodyCallBinding> Calls { get; }
    internal ImmutableArray<BodyOperatorBinding> OperatorBindings { get; }
    public ImmutableArray<ExpressionBinding> Expressions { get; }
    public ImmutableArray<BodySummary> Bodies { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public bool HasErrors => Sources.HasErrors || Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    public bool IsComplete { get; }
    /// <summary>Source locations still requiring a semantic proof; never silently treated as valid.</summary>
    public ImmutableArray<AstNode> UnverifiedNodes { get; }

    public static BodyAnalysis Create(SourceAnalysis sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new BodyBindingBuilder(sources, cancellationToken);
        builder.Build();
        return new(builder);
    }

    public ExpressionBinding? GetExpression(AstExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (!_nodes.Contains(expression)) { throw new ArgumentException("The expression belongs to another source analysis.", nameof(expression)); }
        return _expressions.GetValueOrDefault(expression);
    }

    public BodySummary? GetBody(AstCallable declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (!_nodes.Contains(declaration)) { throw new ArgumentException("The declaration belongs to another source analysis.", nameof(declaration)); }
        return _bodies.GetValueOrDefault(declaration);
    }

    internal BodyOperatorBinding? GetOperator(AstNode site)
    {
        ArgumentNullException.ThrowIfNull(site);
        if (!_nodes.Contains(site)) { throw new ArgumentException("The operator site belongs to another source analysis.", nameof(site)); }
        return _operators.GetValueOrDefault(site);
    }

    internal SpecializedBodyAnalysis GetGenericBody(AstFunction function,
        ImmutableArray<ResolvedType> typeArguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(function);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_nodes.Contains(function)) { throw new ArgumentException("The function belongs to another source analysis.", nameof(function)); }
        var parameters = Sources.Types.GetTypeParameters(function);
        if (parameters.IsEmpty || parameters.Length != typeArguments.Length || typeArguments.IsDefault)
        { throw new ArgumentException("A generic body instance requires every ordered type argument.", nameof(typeArguments)); }
        foreach (var argument in typeArguments)
        {
            Types.ValidateOwnership(argument);
            if (ContainsTypeParameter(argument))
            { throw new ArgumentException("A generic body instance requires closed type arguments.", nameof(typeArguments)); }
        }
        string ids = string.Join(",", typeArguments.Select(argument => argument.Id.ToString(CultureInfo.InvariantCulture)));
        var key = new GenericBodyKey(function, ids);
        // Binding a closed body is intentionally single-flight so concurrent requests cannot
        // duplicate its resource-bounded work. A monitor's ordinary lock acquisition cannot
        // observe cancellation while queued, so acquire in bounded waits instead.
        while (!Monitor.TryEnter(_genericBodyGate, millisecondsTimeout: 25))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_genericBodies.TryGetValue(key, out var cached)) { return cached; }
            var builder = new BodyBindingBuilder(Sources, Types, cancellationToken);
            var result = builder.BuildGenericBody(function, typeArguments);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Summary.Status == BodyBindingStatus.Bound
                && result.Diagnostics.IsEmpty && result.UnverifiedNodes.IsEmpty)
            { _genericBodies.Add(key, result); }
            return result;
        }
        finally { Monitor.Exit(_genericBodyGate); }
    }

    private static bool ContainsTypeParameter(ResolvedType type)
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
}
