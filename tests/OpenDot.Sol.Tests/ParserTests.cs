// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class ParserTests
{
    [Theory]
    [InlineData("module app\n")]
    [InlineData("module any.logical.name; import std.stdio\nobject Main { func main(args: []String) { println(\"Hi\") } }")]
    [InlineData("module app\nfunc add(a, b: int) = a + b\n")]
    [InlineData("module app\nfunc f(x: int): int { val result = x * 2\nresult + 1 }")]
    [InlineData("module app\nval values = [1, 2, 3,]\n")]
    [InlineData("module app\nval add = { x, y -> x + y }\nval other = { $0 + $1 }\n")]
    [InlineData("module app\npublic import names = a.b : first = second, third\n")]
    [InlineData("module app\nfunc f(ref x: int, out y: int) { y = x\nreturn }\n")]
    [InlineData("module app\nfunc f(): () throws Exception { throw Exception(\"message\") }")]
    [InlineData("module app\nfunc f() { call(1\n+ 2) }\n")]
    [InlineData("module app\nfunc f() { val x = 1 +\n2\nreturn }\n")]
    public void SupportedProductionsParse(string text)
    {
        var tree = Parse(text);
        Assert.Empty(tree.Diagnostics);
        Assert.False(tree.HasErrors);
        Assert.Equal(NodeKind.CompilationUnit, tree.Root.Kind);
        Assert.Equal(tree.Source.Length, tree.Root.Span.Length);
    }

    [Fact]
    public void ModuleNameIsIndependentOfPath()
    {
        var tree = SyntaxTree.Parse(SourceText.FromString("module graphics.widgets\n", "app/example.sol"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("graphics.widgets", tree.ModuleName);
        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void GroupedParametersKeepBothSourceNames()
    {
        var tree = Parse("module app\nfunc add(a, b: int) = a + b");
        var function = tree.Root.Children.Single(n => n.Kind == NodeKind.FunctionDeclaration);
        var parameters = function.Children.Single(n => n.Kind == NodeKind.ParameterList);
        var group = Assert.Single(parameters.Children);
        Assert.Equal(new[] { "a", "b" }, group.Children.Where(n => n.Kind == NodeKind.Name)
            .Select(n => tree.Source.GetText(n.Span)));
    }

    [Fact]
    public void ArithmeticPrecedenceIsStructural()
    {
        var tree = Parse("module app\nval x = 1 + 2 * 3");
        var expression = tree.Root.Children[1].Children[1];
        Assert.Equal(NodeKind.BinaryExpression, expression.Kind);
        Assert.Equal(NodeKind.LiteralExpression, expression.Children[0].Kind);
        Assert.Equal(NodeKind.BinaryExpression, expression.Children[1].Kind);
    }

    [Theory]
    [InlineData("object Main {}", "SOL1100")]
    [InlineData("module app\nfunc f()\n{ }", "SOL1103")]
    [InlineData("module app\nclass Example {}", "SOL1102")]
    [InlineData("module app\nfunc f() { val x = }", "SOL1101")]
    [InlineData("module app\nfunc f() {", "SOL1101")]
    [InlineData("module app\nfunc f() { 1 2 }", "SOL1101")]
    public void InvalidOrUnimplementedSyntaxDoesNotReportSuccess(string text, string code)
    {
        var tree = Parse(text);
        Assert.True(tree.HasErrors);
        Assert.Contains(tree.Diagnostics, d => d.Code == code);
        Assert.Equal(tree.Source.Length, tree.Root.Span.Length);
    }

    [Fact]
    public void NestingLimitPreventsUnboundedRecursion()
    {
        var tree = Parse("module app\nval x = " + new string('(', 512) + "1" + new string(')', 512));
        Assert.Contains(tree.Diagnostics, d => d.Code == "SOL1104");
    }

    [Fact]
    public void LongLeftAssociativeChainsAlsoRespectTreeDepthLimit()
    {
        var tree = Parse("module app\nval x = " + string.Join(" + ", Enumerable.Repeat("1", 1024)));
        Assert.Contains(tree.Diagnostics, d => d.Code == "SOL1104");
    }

    [Fact]
    public void MalformedBytesAndRecoveryAlwaysTerminate()
    {
        var random = new Random(23);
        for (int i = 0; i < 128; i++)
        {
            byte[] bytes = new byte[i * 5];
            random.NextBytes(bytes);
            var source = SourceText.FromBytes(bytes);
            var tree = SyntaxTree.Parse(source, errorLimit: 10,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(tree.Diagnostics.Length <= 10);
            Assert.Equal(bytes.Length, tree.Root.Span.Length);
        }
    }

    private static SyntaxTree Parse(string text) => SyntaxTree.Parse(SourceText.FromString(text),
        cancellationToken: TestContext.Current.CancellationToken);
}
