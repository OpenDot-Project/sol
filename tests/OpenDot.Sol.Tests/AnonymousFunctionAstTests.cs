// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using System.Text.Json;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionAstTests
{
    [Fact]
    public void ExplicitSignatureAndBodyAreProjectedAsASeparateExpressionNode()
    {
        var ast = AstTestSources.Parse("val callback = func(input, fallback: int = 10): int "
            + "throws IOException { input }");
        var function = Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>());

        Assert.Equal(new[] { "input", "fallback" }, function.Parameters.Select(parameter => parameter.Name));
        Assert.All(function.Parameters, parameter => Assert.Equal("int", parameter.Type!.Shape.CanonicalName));
        Assert.Same(function.Parameters[0].GroupOrigin.Element, function.Parameters[1].GroupOrigin.Element);
        Assert.Equal("int", function.ReturnType!.Shape.CanonicalName);
        Assert.Equal(new[] { "IOException" }, function.ThrownTypes.Select(type => type.Shape.CanonicalName));
        Assert.Equal(AstBodyKind.Block, function.Body.Kind);
        Assert.IsType<AstBlock>(function.Body.Node);
        Assert.IsType<AstAtom>(function.Body.TailExpression);
        Assert.Same(function, ast.GetNode(function.Origin.Element));
        AstVerifier.Verify(ast, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void PassingModesDefaultsAndMultipleCheckedEffectsKeepTheirSourceOrder()
    {
        var ast = AstTestSources.Parse("val callback = func(ref value: int, out result: String, "
            + "vararg items: byte = 1) throws FirstError, SecondError { return }");
        var function = Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>());

        Assert.Equal(new[] { AstPassingMode.Ref, AstPassingMode.Out, AstPassingMode.Vararg },
            function.Parameters.Select(parameter => parameter.PassingMode));
        Assert.Equal("1", function.Parameters[2].DefaultValue!.Origin.Text);
        Assert.Null(function.ReturnType);
        Assert.Equal(new[] { "FirstError", "SecondError" },
            function.ThrownTypes.Select(type => type.Shape.CanonicalName));
        Assert.Equal(AstBodyKind.Block, function.Body.Kind);
    }

    [Fact]
    public void ThrownTypesAreNotMistakenForAnImplicitlyTypedReturn()
    {
        var ast = AstTestSources.Parse("val callback = func() throws IOException, RuntimeException { }");
        var function = Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>());

        Assert.Empty(function.Parameters);
        Assert.Null(function.ReturnType);
        Assert.Equal(new[] { "IOException", "RuntimeException" },
            function.ThrownTypes.Select(type => type.Shape.CanonicalName));
    }

    [Fact]
    public void ExplicitReturnTypeIsSeparatedFromEffectTypes()
    {
        var ast = AstTestSources.Parse("val callback = func(value: String): String "
            + "throws IOException, RuntimeException { value }");
        var function = Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>());

        Assert.Equal("String", function.ReturnType!.Shape.CanonicalName);
        Assert.Equal(new[] { "IOException", "RuntimeException" },
            function.ThrownTypes.Select(type => type.Shape.CanonicalName));
    }

    [Fact]
    public void NestedLambdaRemainsASeparateNodeAndKeepsItsParentAndOrigin()
    {
        const string source = "val factory = func(value: int): () -> int { { value } }";
        var ast = AstTestSources.Parse(source);
        var function = Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>());
        var lambda = Assert.Single(ast.Nodes.OfType<AstLambda>());

        Assert.Same(lambda, function.Body.TailExpression);
        var statement = Assert.IsType<AstExpressionStatement>(ast.GetParent(lambda));
        Assert.Same(function.Body.Node, ast.GetParent(statement));
        Assert.Equal("{ value }", lambda.Origin.Text);
        Assert.Equal("func(value: int): () -> int { { value } }", function.Origin.Text);
        Assert.Equal(NodeKind.AnonymousFunctionExpression, function.SyntaxKind);
    }

    [Fact]
    public void FlatAstJsonIncludesAnonymousFunctionSignatureAndBodyReference()
    {
        var ast = AstTestSources.Parse("val callback = func(value: int): int throws IOException { value }");
        var function = Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>());
        using var document = JsonDocument.Parse(AstJson.Serialize(ast, TestContext.Current.CancellationToken));
        var entry = document.RootElement.GetProperty("nodes").EnumerateArray()
            .Single(node => node.GetProperty("id").GetInt32() == function.Id);

        Assert.Equal("int", entry.GetProperty("returnType").GetString());
        Assert.Equal(new[] { "IOException" }, entry.GetProperty("throws").EnumerateArray()
            .Select(type => type.GetString()));
        Assert.Equal("Block", entry.GetProperty("bodyKind").GetString());
        Assert.Equal(function.Body.Node!.Id, entry.GetProperty("body").GetInt32());
        Assert.Equal("value", entry.GetProperty("parameters")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void RecoveryWithoutAFunctionBodyProducesAnInspectableIncompleteNode()
    {
        var syntax = SyntaxTree.Parse(SourceText.FromString("module test\nval callback = func(value: int): int"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(syntax.HasErrors);
        var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        var function = Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>());

        Assert.Equal(AstBodyKind.Missing, function.Body.Kind);
        Assert.Null(function.Body.Node);
        Assert.Equal("int", function.ReturnType!.Shape.CanonicalName);
        AstVerifier.Verify(ast, TestContext.Current.CancellationToken);
    }
}
