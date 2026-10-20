// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    ImmutableArray<ResolvedType> ParameterTypes, ResolvedType? ReturnType,
    ImmutableArray<CallArgumentBinding> Arguments, ImmutableArray<int> DefaultParameters);

/// <summary>
/// Optional body checking over an immutable source set. Deferred facts are explicit proof gaps;
/// HasErrors=false alone never certifies a program. No FIR or executable output is produced.
/// </summary>
public sealed class BodyAnalysis
{
    private readonly ImmutableDictionary<AstExpression, ExpressionBinding> _expressions;
    private readonly ImmutableDictionary<AstCallable, BodySummary> _bodies;
    private readonly ImmutableHashSet<AstNode> _nodes;

    private BodyAnalysis(BodyBindingBuilder builder)
    {
        Sources = builder.Sources;
        Types = builder.Types;
        Calls = builder.Calls.Values.ToImmutableArray();
        _expressions = builder.Expressions.ToImmutableDictionary();
        _bodies = builder.Bodies.ToImmutableDictionary();
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
}
