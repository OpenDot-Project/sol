// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;

namespace OpenDot.Sol.Ast;

/// <summary>A source origin owns its snapshot; offsets from other snapshots are never interchangeable.</summary>
public sealed record AstOrigin(PsiElement Element)
{
    public SourceText Source => Element.Snapshot.Source;
    public TextSpan Span => Element.Span;
    public string Text => Element.Text;
}

/// <summary>Immutable structural AST. No node carries resolved types, symbols, or executable code.</summary>
public abstract class AstNode
{
    internal AstNode(int id, AstOrigin origin, ImmutableArray<AstNode> children)
    {
        Id = id;
        Origin = origin;
        Children = children;
    }

    public int Id { get; }
    public AstOrigin Origin { get; }
    public NodeKind SyntaxKind => Origin.Element.Kind;
    public ImmutableArray<AstNode> Children { get; }

    public IEnumerable<AstNode> Descendants()
    {
        var pending = new Stack<AstNode>(Children.Reverse());
        while (pending.TryPop(out var node))
        {
            yield return node;
            for (int index = node.Children.Length - 1; index >= 0; index--)
            {
                pending.Push(node.Children[index]);
            }
        }
    }

    protected AstNode? Child(NodeKind kind)
    {
        return Children.FirstOrDefault(n => n.SyntaxKind == kind);
    }
}

public sealed class AstStructure : AstNode
{
    internal AstStructure(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }
}

public sealed class AstFile : AstNode
{
    internal AstFile(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public string? ModuleName => ((PsiFile)Origin.Element).ModuleName;
    public IEnumerable<AstDeclaration> Declarations => Children.OfType<AstDeclaration>();
    public IEnumerable<AstImport> Imports => Children.OfType<AstImport>();
    public IEnumerable<AstNode> Annotations => Children.Where(n => n.SyntaxKind == NodeKind.FileAnnotation);
}

public sealed class AstImport : AstNode
{
    internal AstImport(int id, AstOrigin origin, ImmutableArray<AstNode> children,
        ImmutableArray<AstImportItem> items)
        : base(id, origin, children)
    {
        Items = items;
    }

    public string ModuleName => ((PsiImport)Origin.Element).ModuleName;
    public string? Alias => ((PsiImport)Origin.Element).Alias;
    public bool IsPublic => Origin.Element.Children.Any(n => n.Kind == NodeKind.Modifier && n.Text == "public");
    public ImmutableArray<AstImportItem> Items { get; }
    public bool IsSelective => !Items.IsEmpty;
}

public sealed record AstImportItem(string LocalName, string OriginalName, AstOrigin Origin);

public enum AstBodyKind
{
    Missing,
    Expression,
    Block,
}

public sealed record AstBody(AstBodyKind Kind, AstNode? Node)
{
    /// <summary>The syntactic tail candidate. Expected return type and flow checking happen later.</summary>
    public AstExpression? TailExpression
    {
        get
        {
            if (Kind == AstBodyKind.Expression)
            {
                return Node as AstExpression;
            }
            if (Node is AstBlock block && block.Statements.LastOrDefault() is AstExpressionStatement statement)
            {
                return statement.Expression;
            }
            return null;
        }
    }
}
