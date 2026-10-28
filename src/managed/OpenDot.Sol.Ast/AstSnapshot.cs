// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;

namespace OpenDot.Sol.Ast;

/// <summary>Normalizes one PSI snapshot without binding names or loading native libraries.</summary>
public sealed partial class AstSnapshot
{
    private readonly Dictionary<PsiElement, AstNode> _nodes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<AstNode, AstNode> _parents = new(ReferenceEqualityComparer.Instance);

    private AstSnapshot(PsiSnapshot psi, CancellationToken cancellationToken)
    {
        Psi = psi;
        var ordered = new List<PsiElement> { psi.Root };
        var pending = new Stack<PsiElement>();
        pending.Push(psi.Root);
        while (pending.TryPop(out var parent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var child in parent.Children)
            {
                ordered.Add(child);
                pending.Push(child);
            }
        }
        for (int index = ordered.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var element = ordered[index];
            var children = element.Children.Select(child => _nodes[child]).ToImmutableArray();
            var node = Convert(index, new(element), children);
            _nodes.Add(element, node);
            foreach (var child in children)
            {
                _parents.Add(child, node);
            }
        }
        Root = (AstFile)_nodes[psi.Root];
        Nodes = ordered.Select(element => _nodes[element]).ToImmutableArray();
    }

    public PsiSnapshot Psi { get; }
    public AstFile Root { get; }
    public ImmutableArray<AstNode> Nodes { get; }
    public bool HasSyntaxErrors => Psi.Syntax.HasErrors;

    public static AstSnapshot Create(PsiSnapshot psi, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(psi);
        cancellationToken.ThrowIfCancellationRequested();
        return new(psi, cancellationToken);
    }

    public AstNode GetNode(PsiElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return _nodes.TryGetValue(element, out var node) ? node
            : throw new ArgumentException("The PSI element belongs to another snapshot.", nameof(element));
    }

    public AstNode? GetParent(AstNode node)
    {
        ValidateOwnership(node);
        return _parents.GetValueOrDefault(node);
    }

    public AstNode FindNode(int byteOffset)
    {
        return GetNode(Psi.FindElement(byteOffset));
    }

    public void ValidateOwnership(AstNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_nodes.TryGetValue(node.Origin.Element, out var owned) || !ReferenceEquals(node, owned))
        {
            throw new ArgumentException("The AST node belongs to another snapshot.", nameof(node));
        }
    }

    private AstNode Convert(int id, AstOrigin origin, ImmutableArray<AstNode> children)
    {
        return origin.Element.Kind switch
        {
            NodeKind.CompilationUnit => new AstFile(id, origin, children),
            NodeKind.ImportDeclaration => new AstImport(id, origin, children, ImportItems(origin)),
            NodeKind.Type => new AstType(id, origin, children, TypeNormalizer.Create(origin, children)),
            NodeKind.TypeParameter => new AstTypeParameter(id, origin, children),
            NodeKind.ClassDeclaration or NodeKind.StructDeclaration or NodeKind.RefStructDeclaration
                or NodeKind.InterfaceDeclaration or NodeKind.ObjectDeclaration or NodeKind.EnumDeclaration
                or NodeKind.AnnotationDeclaration => new AstTypeDeclaration(id, origin, children),
            NodeKind.FunctionDeclaration => new AstFunction(id, origin, children, Parameters(children), Body(children)),
            NodeKind.PrimaryConstructor or NodeKind.ConstructorDeclaration => new AstConstructor(id, origin, children, Parameters(children)),
            NodeKind.Binding or NodeKind.PropertyDeclaration => new AstProperty(id, origin, children),
            NodeKind.AccessorDeclaration => new AstAccessor(id, origin, children, Body(children)),
            NodeKind.IndexerDeclaration => new AstIndexer(id, origin, children, Parameters(children)),
            NodeKind.InitDeclaration => new AstInitializer(id, origin, children),
            NodeKind.EnumVariant => new AstEnumVariant(id, origin, children, Parameters(children)),
            NodeKind.ExternDeclaration => new AstExtern(id, origin, children),
            NodeKind.Block => new AstBlock(id, origin, children),
            NodeKind.ExpressionStatement => new AstExpressionStatement(id, origin, children),
            NodeKind.ReturnStatement => new AstReturn(id, origin, children),
            NodeKind.Assignment => new AstAssignment(id, origin, children, Operator(origin)),
            NodeKind.IfStatement => new AstIf(id, origin, children),
            NodeKind.ForStatement => new AstFor(id, origin, children),
            NodeKind.SwitchStatement => new AstSwitch(id, origin, children),
            NodeKind.SwitchCase => new AstCase(id, origin, children),
            NodeKind.TryStatement or NodeKind.TryExpression => new AstTry(id, origin, children),
            NodeKind.CatchClause => new AstCatch(id, origin, children),
            NodeKind.CallExpression => new AstCall(id, origin, children, Arguments(children)),
            NodeKind.MemberExpression => new AstMember(id, origin, children, Operator(origin) == "?."),
            NodeKind.GenericExpression => new AstGenericApplication(id, origin, children),
            NodeKind.UnaryExpression or NodeKind.BinaryExpression or NodeKind.CastExpression
                or NodeKind.TypeTestExpression => new AstOperation(id, origin, children, Operator(origin)),
            NodeKind.LiteralExpression or NodeKind.NameExpression or NodeKind.PositionalExpression => new AstAtom(id, origin, children),
            NodeKind.TupleExpression or NodeKind.ArrayExpression => new AstSequence(id, origin, children,
                origin.Element.Kind == NodeKind.TupleExpression && children.Length == 1
                && !OwnTokens(origin).Any(t => t.Kind == SyntaxKind.CommaToken)),
            NodeKind.LambdaExpression => Lambda(id, origin, children),
            NodeKind.AnonymousFunctionExpression => AnonymousFunction(id, origin, children),
            NodeKind.LabelledExpression => new AstLabelledExpression(id, origin, children),
            NodeKind.IndexExpression or NodeKind.ThrowExpression
                or NodeKind.RefExpression => new AstExpression(id, origin, children),
            NodeKind.ThrowStatement or NodeKind.BreakStatement or NodeKind.ContinueStatement
                or NodeKind.DeferStatement or NodeKind.UnsafeStatement => new AstStatement(id, origin, children),
            _ => new AstStructure(id, origin, children),
        };
    }

    private static IEnumerable<SyntaxToken> OwnTokens(AstOrigin origin)
    {
        return origin.Element.Tokens.Where(token => !origin.Element.Children.Any(child =>
            child.Span.Start <= token.Span.Start && child.Span.End >= token.Span.End));
    }

    private static string Operator(AstOrigin origin)
    {
        return string.Concat(OwnTokens(origin).Where(t => t.Kind != SyntaxKind.SemicolonToken)
            .Select(t => origin.Source.GetText(t.Span)));
    }

    private static AstBody Body(ImmutableArray<AstNode> children)
    {
        var node = children.LastOrDefault(n => n is AstExpression or AstBlock or AstAssignment);
        return new(node is null ? AstBodyKind.Missing : node is AstBlock ? AstBodyKind.Block : AstBodyKind.Expression, node);
    }

    private static AstLambda Lambda(int id, AstOrigin origin, ImmutableArray<AstNode> children)
    {
        var positions = new SortedSet<int>();
        bool unrepresentable = false;
        var pending = new Stack<AstNode>(children.Reverse());
        while (pending.TryPop(out var node))
        {
            if (node is AstLambda || node.SyntaxKind == NodeKind.AnonymousFunctionExpression)
            {
                continue;
            }
            if (node.SyntaxKind == NodeKind.PositionalExpression)
            {
                if (int.TryParse(node.Origin.Text.AsSpan(1), out int position) && position < int.MaxValue)
                {
                    positions.Add(position);
                }
                else { unrepresentable = true; }
            }
            foreach (var child in node.Children) { pending.Push(child); }
        }
        bool named = children.Any(n => n.SyntaxKind == NodeKind.LambdaParameters);
        return new(id, origin, children, named ? AstLambdaForm.Named
            : positions.Count != 0 || unrepresentable ? AstLambdaForm.Positional : AstLambdaForm.Headerless,
            positions.ToImmutableArray(), unrepresentable);
    }
}
