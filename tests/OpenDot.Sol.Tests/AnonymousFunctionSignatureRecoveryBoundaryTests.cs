// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Text;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionSignatureRecoveryBoundaryTests
{
    [Fact]
    public void MissingParameterCloseRetainsGroupedDefaultReturnTypeBodyAndFollowingDeclaration()
    {
        const string declaration = "val callback = func(first, second: int = 40: int { first }";
        string source = "module test\r\n// café and 🧪 before the signature\r\n" + declaration
            + "\r\nval after: int = 42\r\n";
        var syntax = SyntaxTree.Parse(SourceText.FromString(source, "recovered-signature.sol"),
            cancellationToken: TestContext.Current.CancellationToken);
        var functionSyntax = Assert.Single(Descendants(syntax.Root), node =>
            node.Kind == NodeKind.AnonymousFunctionExpression);
        var parameterListSyntax = Assert.Single(functionSyntax.Children,
            node => node.Kind == NodeKind.ParameterList);
        var parameterGroupSyntax = Assert.Single(parameterListSyntax.Children);
        var defaultSyntax = Assert.Single(parameterGroupSyntax.Children,
            node => node.Kind == NodeKind.LiteralExpression);
        var returnTypeSyntax = Assert.Single(functionSyntax.Children, node => node.Kind == NodeKind.Type);
        var bodySyntax = Assert.Single(functionSyntax.Children, node => node.Kind == NodeKind.Block);
        int defaultCharacterOffset = source.IndexOf("40", StringComparison.Ordinal);
        int defaultByteOffset = Encoding.UTF8.GetByteCount(source[..defaultCharacterOffset]);

        Assert.True(syntax.HasErrors);
        Assert.Contains(syntax.Diagnostics, diagnostic => diagnostic.Code == "SOL1101");
        Assert.Equal(new[] { "first", "second" }, parameterGroupSyntax.Children
            .Where(node => node.Kind == NodeKind.Name)
            .Select(node => syntax.Source.GetText(node.Span)));
        Assert.Equal("40", syntax.Source.GetText(defaultSyntax.Span));
        Assert.Equal("int", syntax.Source.GetText(returnTypeSyntax.Span));
        Assert.Equal("{ first }", syntax.Source.GetText(bodySyntax.Span));
        Assert.Equal(Encoding.UTF8.GetByteCount(source), syntax.Root.Span.End);

        var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        AstVerifier.Verify(ast, TestContext.Current.CancellationToken);
        var function = Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>());
        var firstDefault = Assert.IsType<AstAtom>(function.Parameters[0].DefaultValue);
        var secondDefault = Assert.IsType<AstAtom>(function.Parameters[1].DefaultValue);

        Assert.Equal(new[] { "first", "second" }, function.Parameters.Select(parameter => parameter.Name));
        Assert.All(function.Parameters, parameter =>
            Assert.Equal("int", parameter.Type!.Shape.CanonicalName));
        Assert.Same(firstDefault, secondDefault);
        Assert.Equal("40", firstDefault.Origin.Text);
        Assert.Same(syntax.Source, firstDefault.Origin.Source);
        Assert.Equal(defaultByteOffset, firstDefault.Origin.Span.Start);
        Assert.Equal(2, firstDefault.Origin.Span.Length);
        Assert.Equal("40", syntax.Source.GetText(firstDefault.Origin.Span));
        Assert.Equal("int", function.ReturnType!.Shape.CanonicalName);
        Assert.Equal(AstBodyKind.Block, function.Body.Kind);
        Assert.Equal(declaration[declaration.IndexOf("func", StringComparison.Ordinal)..], function.Origin.Text);
        Assert.Equal(Encoding.UTF8.GetByteCount(source[..source.IndexOf("func", StringComparison.Ordinal)]),
            function.Origin.Span.Start);
        Assert.Equal(function.Origin.Text, syntax.Source.GetText(function.Origin.Span));
        Assert.Contains(ast.Nodes.OfType<AstProperty>(), property => property.BindingNames.Contains("after"));
        Assert.All(function.Descendants(), node =>
        {
            Assert.InRange(node.Origin.Span.Start, function.Origin.Span.Start, function.Origin.Span.End);
            Assert.InRange(node.Origin.Span.End, node.Origin.Span.Start, function.Origin.Span.End);
        });
    }

    private static System.Collections.Generic.IEnumerable<SyntaxNode> Descendants(SyntaxNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }
}
