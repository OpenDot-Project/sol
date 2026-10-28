// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Analysis;

/// <summary>Explicit lexical scopes and unqualified value-name binding, without expression type inference.</summary>
public sealed class LexicalAnalysis
{
    private readonly Dictionary<AstNode, int> _scopeOf;
    private readonly Dictionary<AstAtom, NameBinding> _bindings;
    private readonly Dictionary<AstNode, AstSnapshot> _files;
    private readonly Dictionary<AstLambda, ImmutableArray<LambdaCapture>> _capturesByLambda;
    private readonly Dictionary<AstLambda, ImmutableArray<LambdaReceiverCapture>> _receiverCapturesByLambda;
    private readonly Dictionary<AstAnonymousFunction, ImmutableArray<AnonymousFunctionCapture>> _capturesByAnonymousFunction;
    private readonly Dictionary<AstAnonymousFunction, ImmutableArray<AnonymousFunctionReceiverCapture>> _receiverCapturesByAnonymousFunction;
    private readonly Dictionary<AstFunction, ImmutableArray<LocalFunctionCapture>> _capturesByLocalFunction;
    private readonly Dictionary<AstFunction, ImmutableArray<LocalFunctionReceiverCapture>> _receiverCapturesByLocalFunction;
    private readonly ImmutableHashSet<LocalSymbol> _capturedLocals;
    private readonly Dictionary<AstLambda, LambdaEscape> _escapesByLambda;
    private readonly Dictionary<AstAnonymousFunction, AnonymousFunctionEscape> _escapesByAnonymousFunction;
    private readonly Dictionary<AstFunction, LocalFunctionEscape> _escapesByLocalFunction;
    private readonly TypeAnalysis _types;

    private LexicalAnalysis(LexicalBuilder builder, TypeAnalysis types)
    {
        _types = types;
        Declarations = builder.Session;
        Scopes = builder.Scopes.Select(s => new LexicalScope(s.Id, s.Parent, s.Owner, s.Symbols.ToImmutableArray())).ToImmutableArray();
        _scopeOf = builder.ScopeOf;
        _files = builder.Files;
        _bindings = new(ReferenceEqualityComparer.Instance);
        var diagnostics = builder.Diagnostics;
        foreach (var pair in builder.ScopeOf)
        {
            builder.Cancellation.ThrowIfCancellationRequested();
            if (pair.Key is not AstAtom { SyntaxKind: NodeKind.NameExpression } reference) { continue; }
            if (string.IsNullOrWhiteSpace(reference.Spelling) || reference.Spelling is "this" or "super" or "default") { continue; }
            var lookup = Lookup(reference, reference.Spelling);
            bool capture = lookup.Locals.Any(local => local.Kind != LocalSymbolKind.ConstructorProperty
                && CrossesLambda(pair.Value, local.ScopeId));
            _bindings.Add(reference, new(reference, lookup, capture));
            if (lookup.Declarations.Kind == LookupKind.Ambiguous && diagnostics.Count < Declarations.Options.DiagnosticLimit)
            {
                diagnostics.Add(new("SOL2002", "Ambiguous value name: " + reference.Spelling, reference.Origin.Source, reference.Origin.Span));
            }
        }
        LambdaCaptures = LambdaCaptureAnalysis.Create(Scopes, _scopeOf, _files, _bindings.Values,
            builder.Cancellation);
        IEqualityComparer<AstLambda> lambdaComparer = ReferenceEqualityComparer.Instance;
        _capturesByLambda = LambdaCaptures.GroupBy(capture => capture.Lambda, lambdaComparer)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray(), lambdaComparer);
        _capturedLocals = _bindings.Values.Where(binding => binding.CapturesOuterLocal)
            .SelectMany(binding => binding.Lookup.Locals).ToImmutableHashSet();
        LambdaReceiverCaptures = LambdaReceiverCaptureAnalysis.Create(Scopes, _scopeOf, _files,
            _bindings.Values, types, builder.Cancellation);
        _receiverCapturesByLambda = LambdaReceiverCaptures.GroupBy(capture => capture.Lambda, lambdaComparer)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray(), lambdaComparer);
        AnonymousFunctionCaptures = AnonymousFunctionCaptureAnalysis.Create(Scopes, _scopeOf, _files,
            _bindings.Values, builder.Cancellation);
        IEqualityComparer<AstAnonymousFunction> anonymousFunctionComparer = ReferenceEqualityComparer.Instance;
        _capturesByAnonymousFunction = AnonymousFunctionCaptures.GroupBy(capture => capture.Function,
                anonymousFunctionComparer)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray(), anonymousFunctionComparer);
        AnonymousFunctionReceiverCaptures = AnonymousFunctionReceiverCaptureAnalysis.Create(Scopes, _scopeOf,
            _files, _bindings.Values, types, builder.Cancellation);
        _receiverCapturesByAnonymousFunction = AnonymousFunctionReceiverCaptures
            .GroupBy(capture => capture.Function, anonymousFunctionComparer)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray(), anonymousFunctionComparer);
        LocalFunctionCaptures = LocalFunctionCaptureAnalysis.Create(Scopes, _scopeOf, _files,
            _bindings.Values, builder.Cancellation);
        IEqualityComparer<AstFunction> functionComparer = ReferenceEqualityComparer.Instance;
        _capturesByLocalFunction = LocalFunctionCaptures.GroupBy(capture => capture.Function, functionComparer)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray(), functionComparer);
        LocalFunctionReceiverCaptures = LocalFunctionReceiverCaptureAnalysis.Create(Scopes, _scopeOf, _files,
            _bindings.Values, types, builder.Cancellation);
        _receiverCapturesByLocalFunction = LocalFunctionReceiverCaptures.GroupBy(
                capture => capture.Function, functionComparer)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray(), functionComparer);
        LambdaEscapes = LambdaEscapeAnalysis.Create(Scopes, _scopeOf, _files, _bindings.Values,
            LambdaCaptures, Declarations, types, builder.Cancellation);
        _escapesByLambda = LambdaEscapes.ToDictionary(escape => escape.Lambda, lambdaComparer);
        AnonymousFunctionEscapes = AnonymousFunctionEscapeAnalysis.Create(Scopes, _scopeOf, _files,
            _bindings.Values, AnonymousFunctionCaptures, AnonymousFunctionReceiverCaptures,
            Declarations, types, builder.Cancellation);
        _escapesByAnonymousFunction = AnonymousFunctionEscapes.ToDictionary(escape => escape.Function,
            anonymousFunctionComparer);
        var localFunctionEscapes = LocalFunctionEscapeAnalysis.Create(this, builder.Cancellation);
        LocalFunctionEscapes = localFunctionEscapes.Escapes;
        _escapesByLocalFunction = LocalFunctionEscapes.ToDictionary(escape => escape.Function,
            functionComparer);
        Diagnostics = diagnostics.OrderBy(d => d.Source.Path, StringComparer.Ordinal).ThenBy(d => d.Span.Start).ToImmutableArray();
    }

    public AnalysisSession Declarations { get; }
    public ImmutableArray<LexicalScope> Scopes { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public IEnumerable<NameBinding> Bindings => _bindings.Values;
    /// <summary>Lexical local captures grouped by lambda; this inventory does not prove escape safety.</summary>
    public ImmutableArray<LambdaCapture> LambdaCaptures { get; }
    /// <summary>Explicit and implicit enclosing receivers used by lambdas, separate from local captures.</summary>
    public ImmutableArray<LambdaReceiverCapture> LambdaReceiverCaptures { get; }
    /// <summary>Local-symbol capture inventory for anonymous functions, independent of lambda captures.</summary>
    public ImmutableArray<AnonymousFunctionCapture> AnonymousFunctionCaptures { get; }
    /// <summary>Enclosing receiver inventory for anonymous functions, separate from local-symbol captures.</summary>
    public ImmutableArray<AnonymousFunctionReceiverCapture> AnonymousFunctionReceiverCaptures { get; }
    /// <summary>Resolved local symbols referenced across nested local named-function boundaries.</summary>
    public ImmutableArray<LocalFunctionCapture> LocalFunctionCaptures { get; }
    /// <summary>Enclosing nominal receivers and implicit instance members required by local functions.</summary>
    public ImmutableArray<LocalFunctionReceiverCapture> LocalFunctionReceiverCaptures { get; }
    /// <summary>Conservative source use/lifetime facts; only direct invocation proves non-escape.</summary>
    public ImmutableArray<LambdaEscape> LambdaEscapes { get; }
    /// <summary>Conservative source-use classifications for anonymous-function values.</summary>
    public ImmutableArray<AnonymousFunctionEscape> AnonymousFunctionEscapes { get; }
    /// <summary>Conservative value-lifetime classifications for local named functions.</summary>
    public ImmutableArray<LocalFunctionEscape> LocalFunctionEscapes { get; }
    internal TypeAnalysis Types => _types;
    public bool HasErrors => Declarations.HasErrors || Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>Returns captures for an owned lambda, including an empty result for a noncapturing lambda.</summary>
    public ImmutableArray<LambdaCapture> GetLambdaCaptures(AstLambda lambda)
    {
        ArgumentNullException.ThrowIfNull(lambda);
        if (!_scopeOf.ContainsKey(lambda)) { throw new ArgumentException("The lambda belongs to another lexical snapshot.", nameof(lambda)); }
        return _capturesByLambda.GetValueOrDefault(lambda, []);
    }

    /// <summary>Returns enclosing receiver captures for an owned lambda, empty when none are used.</summary>
    public ImmutableArray<LambdaReceiverCapture> GetLambdaReceiverCaptures(AstLambda lambda)
    {
        ArgumentNullException.ThrowIfNull(lambda);
        if (!_scopeOf.ContainsKey(lambda)) { throw new ArgumentException("The lambda belongs to another lexical snapshot.", nameof(lambda)); }
        return _receiverCapturesByLambda.GetValueOrDefault(lambda, []);
    }

    /// <summary>Returns local captures for an owned anonymous function; an uncaptured function returns empty.</summary>
    public ImmutableArray<AnonymousFunctionCapture> GetAnonymousFunctionCaptures(AstAnonymousFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (!_scopeOf.ContainsKey(function))
        { throw new ArgumentException("The anonymous function belongs to another lexical snapshot.", nameof(function)); }
        return _capturesByAnonymousFunction.GetValueOrDefault(function, []);
    }

    /// <summary>Returns enclosing-local references captured by an owned local named function.</summary>
    public ImmutableArray<LocalFunctionCapture> GetLocalFunctionCaptures(AstFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (!_scopeOf.ContainsKey(function))
        { throw new ArgumentException("The function belongs to another lexical snapshot.", nameof(function)); }
        return _capturesByLocalFunction.GetValueOrDefault(function, []);
    }

    /// <summary>Returns enclosing receiver requirements for an owned local named function.</summary>
    public ImmutableArray<LocalFunctionReceiverCapture> GetLocalFunctionReceiverCaptures(AstFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (!_scopeOf.ContainsKey(function))
        { throw new ArgumentException("The function belongs to another lexical snapshot.", nameof(function)); }
        return _receiverCapturesByLocalFunction.GetValueOrDefault(function, []);
    }

    /// <summary>Returns receiver captures for an owned anonymous function; no receiver use returns empty.</summary>
    public ImmutableArray<AnonymousFunctionReceiverCapture> GetAnonymousFunctionReceiverCaptures(
        AstAnonymousFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (!_scopeOf.ContainsKey(function))
        { throw new ArgumentException("The anonymous function belongs to another lexical snapshot.", nameof(function)); }
        return _receiverCapturesByAnonymousFunction.GetValueOrDefault(function, []);
    }

    /// <summary>Returns the lifetime classification for an owned lambda source node.</summary>
    public LambdaEscape GetLambdaEscape(AstLambda lambda)
    {
        ArgumentNullException.ThrowIfNull(lambda);
        if (!_scopeOf.ContainsKey(lambda)) { throw new ArgumentException("The lambda belongs to another lexical snapshot.", nameof(lambda)); }
        return _escapesByLambda[lambda];
    }

    /// <summary>Returns source-use facts for an owned anonymous function expression.</summary>
    public AnonymousFunctionEscape GetAnonymousFunctionEscape(AstAnonymousFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (!_scopeOf.ContainsKey(function))
        { throw new ArgumentException("The anonymous function belongs to another lexical snapshot.", nameof(function)); }
        return _escapesByAnonymousFunction[function];
    }

    /// <summary>Returns the source escape facts for an owned local named function.</summary>
    public LocalFunctionEscape GetLocalFunctionEscape(AstFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (!_scopeOf.ContainsKey(function))
        { throw new ArgumentException("The function belongs to another lexical snapshot.", nameof(function)); }
        if (_escapesByLocalFunction.TryGetValue(function, out var escape)) { return escape; }
        throw new ArgumentException("The function is not a local named-function declaration.", nameof(function));
    }

    internal bool IsCaptured(LocalSymbol local) => _capturedLocals.Contains(local);

    internal bool TryGetScopeId(AstNode node, out int scopeId) => _scopeOf.TryGetValue(node, out scopeId);

    public static LexicalAnalysis Create(AnalysisSession declarations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        cancellationToken.ThrowIfCancellationRequested();
        return Create(declarations, TypeAnalysis.Create(declarations, cancellationToken), cancellationToken);
    }

    internal static LexicalAnalysis Create(AnalysisSession declarations, TypeAnalysis types,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        ArgumentNullException.ThrowIfNull(types);
        if (!ReferenceEquals(declarations, types.Declarations))
        { throw new ArgumentException("The type analysis belongs to another declaration session.", nameof(types)); }
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new LexicalBuilder(declarations, cancellationToken);
        builder.Build();
        return new(builder, types);
    }

    public LexicalLookup Lookup(AstNode location, string name)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_scopeOf.TryGetValue(location, out int index))
        {
            throw new ArgumentException("The location is not owned by this lexical analysis.", nameof(location));
        }
        int offset = location.Origin.Span.Start;
        for (int? current = index; current is not null; current = Scopes[current.Value].ParentId)
        {
            var local = Scopes[current.Value].Symbols.Where(s => s.Name == name && s.VisibleFrom <= offset).ToImmutableArray();
            if (!local.IsEmpty) { return new(local, new(LookupKind.Missing, LookupScope.None, [])); }
        }
        var file = _files[location];
        DeclarationSymbol? container = null;
        for (AstNode? node = location; node is not null; node = file.GetParent(node))
        {
            if (node is AstTypeDeclaration declaration)
            {
                container = Declarations.GetDeclaredSymbol(declaration);
                break;
            }
        }
        return new([], Declarations.Lookup(file.Root, name, container));
    }

    public NameBinding GetBinding(AstAtom reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return _bindings.TryGetValue(reference, out var binding) ? binding
            : throw new ArgumentException("This node is not an owned unqualified value reference.", nameof(reference));
    }

    private bool CrossesLambda(int start, int target)
    {
        for (int? scope = start; scope is not null && scope != target; scope = Scopes[scope.Value].ParentId)
        {
            var owner = Scopes[scope.Value].Owner;
            if (owner is AstLambda or AstFunction || owner.SyntaxKind == NodeKind.AnonymousFunctionExpression) { return true; }
        }
        return false;
    }
}
