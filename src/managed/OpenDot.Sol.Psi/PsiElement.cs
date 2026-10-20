// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Text;

namespace OpenDot.Sol.Psi;

/// <summary>A read-only concrete-source view. Identity, parent, and range are scoped to Snapshot.</summary>
public abstract class PsiElement
{
    private bool _initialized;
    private ImmutableArray<PsiElement> _children = [];

    internal PsiElement(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent)
    {
        Snapshot = snapshot;
        Syntax = syntax;
        Parent = parent;
    }

    public PsiSnapshot Snapshot { get; }
    public SyntaxNode Syntax { get; }
    public NodeKind Kind => Syntax.Kind;
    public TextSpan Span => Syntax.Span;
    public string Text => Snapshot.Source.GetText(Span);
    public PsiElement? Parent { get; }
    public ImmutableArray<PsiElement> Children => _children;

    /// <summary>Physical lexer tokens intersecting this span; a combined generic closer can extend beyond it.</summary>
    public IEnumerable<SyntaxToken> Tokens => Snapshot.GetTokens(Span);

    public IEnumerable<PsiElement> Ancestors()
    {
        for (var ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent) { yield return ancestor; }
    }

    public IEnumerable<PsiElement> Descendants()
    {
        var pending = new Stack<PsiElement>(Children.Reverse());
        while (pending.TryPop(out var element))
        {
            yield return element;
            for (int index = element.Children.Length - 1; index >= 0; index--) { pending.Push(element.Children[index]); }
        }
    }

    internal void InitializeChildren(ImmutableArray<PsiElement> children)
    {
        if (_initialized) { throw new InvalidOperationException("PSI children are immutable after construction."); }
        _children = children;
        _initialized = true;
    }

    protected PsiElement? Child(NodeKind kind) => Children.FirstOrDefault(child => child.Kind == kind);
}

internal sealed class PsiSyntaxElement(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent)
    : PsiElement(snapshot, syntax, parent);

public sealed class PsiName : PsiElement
{
    internal PsiName(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent) : base(snapshot, syntax, parent) { }
}

public sealed class PsiFile : PsiElement
{
    internal PsiFile(PsiSnapshot snapshot, SyntaxNode syntax) : base(snapshot, syntax, null) { }
    public string? ModuleName => Snapshot.Syntax.ModuleName;
    public IEnumerable<PsiDeclaration> Declarations => Children.OfType<PsiDeclaration>();
    public IEnumerable<PsiImport> Imports => Children.OfType<PsiImport>();
    public IEnumerable<PsiAnnotation> FileAnnotations => Children.OfType<PsiAnnotation>().Where(a => a.Kind == NodeKind.FileAnnotation);
}

public sealed class PsiImport : PsiElement
{
    internal PsiImport(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent) : base(snapshot, syntax, parent) { }
    public string ModuleName => string.Join('.', Child(NodeKind.QualifiedName)?.Children.OfType<PsiName>().Select(n => n.Text) ?? []);
    public string? Alias => Children.OfType<PsiName>().FirstOrDefault(n => n.Span.Start < Child(NodeKind.QualifiedName)?.Span.Start)?.Text;
}

public sealed class PsiAnnotation : PsiElement
{
    internal PsiAnnotation(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent) : base(snapshot, syntax, parent) { }
    public string Name => string.Join('.', Child(NodeKind.QualifiedName)?.Children.OfType<PsiName>().Select(n => n.Text) ?? []);
    public string? Target => Child(NodeKind.AnnotationTarget)?.Text;
    public IEnumerable<PsiElement> Arguments => Child(NodeKind.ArgumentList)?.Children ?? [];
}

public sealed class PsiTypeReference : PsiElement
{
    internal PsiTypeReference(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent) : base(snapshot, syntax, parent) { }
    public IEnumerable<PsiTypeReference> TypeArguments => Child(NodeKind.TypeArgumentList)?.Children.OfType<PsiTypeReference>() ?? [];
}

public sealed class PsiTypeParameter : PsiElement
{
    internal PsiTypeParameter(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent) : base(snapshot, syntax, parent) { }
    public string? Name => Children.OfType<PsiName>().FirstOrDefault()?.Text;
    public PsiTypeReference? Constraint => Children.OfType<PsiTypeReference>().FirstOrDefault();
}

public sealed class PsiLambda : PsiElement
{
    internal PsiLambda(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent) : base(snapshot, syntax, parent) { }
    public IEnumerable<PsiElement> Parameters => Child(NodeKind.LambdaParameters)?.Children ?? [];
    public IEnumerable<PsiElement> Statements => Children.Where(child => child.Kind != NodeKind.LambdaParameters);
}
