// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionSerializationContractTests
{
    [Fact]
    public void FlatJsonPreservesGroupedSignatureModesEffectsAndExpressionBodyReference()
    {
        var ast = AstTestSources.Parse("val callback = func(first, second: int, fallback: int = 7, "
            + "ref destination: String, out result: int): int throws IOException, RuntimeException = first");
        var function = Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>());
        using var document = JsonDocument.Parse(AstJson.Serialize(ast, TestContext.Current.CancellationToken));
        var nodes = document.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
        var entry = nodes.Single(node => node.GetProperty("id").GetInt32() == function.Id);
        var parameters = entry.GetProperty("parameters").EnumerateArray().ToArray();

        Assert.Equal(new[] { "first", "second", "fallback", "destination", "result" },
            function.Parameters.Select(parameter => parameter.Name));
        Assert.Same(function.Parameters[0].GroupOrigin.Element, function.Parameters[1].GroupOrigin.Element);
        Assert.Equal(new[] { AstPassingMode.Value, AstPassingMode.Value, AstPassingMode.Value,
                AstPassingMode.Ref, AstPassingMode.Out },
            function.Parameters.Select(parameter => parameter.PassingMode));
        Assert.Equal(new[] { "Value", "Value", "Value", "Ref", "Out" },
            parameters.Select(parameter => parameter.GetProperty("mode").GetString()));
        Assert.Equal(new[] { "int", "int", "int", "String", "int" },
            parameters.Select(parameter => parameter.GetProperty("type").GetString()));
        Assert.Equal("int", entry.GetProperty("returnType").GetString());
        Assert.Equal(new[] { "IOException", "RuntimeException" },
            entry.GetProperty("throws").EnumerateArray().Select(type => type.GetString()));
        Assert.Equal("Expression", entry.GetProperty("bodyKind").GetString());
        Assert.Equal(function.Body.Node!.Id, entry.GetProperty("body").GetInt32());
        var bodyEntry = nodes.Single(node => node.GetProperty("id").GetInt32() == function.Body.Node!.Id);
        Assert.Equal(function.Body.Node.SyntaxKind.ToString(), bodyEntry.GetProperty("kind").GetString());
        Assert.Equal(function.Parameters[0].NameOrigin.Span.Start,
            parameters[0].GetProperty("nameStart").GetInt32());
        Assert.Equal(function.Parameters[0].GroupOrigin.Span.Start,
            parameters[1].GetProperty("groupStart").GetInt32());
        Assert.Equal(function.Parameters[2].DefaultValue!.Id, parameters[2].GetProperty("default").GetInt32());
    }

    [Fact]
    public void FlatJsonPreservesEscapingParameterAnnotationOnEachExpandedName()
    {
        var ast = AstTestSources.Parse("val callback = func(@escaping first, second: () -> int): int = first()");
        var function = Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>());
        using var document = JsonDocument.Parse(AstJson.Serialize(ast, TestContext.Current.CancellationToken));
        var entry = document.RootElement.GetProperty("nodes").EnumerateArray()
            .Single(node => node.GetProperty("id").GetInt32() == function.Id);
        var parameters = entry.GetProperty("parameters").EnumerateArray().ToArray();

        Assert.Equal(new[] { "first", "second" }, function.Parameters.Select(parameter => parameter.Name));
        Assert.All(function.Parameters, parameter => Assert.True(parameter.IsEscaping));
        Assert.All(parameters, parameter => Assert.Equal("escaping",
            Assert.Single(parameter.GetProperty("annotations").EnumerateArray()).GetString()));
    }

    [Fact]
    public void BlockAndExpressionBodyReferencesResolveToDirectOwnedNodes()
    {
        var ast = AstTestSources.Parse("val block = func(): int { 1 }\n"
            + "val expression = func(): int = 2");
        AstVerifier.Verify(ast, TestContext.Current.CancellationToken);
        var functions = ast.Nodes.OfType<AstAnonymousFunction>().OrderBy(function => function.Origin.Span.Start).ToArray();
        using var document = JsonDocument.Parse(AstJson.Serialize(ast, TestContext.Current.CancellationToken));
        var serialized = document.RootElement.GetProperty("nodes").EnumerateArray().ToArray();

        Assert.Equal(2, functions.Length);
        Assert.Equal(new[] { AstBodyKind.Block, AstBodyKind.Expression }, functions.Select(function => function.Body.Kind));
        Assert.IsType<AstBlock>(functions[0].Body.Node);
        Assert.IsType<AstAtom>(functions[1].Body.Node);
        for (int index = 0; index < functions.Length; index++)
        {
            var function = functions[index];
            var entry = serialized.Single(node => node.GetProperty("id").GetInt32() == function.Id);
            Assert.Equal(function.Body.Kind.ToString(), entry.GetProperty("bodyKind").GetString());
            Assert.Equal(function.Body.Node!.Id, entry.GetProperty("body").GetInt32());
            Assert.Same(function, ast.GetParent(function.Body.Node));
            Assert.Same(function.Body.Node, ast.GetNode(function.Body.Node.Origin.Element));
        }
    }

    [Fact]
    public void SignatureTypeReferencesResolveToDirectChildrenAndParameterGroups()
    {
        var ast = AstTestSources.Parse("val callback = func(value, other: String): int throws IOException, RuntimeException = 1");
        var function = Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>());

        Assert.Same(ast.GetNode(function.Parameters[0].GroupOrigin.Element), ast.GetParent(function.Parameters[0].Type!));
        Assert.Same(function.Parameters[0].GroupOrigin.Element, function.Parameters[1].GroupOrigin.Element);
        Assert.Same(function.Parameters[0].Type, function.Parameters[1].Type);
        Assert.Same(ast.GetParent(function.Parameters[0].Type!), ast.GetParent(function.Parameters[1].Type!));
        Assert.Same(function, ast.GetParent(function.ReturnType!));
        Assert.All(function.ThrownTypes, type => Assert.Same(function, ast.GetParent(type)));
        Assert.All(function.ThrownTypes, ast.ValidateOwnership);
        AstVerifier.Verify(ast, TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(AstJson.Serialize(ast, TestContext.Current.CancellationToken));
        var entry = document.RootElement.GetProperty("nodes").EnumerateArray()
            .Single(node => node.GetProperty("id").GetInt32() == function.Id);
        Assert.Equal("int", entry.GetProperty("returnType").GetString());
        Assert.Equal(new[] { "IOException", "RuntimeException" },
            entry.GetProperty("throws").EnumerateArray().Select(type => type.GetString()));
    }

    [Fact]
    public void AnonymousFunctionInspectionIsDeterministicAcrossSnapshotsAndStreamWrites()
    {
        const string sourceText = "val callback = func(value: int): int throws IOException = value + 1";
        var first = AstTestSources.Parse(sourceText, path: "C:/private/anon/input.sol");
        var second = AstTestSources.Parse(sourceText, path: "C:/private/anon/input.sol");
        string serialized = AstJson.Serialize(first, TestContext.Current.CancellationToken);
        using var output = new MemoryStream();
        AstJson.Write(first, output, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(serialized);

        Assert.Equal(Encoding.UTF8.GetBytes(serialized), output.ToArray());
        Assert.Equal(serialized, AstJson.Serialize(second, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("C:/private/anon", serialized, System.StringComparison.Ordinal);
        Assert.Equal(AstJson.SchemaVersion, document.RootElement.GetProperty("schema").GetInt32());
    }

    [Fact]
    public void MissingBodyRecoverySerializesAnIncompleteFunctionWithoutInventingAReference()
    {
        var syntax = SyntaxTree.Parse(SourceText.FromString("module test\nval callback = func(value: int): int"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(syntax.HasErrors);
        var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        var function = Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>());
        AstVerifier.Verify(ast, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(AstJson.Serialize(ast, TestContext.Current.CancellationToken));
        var entry = document.RootElement.GetProperty("nodes").EnumerateArray()
            .Single(node => node.GetProperty("id").GetInt32() == function.Id);

        Assert.Equal(AstBodyKind.Missing, function.Body.Kind);
        Assert.Null(function.Body.Node);
        Assert.Equal("Missing", entry.GetProperty("bodyKind").GetString());
        Assert.False(entry.TryGetProperty("body", out _));
        Assert.True(document.RootElement.GetProperty("hasSyntaxErrors").GetBoolean());
    }

    [Fact]
    public void RecoveredUnclosedBlockKeepsAValidBodyNodeAndSnapshotOwnership()
    {
        var syntax = SyntaxTree.Parse(SourceText.FromString("module test\nval callback = func(value: int): int { return value"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(syntax.HasErrors);
        var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        var other = AstSnapshot.Create(ast.Psi, TestContext.Current.CancellationToken);
        var function = Assert.Single(ast.Nodes.OfType<AstAnonymousFunction>());
        var body = Assert.IsType<AstBlock>(function.Body.Node);

        AstVerifier.Verify(ast, TestContext.Current.CancellationToken);
        Assert.Same(function, ast.GetParent(body));
        Assert.Same(body, ast.GetNode(body.Origin.Element));
        Assert.Throws<System.ArgumentException>(() => other.ValidateOwnership(function));
        Assert.Throws<System.ArgumentException>(() => other.GetParent(body));
    }
}
