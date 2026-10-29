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
    BodyBindingStatus Status, bool IsWritable, bool IsStorage, ImmutableArray<ResolvedType> Effects)
{
    /// <summary>The selected source declaration when this expression is a function value.</summary>
    public AstFunction? FunctionValueTarget { get; init; }

    /// <summary>Closed or enclosing-instance arguments selected for a generic function value.</summary>
    public ImmutableArray<ResolvedType> FunctionValueTypeArguments { get; init; } = [];
}

public sealed record BodySummary(AstCallable Declaration, ResolvedType? ReturnType,
    bool CanCompleteNormally, BodyBindingStatus Status, ImmutableArray<ResolvedType> EscapingEffects);

/// <summary>Definite-assignment result for one anonymous-function out parameter.</summary>
public sealed record AnonymousFunctionOutParameterSummary(AstParameter Parameter,
    bool AssignedOnEveryNormalExit);

/// <summary>
/// Source-flow facts for an anonymous function body. These facts do not define a closure layout or
/// guarantee that FIR can emit the function.
/// </summary>
/// <param name="NormalExitCount">Number of abstract reachable normal exit states, not runtime executions.</param>
/// <param name="HasReachableFallthrough">Whether the body retains a reachable fallthrough state.</param>
/// <param name="IsComplete">Whether this function's analyzed subtree has no known errors or deferred proof gaps.</param>
public sealed record AnonymousFunctionBodySummary(AstAnonymousFunction Function, ResolvedType FunctionType,
    int NormalExitCount, bool HasReachableFallthrough,
    ImmutableArray<AnonymousFunctionOutParameterSummary> OutParameters, bool IsComplete);

/// <summary>Definite-assignment result for one lambda out parameter.</summary>
public sealed record LambdaOutParameterSummary(AstNode Parameter,
    bool AssignedOnEveryNormalExit);

/// <summary>
/// Source-flow facts for one lambda body. These facts do not define a closure layout or guarantee
/// that FIR can emit the lambda.
/// </summary>
/// <param name="NormalExitCount">Number of abstract reachable normal exit states, not runtime executions.</param>
/// <param name="HasReachableFallthrough">Whether the body retains a reachable fallthrough state.</param>
/// <param name="IsComplete">Whether this lambda's analyzed subtree has no known errors or deferred proof gaps.</param>
public sealed record LambdaBodySummary(AstLambda Lambda, ResolvedType FunctionType,
    int NormalExitCount, bool HasReachableFallthrough,
    ImmutableArray<LambdaOutParameterSummary> OutParameters, bool IsComplete);

/// <summary>Receiver lookup retains the substituted declaring type for generic base members.</summary>
public sealed record MemberBinding(DeclarationSymbol Symbol, ResolvedType DeclaringType);

public sealed record BodyCallBinding(AstCall Call, AstCallable Target, ResolvedType? Receiver,
    ImmutableArray<ResolvedType> TypeArguments, ImmutableArray<ResolvedType> ParameterTypes, ResolvedType? ReturnType,
    ImmutableArray<CallArgumentBinding> Arguments, ImmutableArray<int> DefaultParameters)
{
    public ImmutableArray<int> EmptyVarargParameters { get; init; } = [];
    internal ResolvedType? SourceReceiver { get; init; }
}

/// <summary>Selected source operator target and its candidate-local closed signature.</summary>
public sealed record BodyOperatorBinding(AstNode Site, string Operator, AstFunction Target,
    ImmutableArray<ResolvedType> TypeArguments, ImmutableArray<ResolvedType> ParameterTypes,
    ResolvedType? ReturnType, ImmutableArray<ResolvedType> Effects, bool UsesNullableLifting);

/// <summary>Closed semantic facts for one generic function instance within one BodyAnalysis.</summary>
internal sealed partial class SpecializedBodyAnalysis
{
    private ImmutableDictionary<AstExpression, ExpressionBinding> _expressions;
    private ImmutableDictionary<AstCallable, BodySummary> _bodies;
    private readonly ImmutableDictionary<AstCall, BodyCallBinding> _calls;
    private readonly ImmutableDictionary<AstNode, BodyOperatorBinding> _operators;
    private readonly ImmutableHashSet<AstNode> _nodes;

    internal SpecializedBodyAnalysis(BodyBindingBuilder builder, AstFunction function,
        ImmutableArray<ResolvedType> typeArguments, BodySummary summary)
        : this(builder, function, [], typeArguments, null, summary) { }

    internal SpecializedBodyAnalysis(BodyBindingBuilder builder, AstFunction function,
        ImmutableArray<ResolvedType> ownerTypeArguments, ImmutableArray<ResolvedType> typeArguments,
        ResolvedType? constructedOwner, BodySummary summary)
    {
        Sources = builder.Sources;
        Declaration = function;
        OwnerTypeArguments = ownerTypeArguments;
        TypeArguments = typeArguments;
        ConstructedOwner = constructedOwner;
        Summary = summary;
        _expressions = builder.Expressions.ToImmutableDictionary();
        _bodies = builder.Bodies.ToImmutableDictionary();
        _calls = builder.Calls.ToImmutableDictionary();
        _operators = builder.OperatorBindings.ToImmutableDictionary();
        _nodes = builder.OwnedNodes;
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
    internal ImmutableArray<ResolvedType> OwnerTypeArguments { get; }
    internal ImmutableArray<ResolvedType> TypeArguments { get; }
    internal ResolvedType? ConstructedOwner { get; }
    internal BodySummary Summary { get; private set; }
    internal ImmutableArray<ExpressionBinding> Expressions { get; private set; }
    internal ImmutableArray<BodyCallBinding> Calls { get; }
    internal ImmutableArray<BodyOperatorBinding> OperatorBindings { get; }
    internal ImmutableArray<BodySummary> Bodies { get; private set; }
    internal ImmutableArray<AstNode> UnverifiedNodes { get; private set; }
    internal ImmutableArray<Diagnostic> Diagnostics { get; private set; }

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
/// Source-only projection of a member body from a source receiver to its canonical constructed
/// declaring owner. A body is present only when the inheritance graph yields one unique owner.
/// </summary>
internal sealed record GenericMemberBodyProjectionAnalysis(
    ConstructedOwnerProjection Projection,
    SpecializedBodyAnalysis? Body);

/// <summary>
/// Optional body checking over an immutable source set. Deferred facts are explicit proof gaps;
/// HasErrors=false alone never certifies a program. No FIR or executable output is produced.
/// </summary>
public sealed partial class BodyAnalysis
{
    private readonly record struct GenericBodyKey(AstFunction Function, string OwnerTypeIds, string MethodTypeIds);
    private readonly record struct GenericOwnerProjectionKey(int ReceiverTypeId, int OwnerSymbolId);
    private ImmutableDictionary<AstExpression, ExpressionBinding> _expressions;
    private ImmutableDictionary<AstCallable, BodySummary> _bodies;
    private ImmutableDictionary<AstAnonymousFunction, AnonymousFunctionBodySummary> _anonymousFunctionBodies;
    private ImmutableDictionary<AstLambda, LambdaBodySummary> _lambdaBodies;
    private readonly ImmutableDictionary<AstNode, BodyOperatorBinding> _operators;
    private readonly ImmutableHashSet<AstNode> _nodes;
    private readonly ImmutableHashSet<LocalSymbol> _localSymbols;
    private readonly ImmutableDictionary<LocalSymbol, ResolvedType> _localTypes;
    private readonly object _genericBodyGate = new();
    private readonly Dictionary<GenericBodyKey, SpecializedBodyAnalysis> _genericBodies = [];
    private readonly Dictionary<GenericBodyKey, SpecializedBodyAnalysis> _genericLocalBodies = [];
    private readonly Dictionary<GenericOwnerProjectionKey, ConstructedOwnerProjection> _genericOwnerProjections = [];

    private BodyAnalysis(BodyBindingBuilder builder)
    {
        Sources = builder.Sources;
        Types = builder.Types;
        Calls = builder.Calls.Values.ToImmutableArray();
        OperatorBindings = builder.OperatorBindings.Values.ToImmutableArray();
        _expressions = builder.Expressions.ToImmutableDictionary();
        _bodies = builder.Bodies.ToImmutableDictionary();
        _anonymousFunctionBodies = builder.AnonymousFunctionBodies.ToImmutableDictionary();
        _lambdaBodies = builder.LambdaBodies.ToImmutableDictionary();
        _operators = builder.OperatorBindings.ToImmutableDictionary();
        _nodes = builder.OwnedNodes;
        foreach (var (function, summary) in _anonymousFunctionBodies)
        {
            if (!_nodes.Contains(function) || !ReferenceEquals(function, summary.Function)
                || summary.NormalExitCount < 0
                || summary.OutParameters.Any(outParameter => !function.Parameters.Contains(outParameter.Parameter)))
            { throw new InvalidOperationException("Anonymous-function summary is not owned by its source analysis."); }
            Types.ValidateOwnership(summary.FunctionType);
        }
        foreach (var (lambda, summary) in _lambdaBodies)
        {
            if (!_nodes.Contains(lambda) || !ReferenceEquals(lambda, summary.Lambda)
                || summary.FunctionType.Kind != ResolvedTypeKind.Function
                || summary.NormalExitCount < 0
                || summary.HasReachableFallthrough && summary.NormalExitCount == 0
                || summary.OutParameters.Any(outParameter => !_nodes.Contains(outParameter.Parameter)
                    || !lambda.Parameters.Contains(outParameter.Parameter)))
            { throw new InvalidOperationException("Lambda summary is not owned by its source analysis."); }
            Types.ValidateOwnership(summary.FunctionType);
        }
        _localSymbols = ImmutableHashSet.CreateRange<LocalSymbol>(ReferenceEqualityComparer.Instance,
            Sources.Lexical.Scopes.SelectMany(scope => scope.Symbols));
        var localTypes = ImmutableDictionary.CreateBuilder<LocalSymbol, ResolvedType>(ReferenceEqualityComparer.Instance);
        foreach (var pair in builder.LocalTypes)
        {
            if (!_localSymbols.Contains(pair.Key))
            { throw new InvalidOperationException("Body analysis produced a type for a foreign local symbol."); }
            Types.ValidateOwnership(pair.Value);
            localTypes.Add(pair.Key, pair.Value);
        }
        _localTypes = localTypes.ToImmutable();
        UnverifiedNodes = builder.Unverified.OrderBy(n => n.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(n => n.Origin.Span.Start).ToImmutableArray();
        Expressions = _expressions.Values.OrderBy(e => e.Expression.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(e => e.Expression.Origin.Span.Start).ToImmutableArray();
        Bodies = _bodies.Values.OrderBy(b => b.Declaration.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(b => b.Declaration.Origin.Span.Start).ToImmutableArray();
        AnonymousFunctionBodies = _anonymousFunctionBodies.Values
            .OrderBy(summary => summary.Function.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(summary => summary.Function.Origin.Span.Start).ToImmutableArray();
        LambdaBodies = _lambdaBodies.Values
            .OrderBy(summary => summary.Lambda.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(summary => summary.Lambda.Origin.Span.Start).ToImmutableArray();
        Diagnostics = Sources.Diagnostics.Concat(builder.Diagnostics).Distinct()
            .OrderBy(d => d.Source.Path, StringComparer.Ordinal).ThenBy(d => d.Span.Start)
            .ThenBy(d => d.Code, StringComparer.Ordinal).Take(Sources.Declarations.Options.DiagnosticLimit).ToImmutableArray();
        IsComplete = !builder.Deferred && !HasErrors;
    }

    public SourceAnalysis Sources { get; }
    public TypeSpecialization Types { get; }
    public ImmutableArray<BodyCallBinding> Calls { get; }
    internal ImmutableArray<BodyOperatorBinding> OperatorBindings { get; }
    public ImmutableArray<ExpressionBinding> Expressions { get; private set; }
    public ImmutableArray<BodySummary> Bodies { get; private set; }
    /// <summary>Per-function body flow facts for anonymous functions with a resolved signature and analyzed body.</summary>
    public ImmutableArray<AnonymousFunctionBodySummary> AnonymousFunctionBodies { get; private set; }
    /// <summary>Per-lambda body-flow facts for lambdas with a resolved signature and analyzed body.</summary>
    public ImmutableArray<LambdaBodySummary> LambdaBodies { get; private set; }
    public ImmutableArray<Diagnostic> Diagnostics { get; private set; }
    public bool HasErrors => Sources.HasErrors || Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    public bool IsComplete { get; private set; }
    /// <summary>Source locations still requiring a semantic proof; never silently treated as valid.</summary>
    public ImmutableArray<AstNode> UnverifiedNodes { get; private set; }

    public static BodyAnalysis Create(SourceAnalysis sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new BodyBindingBuilder(sources, cancellationToken);
        // Return inference must be available while ordinary bodies are being built. This private
        // workspace contains only source/type ownership and never escapes as a completed analysis.
        var inferenceWorkspace = new BodyAnalysis(builder);
        builder.ClosedMemberReturn = inferenceWorkspace.ResolveClosedMemberReturn;
        builder.ClosedOwnerReturnTemplate = inferenceWorkspace.ResolveClosedOwnerReturnTemplate;
        builder.Build();
        var result = new BodyAnalysis(builder);
        result.AnalyzeGenericMemberCalls(cancellationToken);
        return result;
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

    /// <summary>
    /// Returns body-flow facts for an owned anonymous function, or null when its signature did not resolve
    /// or the source body is missing.
    /// </summary>
    public AnonymousFunctionBodySummary? GetAnonymousFunctionBody(AstAnonymousFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (!_nodes.Contains(function))
        { throw new ArgumentException("The anonymous function belongs to another source analysis.", nameof(function)); }
        return _anonymousFunctionBodies.GetValueOrDefault(function);
    }

    /// <summary>
    /// Returns body-flow facts for an owned lambda, or null when its signature could not be
    /// resolved or its body analysis did not reach a summary.
    /// </summary>
    public LambdaBodySummary? GetLambdaBody(AstLambda lambda)
    {
        ArgumentNullException.ThrowIfNull(lambda);
        if (!_nodes.Contains(lambda))
        { throw new ArgumentException("The lambda belongs to another source analysis.", nameof(lambda)); }
        return _lambdaBodies.GetValueOrDefault(lambda);
    }

    /// <summary>
    /// Returns a local's resolved semantic type, or null when this analysis has no non-error type
    /// proof for it. The result is analysis-owned and is not a runtime layout or storage guarantee.
    /// </summary>
    public ResolvedType? GetLocalType(LocalSymbol local)
    {
        ArgumentNullException.ThrowIfNull(local);
        if (!_localSymbols.Contains(local))
        { throw new ArgumentException("The local symbol is not owned by this source analysis.", nameof(local)); }
        if (_localTypes.TryGetValue(local, out var type))
        {
            return ContainsError(type) ? null : type;
        }
        if (local.WrittenType is null) { return null; }
        var written = Sources.Types.GetType(local.WrittenType);
        Types.ValidateOwnership(written);
        return ContainsError(written) ? null : written;
    }

    private static bool ContainsError(ResolvedType root)
    {
        var pending = new Stack<ResolvedType>();
        var seen = new HashSet<ResolvedType>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current)) { continue; }
            if (current.IsError) { return true; }
            foreach (var argument in current.Arguments) { pending.Push(argument); }
            foreach (var effect in current.Effects) { pending.Push(effect); }
        }
        return false;
    }

    internal BodyOperatorBinding? GetOperator(AstNode site)
    {
        ArgumentNullException.ThrowIfNull(site);
        if (!_nodes.Contains(site)) { throw new ArgumentException("The operator site belongs to another source analysis.", nameof(site)); }
        return _operators.GetValueOrDefault(site);
    }

    internal SpecializedBodyAnalysis GetGenericBody(AstFunction function,
        ImmutableArray<ResolvedType> typeArguments, CancellationToken cancellationToken)
        => GetGenericBodyCore(function, typeArguments, cancellationToken, includeDependencies: true);

    private SpecializedBodyAnalysis GetGenericBodyLocal(AstFunction function,
        ImmutableArray<ResolvedType> typeArguments, CancellationToken cancellationToken)
        => GetGenericBodyCore(function, typeArguments, cancellationToken, includeDependencies: false);

    private SpecializedBodyAnalysis GetGenericBodyCore(AstFunction function,
        ImmutableArray<ResolvedType> typeArguments, CancellationToken cancellationToken, bool includeDependencies)
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
        string methodTypeIds = TypeIds(typeArguments);
        var key = new GenericBodyKey(function, "", methodTypeIds);
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
            var cache = includeDependencies ? _genericBodies : _genericLocalBodies;
            if (cache.TryGetValue(key, out var cached)) { return cached; }
            SpecializedBodyAnalysis result;
            if (includeDependencies)
            {
                result = GetGenericBodyLocal(function, typeArguments, cancellationToken).CopyForDependencyProof();
                AnalyzeSpecializedMemberCalls(result, cancellationToken);
            }
            else
            {
                var builder = new BodyBindingBuilder(Sources, Types, cancellationToken)
                { ClosedMemberReturn = ResolveClosedMemberReturn, ClosedOwnerReturnTemplate = ResolveClosedOwnerReturnTemplate };
                result = builder.BuildGenericBody(function, typeArguments);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Summary.Status == BodyBindingStatus.Bound
                && result.Diagnostics.IsEmpty && result.UnverifiedNodes.IsEmpty)
            { cache.Add(key, result); }
            return result;
        }
        finally { Monitor.Exit(_genericBodyGate); }
    }

    internal SpecializedBodyAnalysis GetGenericMemberBody(AstFunction function, ResolvedType constructedOwner,
        ImmutableArray<ResolvedType> methodTypeArguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(constructedOwner);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_nodes.Contains(function))
        { throw new ArgumentException("The function belongs to another source analysis.", nameof(function)); }
        Types.ValidateOwnership(constructedOwner);

        var owner = GenericMethodSourcePolicy.DirectOwner(function, Sources);
        var ownerSymbol = owner is null ? null : Sources.Declarations.GetDeclaredSymbol(owner);
        if (owner is null || ownerSymbol is null || owner.Arity <= 0
            || ownerSymbol.Parts.IsEmpty
            || !owner.Members.Any(member => ReferenceEquals(member, function))
            || constructedOwner.Kind != ResolvedTypeKind.Nominal
            || !ReferenceEquals(constructedOwner.Declaration, ownerSymbol))
        { throw new ArgumentException("A generic member body requires its direct constructed owner type.", nameof(constructedOwner)); }

        var ownerFile = Sources.Declarations.Files.Single(file => file.Nodes.Contains(owner));
        for (var parent = ownerFile.GetParent(owner); parent is not null; parent = ownerFile.GetParent(parent))
        {
            if (parent is AstTypeDeclaration)
            { throw new ArgumentException("Nested generic owners are not supported for member body reanalysis.", nameof(function)); }
        }

        if (!ReferenceEquals(Sources.Declarations.GetDeclaredSymbol(ownerSymbol.Parts[0]), ownerSymbol)
            || ownerSymbol.Arity != constructedOwner.Arguments.Length
            || Sources.Types.GetTypeParameters(owner).Length != ownerSymbol.Arity)
        { throw new ArgumentException("The constructed owner is not a canonical closed generic declaration.", nameof(constructedOwner)); }

        var methodParameters = Sources.Types.GetTypeParameters(function);
        if (methodTypeArguments.IsDefault || methodParameters.Length != methodTypeArguments.Length)
        { throw new ArgumentException("A generic member body requires every ordered method type argument.", nameof(methodTypeArguments)); }
        if (constructedOwner.Arguments.IsDefault || constructedOwner.Arguments.Length != ownerSymbol.Arity)
        { throw new ArgumentException("The constructed owner has an incomplete type argument vector.", nameof(constructedOwner)); }

        foreach (var argument in constructedOwner.Arguments)
        { ValidateClosedBodyArgument(argument, nameof(constructedOwner)); }
        foreach (var argument in methodTypeArguments)
        { ValidateClosedBodyArgument(argument, nameof(methodTypeArguments)); }

        string ownerTypeIds = TypeIds(constructedOwner.Arguments);
        string methodTypeIds = TypeIds(methodTypeArguments);
        var key = new GenericBodyKey(function, ownerTypeIds, methodTypeIds);
        while (!Monitor.TryEnter(_genericBodyGate, millisecondsTimeout: 25))
        { cancellationToken.ThrowIfCancellationRequested(); }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_genericBodies.TryGetValue(key, out var cached)) { return cached; }
            var builder = new BodyBindingBuilder(Sources, Types, cancellationToken);
            builder.ClosedMemberReturn = ResolveClosedMemberReturn;
            builder.ClosedOwnerReturnTemplate = ResolveClosedOwnerReturnTemplate;
            var result = builder.BuildGenericMemberBody(function, constructedOwner, methodTypeArguments);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Summary.Status == BodyBindingStatus.Bound
                && result.Diagnostics.IsEmpty && result.UnverifiedNodes.IsEmpty)
            { _genericBodies.Add(key, result); }
            return result;
        }
        finally { Monitor.Exit(_genericBodyGate); }
    }

    /// <summary>
    /// Projects a source receiver through its constructed base graph to a generic method's direct
    /// declaring owner, then rebinds the body for that unique owner. This does not perform ordinary
    /// call selection or admit the result to FIR.
    /// </summary>
    internal GenericMemberBodyProjectionAnalysis GetGenericMemberBodyForReceiver(AstFunction function,
        ResolvedType receiver, ImmutableArray<ResolvedType> methodTypeArguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(receiver);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_nodes.Contains(function))
        { throw new ArgumentException("The function belongs to another source analysis.", nameof(function)); }

        var owner = GenericMethodSourcePolicy.DirectOwner(function, Sources);
        var ownerSymbol = owner is null ? null : Sources.Declarations.GetDeclaredSymbol(owner);
        if (owner is null || ownerSymbol is null || owner.Arity <= 0
            || ownerSymbol.Parts.IsEmpty
            || !owner.Members.Any(member => ReferenceEquals(member, function)))
        { throw new ArgumentException("A projected generic member body requires a directly declared generic owner method.", nameof(function)); }

        var ownerFile = Sources.Declarations.Files.Single(file => file.Nodes.Contains(owner));
        for (var parent = ownerFile.GetParent(owner); parent is not null; parent = ownerFile.GetParent(parent))
        {
            if (parent is AstTypeDeclaration)
            { throw new ArgumentException("Nested generic owners are not supported for member body reanalysis.", nameof(function)); }
        }

        var ownerParameters = Sources.Types.GetTypeParameters(owner);
        var methodParameters = Sources.Types.GetTypeParameters(function);
        if (!ReferenceEquals(Sources.Declarations.GetDeclaredSymbol(ownerSymbol.Parts[0]), ownerSymbol)
            || ownerParameters.Length != ownerSymbol.Arity || owner.Arity != ownerSymbol.Arity)
        { throw new ArgumentException("A projected generic member body requires a canonical generic owner.", nameof(function)); }
        if (methodTypeArguments.IsDefault || methodParameters.Length != methodTypeArguments.Length)
        { throw new ArgumentException("A projected generic member body requires every ordered method type argument.", nameof(methodTypeArguments)); }
        foreach (var argument in methodTypeArguments)
        { ValidateClosedBodyArgument(argument, nameof(methodTypeArguments)); }

        var projectionKey = new GenericOwnerProjectionKey(receiver.Id, ownerSymbol.Id);
        while (!Monitor.TryEnter(_genericBodyGate, millisecondsTimeout: 25))
        { cancellationToken.ThrowIfCancellationRequested(); }
        ConstructedOwnerProjection projection;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_genericOwnerProjections.TryGetValue(projectionKey, out var cachedProjection))
            {
                projection = cachedProjection;
            }
            else
            {
                var resolver = new MemberLookupResolver(Sources.Types, Types, BodySourceIndex.Get(Sources),
                    cancellationToken);
                projection = resolver.ProjectOwner(receiver, ownerSymbol, function);
                cancellationToken.ThrowIfCancellationRequested();
                _genericOwnerProjections.Add(projectionKey, projection);
            }
        }
        finally { Monitor.Exit(_genericBodyGate); }

        if (projection.Status != OwnerProjectionStatus.Unique)
        { return new(projection, null); }

        var candidate = projection.Candidates.Single();
        var body = GetGenericMemberBody(function, candidate.ConstructedOwner,
            methodTypeArguments, cancellationToken);
        return new(projection, body);
    }

    private void ValidateClosedBodyArgument(ResolvedType argument, string parameterName)
    {
        Types.ValidateOwnership(argument);
        if (ContainsTypeParameter(argument))
        { throw new ArgumentException("A generic member body requires closed type arguments.", parameterName); }
    }

    private static string TypeIds(ImmutableArray<ResolvedType> arguments) => string.Join(",",
        arguments.Select(argument => argument.Id.ToString(CultureInfo.InvariantCulture)));

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
