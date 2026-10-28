// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionParserRecoveryTests
{
    [Fact]
    public void MissingParameterColonKeepsTheAnonymousFunctionAndFollowingDeclaration()
    {
        const string source = "module test\nval callback = func(value int) { value }\nval after: int = 42\n";
        var syntax = SyntaxTree.Parse(SourceText.FromString(source),
            cancellationToken: TestContext.Current.CancellationToken);
        var cst = Descendants(syntax.Root).ToArray();

        Assert.True(syntax.HasErrors);
        Assert.Contains(cst, node => node.Kind == NodeKind.AnonymousFunctionExpression);
        Assert.Equal(2, PropertyCount(cst));
        AssertTreeSpansAreContained(syntax.Root, new(0, syntax.Source.Length));

        var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        AstVerifier.Verify(ast, TestContext.Current.CancellationToken);
        Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>());
        Assert.Contains(ast.Nodes.OfType<AstProperty>(), property => property.BindingNames.Contains("after"));
    }

    [Fact]
    public void MissingLambdaArrowRecoversItsExpressionAndDoesNotConsumeTheNextDeclaration()
    {
        const string source = "module test\nval callback = { item: int item }\nval after: int = 7\n";
        var syntax = SyntaxTree.Parse(SourceText.FromString(source),
            cancellationToken: TestContext.Current.CancellationToken);
        var cst = Descendants(syntax.Root).ToArray();

        Assert.True(syntax.HasErrors);
        var lambda = Assert.Single(cst, node => node.Kind == NodeKind.LambdaExpression);
        Assert.Contains(lambda.Children, child => child.Kind == NodeKind.LambdaParameters);
        Assert.Equal(2, PropertyCount(cst));

        var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        AstVerifier.Verify(ast, TestContext.Current.CancellationToken);
        Assert.Single(ast.Nodes.OfType<AstLambda>());
        Assert.Contains(ast.Nodes.OfType<AstProperty>(), property => property.BindingNames.Contains("after"));
    }

    [Fact]
    public void MissingParameterCloseBeforeBodyReportsAnErrorAndRetainsTheBodyBoundary()
    {
        const string source = "module test\nval callback = func(value: int { value }\nval after: int = 9\n";
        var syntax = SyntaxTree.Parse(SourceText.FromString(source),
            cancellationToken: TestContext.Current.CancellationToken);
        var cst = Descendants(syntax.Root).ToArray();
        var function = Assert.Single(cst, node => node.Kind == NodeKind.AnonymousFunctionExpression);
        var block = Assert.Single(function.Children, node => node.Kind == NodeKind.Block);

        Assert.True(syntax.HasErrors);
        Assert.Contains(syntax.Diagnostics, diagnostic => diagnostic.Code == "SOL1101");
        Assert.Equal("{ value }", syntax.Source.GetText(block.Span));
        Assert.Equal(2, PropertyCount(cst));
        AssertTreeSpansAreContained(syntax.Root, new(0, syntax.Source.Length));

        var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        AstVerifier.Verify(ast, TestContext.Current.CancellationToken);
        Assert.Equal("func(value: int { value }", Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>()).Origin.Text);
    }

    [Fact]
    public void MissingBodyLeavesTheNextTopLevelDeclarationAvailableForParsing()
    {
        const string source = "module test\nval callback = func(value: int): int\nval after: int = 11\n";
        var syntax = SyntaxTree.Parse(SourceText.FromString(source),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(syntax.HasErrors);
        Assert.Equal(2, PropertyCount(Descendants(syntax.Root)));

        var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        AstVerifier.Verify(ast, TestContext.Current.CancellationToken);
        var function = Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>());
        Assert.Equal(AstBodyKind.Missing, function.Body.Kind);
        Assert.Contains(ast.Nodes.OfType<AstProperty>(), property => property.BindingNames.Contains("after"));
    }

    [Fact]
    public void UnicodeTriviaAndCrLfDoNotShiftFunctionOrParameterSourceOrigins()
    {
        const string declaration = "val callback = func(input: String): String throws IOException = input";
        string source = "module test\r\n// café and 🧪 before the declaration\r\n" + declaration + "\r\n";
        var syntax = SyntaxTree.Parse(SourceText.FromString(source, "unicode.sol"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(syntax.HasErrors, string.Join("\n", syntax.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(source, string.Concat(syntax.Tokens.Select(token => syntax.Source.GetText(token.FullSpan))));

        var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        AstVerifier.Verify(ast, TestContext.Current.CancellationToken);
        var function = Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>());

        Assert.Equal(declaration[declaration.IndexOf("func", System.StringComparison.Ordinal)..], function.Origin.Text);
        Assert.Equal("input", function.Parameters[0].NameOrigin.Text);
        Assert.Equal(function.Parameters[0].NameOrigin.Span.Start,
            function.Parameters[0].GroupOrigin.Span.Start);
        Assert.Equal(function.Origin.Text, syntax.Source.GetText(function.Origin.Span));
        Assert.All(function.Descendants(), node =>
        {
            Assert.InRange(node.Origin.Span.Start, function.Origin.Span.Start, function.Origin.Span.End);
            Assert.InRange(node.Origin.Span.End, node.Origin.Span.Start, function.Origin.Span.End);
        });
    }

    [Fact]
    public void ErrorLimitBoundsDiagnosticsWithoutDiscardingTheFileRoot()
    {
        const string source = "module test\nval callback = func(, , , ,) { }\nval after: int = 13\n";
        var syntax = SyntaxTree.Parse(SourceText.FromString(source), errorLimit: 2,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(syntax.HasErrors);
        Assert.True(syntax.Diagnostics.Length <= 2);
        Assert.Equal(NodeKind.CompilationUnit, syntax.Root.Kind);
        Assert.Equal(source.Length, syntax.Root.Span.End);
        Assert.Contains(Descendants(syntax.Root), node => node.Kind == NodeKind.AnonymousFunctionExpression);
    }

    private static System.Collections.Generic.IEnumerable<SyntaxNode> Descendants(SyntaxNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }

    private static int PropertyCount(System.Collections.Generic.IEnumerable<SyntaxNode> nodes) =>
        nodes.Count(node => node.Kind is NodeKind.Binding or NodeKind.PropertyDeclaration);

    private static void AssertTreeSpansAreContained(SyntaxNode node, TextSpan enclosingSpan)
    {
        Assert.InRange(node.Span.Start, enclosingSpan.Start, enclosingSpan.End);
        Assert.InRange(node.Span.End, node.Span.Start, enclosingSpan.End);
        foreach (var child in node.Children) { AssertTreeSpansAreContained(child, node.Span); }
    }
}
