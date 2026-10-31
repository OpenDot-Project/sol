// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionParameterGrammarTests
{
    [Fact]
    public void AnonymousFunctionParametersMayOmitTheirTypes()
    {
        var tree = Parse("module test\nval callback = func(item, fallback) { item }\n");
        var function = Assert.Single(Descendants(tree.Root), node =>
            node.Kind == NodeKind.AnonymousFunctionExpression);
        var parameters = Assert.Single(function.Children, node => node.Kind == NodeKind.ParameterList);
        var group = Assert.Single(parameters.Children);

        Assert.Empty(tree.Diagnostics);
        Assert.Equal(new[] { "item", "fallback" }, Names(tree, group));
        Assert.DoesNotContain(group.Children, node => node.Kind == NodeKind.Type);
    }

    [Fact]
    public void OrdinaryFunctionParametersMustDeclareTheirTypes()
    {
        var tree = Parse("module test\nfunc ordinary(value) { value }\n");

        Assert.True(tree.HasErrors);
        Assert.Contains(tree.Diagnostics, diagnostic => diagnostic.Code == "SOL1101");
    }

    [Fact]
    public void ParameterGroupsRetainPassingModesGroupedNamesAndDefaults()
    {
        var tree = Parse("module test\nfunc grouped(ref first, second: int = 7, out target: String, "
            + "vararg values: byte) { }\n");
        var function = Assert.Single(Descendants(tree.Root), node => node.Kind == NodeKind.FunctionDeclaration);
        var parameters = Assert.Single(function.Children, node => node.Kind == NodeKind.ParameterList);
        var groups = parameters.Children;

        Assert.Empty(tree.Diagnostics);
        Assert.Equal(3, groups.Length);
        Assert.Equal(new[] { "first", "second" }, Names(tree, groups[0]));
        Assert.Equal("ref", Mode(tree, groups[0]));
        Assert.Single(groups[0].Children, node => node.Kind == NodeKind.Type);
        Assert.Single(groups[0].Children, node => node.Kind == NodeKind.LiteralExpression);
        Assert.Equal(new[] { "target" }, Names(tree, groups[1]));
        Assert.Equal("out", Mode(tree, groups[1]));
        Assert.Equal(new[] { "values" }, Names(tree, groups[2]));
        Assert.Equal("vararg", Mode(tree, groups[2]));
    }

    [Fact]
    public void FunctionTypeThrowsClauseStopsBeforeRefOutAndVarargNamedParameters()
    {
        var tree = Parse("module test\nfunc consume(callback: (int) -> () throws IOException, "
            + "ref input: int, out output: String, vararg rest: byte) { }\n");
        var function = Assert.Single(Descendants(tree.Root), node => node.Kind == NodeKind.FunctionDeclaration);
        var parameters = Assert.Single(function.Children, node => node.Kind == NodeKind.ParameterList);
        var groups = parameters.Children;
        var callbackType = Assert.Single(groups[0].Children, node => node.Kind == NodeKind.Type);
        var throwsClause = Assert.Single(Descendants(callbackType), node => node.Kind == NodeKind.ThrowsClause);

        Assert.Empty(tree.Diagnostics);
        Assert.Equal(4, groups.Length);
        Assert.Equal("IOException", tree.Source.GetText(Assert.Single(throwsClause.Children).Span));
        Assert.Equal("ref", Mode(tree, groups[1]));
        Assert.Equal(new[] { "input" }, Names(tree, groups[1]));
        Assert.Equal("out", Mode(tree, groups[2]));
        Assert.Equal(new[] { "output" }, Names(tree, groups[2]));
        Assert.Equal("vararg", Mode(tree, groups[3]));
        Assert.Equal(new[] { "rest" }, Names(tree, groups[3]));
    }

    private static string[] Names(SyntaxTree tree, SyntaxNode group) => group.Children
        .Where(node => node.Kind == NodeKind.Name)
        .Select(node => tree.Source.GetText(node.Span))
        .ToArray();

    private static string Mode(SyntaxTree tree, SyntaxNode group) => tree.Source.GetText(
        Assert.Single(group.Children, node => node.Kind == NodeKind.PassingMode).Span);

    private static System.Collections.Generic.IEnumerable<SyntaxNode> Descendants(SyntaxNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }

    private static SyntaxTree Parse(string source) => SyntaxTree.Parse(SourceText.FromString(source),
        cancellationToken: TestContext.Current.CancellationToken);
}
