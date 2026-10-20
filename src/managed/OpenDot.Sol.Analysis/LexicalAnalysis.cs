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

    private LexicalAnalysis(LexicalBuilder builder)
    {
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
        Diagnostics = diagnostics.OrderBy(d => d.Source.Path, StringComparer.Ordinal).ThenBy(d => d.Span.Start).ToImmutableArray();
    }

    public AnalysisSession Declarations { get; }
    public ImmutableArray<LexicalScope> Scopes { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public IEnumerable<NameBinding> Bindings => _bindings.Values;
    public bool HasErrors => Declarations.HasErrors || Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public static LexicalAnalysis Create(AnalysisSession declarations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new LexicalBuilder(declarations, cancellationToken);
        builder.Build();
        return new(builder);
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
