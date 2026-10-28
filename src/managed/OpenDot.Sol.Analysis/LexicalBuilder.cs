// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Analysis;

internal sealed class LexicalBuilder
{
    internal sealed record ScopeData(int Id, int? Parent, AstNode Owner, List<LocalSymbol> Symbols);
    private int _nextSymbol;

    internal LexicalBuilder(AnalysisSession session, CancellationToken cancellation)
    {
        Session = session;
        Cancellation = cancellation;
        foreach (var file in session.Files)
        {
            foreach (var node in file.Nodes) { Files.Add(node, file); }
        }
    }

    internal AnalysisSession Session { get; }
    internal CancellationToken Cancellation { get; }
    internal List<ScopeData> Scopes { get; } = [];
    internal Dictionary<AstNode, int> ScopeOf { get; } = new(ReferenceEqualityComparer.Instance);
    internal Dictionary<AstNode, AstSnapshot> Files { get; } = new(ReferenceEqualityComparer.Instance);
    internal List<Diagnostic> Diagnostics { get; } = [];

    internal void Build()
    {
        foreach (var file in Session.Files)
        {
            int root = NewScope(file.Root, null);
            var pending = new Stack<(AstNode Node, int Scope)>();
            pending.Push((file.Root, root));
            while (pending.TryPop(out var frame))
            {
                Cancellation.ThrowIfCancellationRequested();
                var node = frame.Node;
                int scope = frame.Scope;
                if (OpensScope(node) && node is not AstFile)
                {
                    if (node is AstFunction && Scopes[scope].Owner is not (AstTypeDeclaration or AstFile))
                    {
                        Add(scope, ((AstFunction)node).Name, LocalSymbolKind.Function, node.Origin,
                            null, false, node.Origin.Span.Start);
                    }
                    scope = NewScope(node, scope);
                }
                if (IsInitializationContext(node))
                {
                    if (Scopes[scope].Owner != node) { scope = NewScope(node, scope); }
                    AddConstructorContext(node, scope);
                }
                ScopeOf.Add(node, scope);
                AddDeclarations(node, scope);
                for (int index = node.Children.Length - 1; index >= 0; index--)
                {
                    pending.Push((node.Children[index], scope));
                }
            }
        }
    }

    private static bool OpensScope(AstNode node)
    {
        return node is AstBlock or AstCallable or AstLambda or AstFor or AstCase or AstCatch or AstTypeDeclaration
            || node.SyntaxKind == NodeKind.AnonymousFunctionExpression;
    }

    private int NewScope(AstNode owner, int? parent)
    {
        int id = Scopes.Count;
        Scopes.Add(new(id, parent, owner, []));
        return id;
    }

    private void AddDeclarations(AstNode node, int scope)
    {
        switch (node)
        {
            case AstCallable callable:
                foreach (var parameter in callable.Parameters)
                {
                    Add(scope, parameter.Name, LocalSymbolKind.Parameter, parameter.NameOrigin,
                        parameter.Type, parameter.PassingMode is AstPassingMode.Ref or AstPassingMode.Out,
                        parameter.DefaultValue?.Origin.Span.End ?? parameter.NameOrigin.Span.End,
                        parameter.PassingMode);
                }
                if (callable is AstAccessor { Name: "set" or "init" })
                {
                    var property = Ancestors(node).OfType<AstProperty>().FirstOrDefault();
                    Add(scope, "value", LocalSymbolKind.AccessorValue, node.Origin, property?.Type, false, node.Origin.Span.Start);
                }
                break;
            case AstTypeDeclaration type:
                if (type.PrimaryConstructor is not null)
                {
                    foreach (var parameter in type.PrimaryConstructor.Parameters.Where(p => p.IsProperty))
                    {
                        Add(scope, parameter.Name, LocalSymbolKind.ConstructorProperty, parameter.NameOrigin,
                            parameter.Type, parameter.IsMutable, type.Origin.Span.Start);
                    }
                }
                break;
            case AstLambda lambda:
                foreach (var parameter in lambda.Parameters)
                {
                    var name = parameter.Children.FirstOrDefault(n => n.SyntaxKind == NodeKind.Name);
                    if (name is null) { continue; }
                    Add(scope, name.Origin.Text, LocalSymbolKind.Parameter, name.Origin,
                        parameter.Children.OfType<AstType>().FirstOrDefault(),
                        parameter.Children.Any(n => n.SyntaxKind == NodeKind.PassingMode && n.Origin.Text is "ref" or "out"),
                        name.Origin.Span.End, PassingMode(parameter));
                }
                break;
            case AstCatch handler:
                Add(scope, handler.Name, LocalSymbolKind.Catch, handler.Origin, handler.ExceptionType, false,
                    handler.Body?.Origin.Span.Start ?? handler.Origin.Span.End);
                break;
            case AstCase @case:
                var bindings = @case.Pattern?.Children.FirstOrDefault(n => n.SyntaxKind == NodeKind.PatternBindings);
                if (bindings is not null)
                {
                    foreach (var name in bindings.Children)
                    {
                        Add(scope, name.Origin.Text, LocalSymbolKind.Pattern, name.Origin, null, false,
                            @case.Pattern!.Origin.Span.End);
                    }
                }
                break;
            case AstFor { Form: AstForForm.Each } loop when loop.Binding is not null:
                foreach (string name in loop.Binding.BindingNames)
                {
                    Add(scope, name, LocalSymbolKind.Variable, loop.Binding.Origin, null, loop.Binding.IsMutable,
                        loop.Body?.Origin.Span.Start ?? loop.Origin.Span.End);
                }
                break;
            case AstProperty property:
                if (!Ancestors(property).Any(n => n is AstCallable or AstLambda)
                    || Files[property].GetParent(property)?.SyntaxKind == NodeKind.ForEachBinding) { break; }
                foreach (string name in property.BindingNames)
                {
                    Add(scope, name, LocalSymbolKind.Variable, property.Origin, property.Type, property.IsMutable,
                        property.Origin.Span.End);
                }
                break;
        }
        if (node.SyntaxKind == NodeKind.OutVariable)
        {
            foreach (var name in node.Children.Where(n => n.SyntaxKind == NodeKind.Name))
            {
                Add(scope, name.Origin.Text, LocalSymbolKind.Variable, name.Origin, null, true, node.Origin.Span.End);
            }
        }
        if (node.SyntaxKind == NodeKind.AnonymousFunctionExpression)
        {
            var list = node.Children.FirstOrDefault(n => n.SyntaxKind == NodeKind.ParameterList);
            foreach (var group in list?.Children ?? [])
            {
                foreach (var name in group.Children.Where(n => n.SyntaxKind == NodeKind.Name))
                {
                    Add(scope, name.Origin.Text, LocalSymbolKind.Parameter, name.Origin,
                        group.Children.OfType<AstType>().FirstOrDefault(),
                        PassingMode(group) is AstPassingMode.Ref or AstPassingMode.Out,
                        name.Origin.Span.End, PassingMode(group));
                }
            }
        }
    }

    private IEnumerable<AstNode> Ancestors(AstNode node)
    {
        var file = Files[node];
        for (var parent = file.GetParent(node); parent is not null; parent = file.GetParent(parent))
        {
            yield return parent;
        }
    }

    private bool IsInitializationContext(AstNode node)
    {
        var ancestors = Ancestors(node).ToArray();
        if (ancestors.TakeWhile(n => n is not AstTypeDeclaration).Any(n => n is AstCallable or AstLambda)) { return false; }
        if (ancestors.OfType<AstTypeDeclaration>().FirstOrDefault()?.PrimaryConstructor is null) { return false; }
        if (node is AstInitializer || node.SyntaxKind == NodeKind.BaseList) { return true; }
        var parent = Files[node].GetParent(node);
        return node is AstExpression && (parent is AstProperty
            || parent?.SyntaxKind == NodeKind.Delegation && Files[node].GetParent(parent) is AstProperty);
    }

    private void AddConstructorContext(AstNode node, int scope)
    {
        // Constructor-only parameters are visible to instance initialization, not ordinary methods.
        if (Ancestors(node).TakeWhile(n => n is not AstTypeDeclaration).Any(n => n is AstCallable or AstLambda)) { return; }
        var type = Ancestors(node).OfType<AstTypeDeclaration>().FirstOrDefault();
        if (type?.PrimaryConstructor is null) { return; }
        foreach (var parameter in type.PrimaryConstructor.Parameters.Where(p => !p.IsProperty))
        {
            Add(scope, parameter.Name, LocalSymbolKind.Parameter, parameter.NameOrigin, parameter.Type,
                false, type.Origin.Span.Start);
        }
    }

    private static AstPassingMode PassingMode(AstNode node)
        => node.Children.FirstOrDefault(child => child.SyntaxKind == NodeKind.PassingMode)?.Origin.Text switch
        {
            "ref" => AstPassingMode.Ref,
            "out" => AstPassingMode.Out,
            _ => AstPassingMode.Value,
        };

    private void Add(int scope, string? name, LocalSymbolKind kind, AstOrigin origin,
        AstType? type, bool mutable, int visible, AstPassingMode passingMode = AstPassingMode.Value)
    {
        if (string.IsNullOrEmpty(name) || name == "_") { return; }
        var symbols = Scopes[scope].Symbols;
        bool conflict = symbols.Any(symbol => symbol.Name == name
            && (kind != LocalSymbolKind.Function || symbol.Kind != LocalSymbolKind.Function));
        if (conflict && Diagnostics.Count < Session.Options.DiagnosticLimit)
        {
            Diagnostics.Add(new("SOL2120", "Duplicate lexical declaration: " + name, origin.Source, origin.Span));
        }
        symbols.Add(new(_nextSymbol++, name, kind, origin, type, mutable, visible, scope)
        { PassingMode = passingMode });
    }
}
