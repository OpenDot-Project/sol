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

public sealed class AnonymousFunctionDefaultParameterRecoveryTests
{
    [Fact]
    public void SemanticallyRejectedDefaultRemainsInSyntaxAndAstWithExactSourceOrigin()
    {
        const string declaration = "val callback = func(first, second: int = 40): int { first }";
        string source = "module test\r\n// 🧪 trivia before the declaration\r\n" + declaration
            + "\r\nval after: int = 42\r\n";
        var syntax = SyntaxTree.Parse(SourceText.FromString(source, "anonymous-default.sol"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(syntax.HasErrors, string.Join("\n", syntax.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        var functionSyntax = Assert.Single(Descendants(syntax.Root), node =>
            node.Kind == NodeKind.AnonymousFunctionExpression);
        var parameterList = Assert.Single(functionSyntax.Children, node => node.Kind == NodeKind.ParameterList);
        var parameterGroup = Assert.Single(parameterList.Children);
        var defaultSyntax = Assert.Single(parameterGroup.Children, node => node.Kind == NodeKind.LiteralExpression);
        Assert.Equal("40", syntax.Source.GetText(defaultSyntax.Span));

        var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        AstVerifier.Verify(ast, TestContext.Current.CancellationToken);
        var function = Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>());
        var firstDefault = Assert.IsType<AstAtom>(function.Parameters[0].DefaultValue);
        var secondDefault = Assert.IsType<AstAtom>(function.Parameters[1].DefaultValue);
        int expectedDefaultStart = source.IndexOf("40", StringComparison.Ordinal);

        Assert.Equal(new[] { "first", "second" }, function.Parameters.Select(parameter => parameter.Name));
        Assert.Same(firstDefault, secondDefault);
        Assert.Equal("40", firstDefault.Origin.Text);
        Assert.Equal(Encoding.UTF8.GetByteCount(source[..expectedDefaultStart]), firstDefault.Origin.Span.Start);
        Assert.Equal(2, firstDefault.Origin.Span.Length);
        Assert.Equal("40", syntax.Source.GetText(firstDefault.Origin.Span));
        Assert.Equal(declaration[declaration.IndexOf("func", StringComparison.Ordinal)..], function.Origin.Text);
        Assert.Contains(ast.Nodes.OfType<AstProperty>(), property => property.BindingNames.Contains("after"));
        Assert.All(function.Descendants(), node =>
        {
            Assert.InRange(node.Origin.Span.Start, function.Origin.Span.Start, function.Origin.Span.End);
            Assert.InRange(node.Origin.Span.End, node.Origin.Span.Start, function.Origin.Span.End);
        });
    }

    [Fact]
    public void MissingBodyRecoveryRetainsDefaultParameterAndFollowingDeclaration()
    {
        const string source = "module test\nval callback = func(value: int = 40): int\n"
            + "val after: int = 42\n";
        var syntax = SyntaxTree.Parse(SourceText.FromString(source, "recovered-anonymous-default.sol"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(syntax.HasErrors);
        Assert.Contains(syntax.Diagnostics, diagnostic => diagnostic.Code == "SOL1101");
        Assert.Equal(source.Length, syntax.Root.Span.End);

        var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        AstVerifier.Verify(ast, TestContext.Current.CancellationToken);
        var function = Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>());
        var defaultValue = Assert.IsType<AstAtom>(Assert.Single(function.Parameters).DefaultValue);

        Assert.Equal(AstBodyKind.Missing, function.Body.Kind);
        Assert.Equal("40", defaultValue.Origin.Text);
        Assert.Equal(source.IndexOf("40", StringComparison.Ordinal), defaultValue.Origin.Span.Start);
        Assert.Equal(source.Substring(defaultValue.Origin.Span.Start, defaultValue.Origin.Span.Length),
            syntax.Source.GetText(defaultValue.Origin.Span));
        Assert.Contains(ast.Nodes.OfType<AstProperty>(), property => property.BindingNames.Contains("after"));
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
