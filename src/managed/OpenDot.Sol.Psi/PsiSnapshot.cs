// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Text;

namespace OpenDot.Sol.Psi;

/// <summary>Owns immutable source views for one concrete syntax snapshot, without semantic binding or LLVM.</summary>
public sealed class PsiSnapshot
{
    private readonly Dictionary<SyntaxNode, PsiElement> _elements = new(ReferenceEqualityComparer.Instance);

    private PsiSnapshot(SyntaxTree syntax, CancellationToken cancellationToken)
    {
        if (syntax.Root.Kind != NodeKind.CompilationUnit) { throw new ArgumentException("A PSI snapshot requires a compilation-unit root.", nameof(syntax)); }
        Syntax = syntax;
        if (syntax.Root.Span.Start != 0 || syntax.Root.Span.End != syntax.Source.Length)
        {
            throw new ArgumentException("The compilation unit must cover the source snapshot.", nameof(syntax));
        }
        Root = (PsiFile)Create(syntax.Root, null);
        var pending = new Stack<PsiElement>();
        pending.Push(Root);
        while (pending.TryPop(out var parent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var children = ImmutableArray.CreateBuilder<PsiElement>(parent.Syntax.Children.Length);
            foreach (var child in parent.Syntax.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child.Span.Start < parent.Span.Start || child.Span.End > parent.Span.End)
                {
                    throw new ArgumentException("A child syntax span is outside its parent.", nameof(syntax));
                }
                var element = Create(child, parent);
                children.Add(element);
                pending.Push(element);
            }
            parent.InitializeChildren(children.MoveToImmutable());
        }
    }

    public SyntaxTree Syntax { get; }
    public SourceText Source => Syntax.Source;
    public PsiFile Root { get; }

    public static PsiSnapshot Create(SyntaxTree syntax, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        cancellationToken.ThrowIfCancellationRequested();
        return new(syntax, cancellationToken);
    }

    /// <summary>Returns the unique wrapper for a node from this snapshot; equal nodes from another snapshot are rejected.</summary>
    public PsiElement GetElement(SyntaxNode syntax)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        return _elements.TryGetValue(syntax, out var element) ? element
            : throw new ArgumentException("This node does not belong to the snapshot.", nameof(syntax));
    }

    /// <summary>Finds the deepest structural element containing a UTF-8 byte offset. EOF maps to the file.</summary>
    public PsiElement FindElement(int byteOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteOffset, Source.Length);
        PsiElement result = Root;
        bool found;
        do
        {
            found = false;
            foreach (var child in result.Children)
            {
                if (!child.Span.Contains(byteOffset)) { continue; }
                result = child;
                found = true;
                break;
            }
        } while (found);
        return result;
    }

    internal IEnumerable<SyntaxToken> GetTokens(TextSpan span)
    {
        if (span.IsEmpty) { yield break; }
        var tokens = Syntax.Tokens;
        int low = 0;
        int high = tokens.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (tokens[middle].Span.End <= span.Start) { low = middle + 1; }
            else { high = middle; }
        }
        for (int index = low; index < tokens.Length && tokens[index].Span.Start < span.End; index++)
        {
            yield return tokens[index];
        }
    }

    private PsiElement Create(SyntaxNode syntax, PsiElement? parent)
    {
        if (syntax.Kind == NodeKind.CompilationUnit && parent is not null) { throw new ArgumentException("A compilation unit cannot be nested.", nameof(syntax)); }
        Source.Validate(syntax.Span);
        PsiElement element = syntax.Kind switch
        {
            NodeKind.CompilationUnit => new PsiFile(this, syntax),
            NodeKind.ClassDeclaration or NodeKind.StructDeclaration or NodeKind.RefStructDeclaration
                or NodeKind.InterfaceDeclaration or NodeKind.ObjectDeclaration or NodeKind.EnumDeclaration
                or NodeKind.AnnotationDeclaration => new PsiTypeDeclaration(this, syntax, parent),
            NodeKind.FunctionDeclaration => new PsiFunction(this, syntax, parent),
            NodeKind.PrimaryConstructor or NodeKind.ConstructorDeclaration => new PsiConstructor(this, syntax, parent),
            NodeKind.PropertyDeclaration or NodeKind.Binding => new PsiProperty(this, syntax, parent),
            NodeKind.IndexerDeclaration => new PsiIndexer(this, syntax, parent),
            NodeKind.AccessorDeclaration => new PsiAccessor(this, syntax, parent),
            NodeKind.EnumVariant => new PsiEnumVariant(this, syntax, parent),
            NodeKind.InitDeclaration => new PsiInitializer(this, syntax, parent),
            NodeKind.ExternDeclaration => new PsiExtern(this, syntax, parent),
            NodeKind.ParameterGroup => new PsiParameterGroup(this, syntax, parent),
            NodeKind.Type => new PsiTypeReference(this, syntax, parent),
            NodeKind.TypeParameter => new PsiTypeParameter(this, syntax, parent),
            NodeKind.Annotation or NodeKind.FileAnnotation => new PsiAnnotation(this, syntax, parent),
            NodeKind.ImportDeclaration => new PsiImport(this, syntax, parent),
            NodeKind.LambdaExpression => new PsiLambda(this, syntax, parent),
            NodeKind.Name or NodeKind.OperatorName => new PsiName(this, syntax, parent),
            _ => new PsiSyntaxElement(this, syntax, parent),
        };
        if (!_elements.TryAdd(syntax, element)) { throw new ArgumentException("A concrete node cannot have multiple parents.", nameof(syntax)); }
        return element;
    }
}
