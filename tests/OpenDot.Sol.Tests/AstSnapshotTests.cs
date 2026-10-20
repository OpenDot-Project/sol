// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AstSnapshotTests
{
    [Fact]
    public void DeclarationFixtureProjectsIntoInspectableAstWithoutLosingNodeOrigins()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "declarations.sol");
        var source = SourceText.FromBytes(File.ReadAllBytes(path), path);
        var syntax = SyntaxTree.Parse(source, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(syntax.HasErrors);
        var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        AstVerifier.Verify(ast, TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(AstJson.Serialize(ast, TestContext.Current.CancellationToken));
        Assert.Equal("fixtures.models", json.RootElement.GetProperty("module").GetString());
        Assert.Equal(ast.Nodes.Length, json.RootElement.GetProperty("nodes").GetArrayLength());
        Assert.Contains(ast.Nodes, node => node is AstTypeDeclaration { Name: "User" });
        Assert.Contains(ast.Nodes, node => node is AstCall);
        Assert.All(ast.Nodes, node => Assert.Same(source, node.Origin.Source));
    }

    [Fact]
    public void NodeIdentityAndParentsAreSnapshotScoped()
    {
        var first = AstTestSources.Parse("func f(x: int): int = x");
        var second = AstTestSources.Parse("func f(x: int): int = x");
        var function = Assert.Single(first.Root.Declarations);
        Assert.Same(first.Root, first.GetParent(function));
        Assert.Null(first.GetParent(first.Root));
        Assert.Same(function, first.GetNode(function.Origin.Element));
        Assert.Throws<ArgumentException>(() => second.ValidateOwnership(function));
        Assert.Throws<ArgumentException>(() => second.GetParent(function));
        Assert.Throws<ArgumentException>(() => second.GetNode(function.Origin.Element));
    }

    [Fact]
    public void Utf8OffsetsArePreservedIncludingNonAsciiLiterals()
    {
        var ast = AstTestSources.Parse("val text = \"çay ☕\"");
        var literal = Assert.Single(ast.Root.Descendants().OfType<AstAtom>());
        Assert.Equal("\"çay ☕\"", literal.Origin.Text);
        Assert.True(literal.Origin.Span.Length > literal.Origin.Text.Length);
        Assert.Same(literal, ast.FindNode(literal.Origin.Span.Start));
        Assert.Same(ast.Root, ast.FindNode(ast.Psi.Source.Length));
        Assert.Throws<ArgumentOutOfRangeException>(() => ast.FindNode(-1));
    }

    [Fact]
    public void NormalizationDoesNotMutateOriginalBytesOrTokens()
    {
        var source = SourceText.FromString("module test\r\n// keep trivia\r\nval x: Box<Box<int>>=source\r\n");
        byte[] before = source.Bytes.ToArray();
        var syntax = SyntaxTree.Parse(source, cancellationToken: TestContext.Current.CancellationToken);
        var tokens = syntax.Tokens;
        var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax, cancellationToken: TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken);
        AstVerifier.Verify(ast, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(before, source.Bytes.ToArray());
        Assert.Equal(tokens, ast.Psi.Syntax.Tokens);
        Assert.Equal("Box<Box<int>>", Assert.Single(ast.Root.Declarations.OfType<AstProperty>()).Type!.Shape.CanonicalName);
    }

    [Fact]
    public void JsonIsFlatDeterministicAndDoesNotIncludeSourcePath()
    {
        var ast = AstTestSources.Parse("func f(a,b:int) = a + b", path: "C:/private/location/input.sol");
        string first = AstJson.Serialize(ast, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(first, AstJson.Serialize(ast, cancellationToken: TestContext.Current.CancellationToken));
        Assert.DoesNotContain("C:/private", first, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(first);
        Assert.Equal(AstJson.SchemaVersion, json.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(ast.Nodes.Length, json.RootElement.GetProperty("nodes").GetArrayLength());
        using var stream = new MemoryStream();
        AstJson.Write(ast, stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(stream.CanWrite);
    }

    [Fact]
    public void ErrorTreesCanBeInspectedWithoutClaimingSuccess()
    {
        var syntax = SyntaxTree.Parse(SourceText.FromString("module test\nclass C { func f(x: ) { return }"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(syntax.HasErrors);
        var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax, cancellationToken: TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken);
        AstVerifier.Verify(ast, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(ast.HasSyntaxErrors);
        Assert.Contains("\"hasSyntaxErrors\": true", AstJson.Serialize(ast, cancellationToken: TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationIsObservedBeforeProducingASnapshotOrInspection()
    {
        var ast = AstTestSources.Parse("val x = 1");
        var cancelled = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => AstSnapshot.Create(ast.Psi, cancelled));
        Assert.Throws<OperationCanceledException>(() => AstVerifier.Verify(ast, cancelled));
        Assert.Throws<OperationCanceledException>(() => AstJson.Serialize(ast, cancelled));
    }

    [Fact]
    public async Task ReadOnlyNavigationAndSerializationCanRunConcurrently()
    {
        var ast = AstTestSources.Parse("class C<T> { func get(value: T): T = value }");
        string expected = AstJson.Serialize(ast, cancellationToken: TestContext.Current.CancellationToken);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            AstVerifier.Verify(ast, cancellationToken: TestContext.Current.CancellationToken);
            return AstJson.Serialize(ast, cancellationToken: TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken));
        var values = await Task.WhenAll(tasks);
        Assert.All(values, value => Assert.Equal(expected, value));
    }

    [Fact]
    public void ArbitraryByteRecoveryRetainsOriginsWithinTheirSource()
    {
        var random = new Random(20261020);
        for (int count = 0; count < 96; count++)
        {
            byte[] bytes = new byte[random.Next(0, 180)];
            random.NextBytes(bytes);
            var syntax = SyntaxTree.Parse(SourceText.FromBytes(bytes), errorLimit: 20, maxDepth: 32, cancellationToken: TestContext.Current.CancellationToken);
            var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax, cancellationToken: TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken);
            AstVerifier.Verify(ast, cancellationToken: TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(AstJson.Serialize(ast, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(ast.Nodes.Length, document.RootElement.GetProperty("nodes").GetArrayLength());
        }
    }
}
