// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FunctionValueArrayTests
{
    [Fact]
    public void DirectFunctionReferencesCanBeStoredAndLoadedAsTypedArrayElements()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(
            "func increment(value: int): int = value + 1\n"
            + "func <T> identity(value: T): T = value\n"
            + "object Main { func main(args: []String): () {\n"
            + "val callbacks: []((int) -> int) = [increment, identity]\n"
            + "val result = callbacks[1](41)\n"
            + "} }")], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var entry = compilation.Program!.Program.Functions[compilation.Program.Program.Entry];
        var callbackArray = Assert.Single(entry.Body!.Statements.OfType<FirDeclare>(),
            declaration => declaration.Initializer is FirArrayLiteral);
        var literal = Assert.IsType<FirArrayLiteral>(callbackArray.Initializer);
        Assert.Equal(2, literal.Elements.Length);
        Assert.All(literal.Elements, element => Assert.IsType<FirFunctionReference>(element));
        Assert.Equal(ResolvedTypeKind.Function, literal.Type.Arguments[0].Kind);

        var indirect = Assert.Single(entry.Body.Statements.OfType<FirDeclare>(),
            declaration => declaration.Initializer is FirIndirectCall);
        var call = Assert.IsType<FirIndirectCall>(indirect.Initializer);
        Assert.IsType<FirArrayIndex>(call.Function);
        Assert.Equal("int", call.Type.Name);
        Assert.Contains("IndirectCall", LoweringTrace.Inspect(compilation.Program,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void EmptyFunctionReferenceArrayRequiresAndPreservesItsElementSignature()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(
            "object Main { func main(args: []String): () {\n"
            + "val callbacks: []((String) -> String) = []\n"
            + "val count = callbacks.length\n"
            + "} }")], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var program = compilation.Program!.Program;
        var entry = program.Functions[program.Entry];
        var literal = Assert.Single(entry.Body!.Statements.OfType<FirDeclare>(),
            declaration => declaration.Initializer is FirArrayLiteral);
        var array = Assert.IsType<FirArrayLiteral>(literal.Initializer);
        Assert.Empty(array.Elements);
        Assert.Equal(ResolvedTypeKind.Function, array.Type.Arguments[0].Kind);
        Assert.Equal("std.core.String", array.Type.Arguments[0].Arguments[0].Name);
    }

    [Fact]
    public void NestedFunctionArraysTraceArrayObjectsButNotTheirCodePointers()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(
            "func increment(value: int): int = value + 1\n"
            + "object Main { func main(args: []String): () {\n"
            + "val callbacks: [][]((int) -> int) = [[increment], [increment]]\n"
            + "val result = callbacks[1][0](41)\n"
            + "} }")], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var program = compilation.Program!.Program;
        var entry = program.Functions[program.Entry];
        var outerDeclaration = Assert.Single(entry.Body!.Statements.OfType<FirDeclare>(),
            declaration => declaration.Initializer is FirArrayLiteral);
        var outer = Assert.IsType<FirArrayLiteral>(outerDeclaration.Initializer);
        Assert.Equal(ResolvedTypeKind.Array, outer.Type.Arguments[0].Kind);
        Assert.Equal(ResolvedTypeKind.Function, outer.Type.Arguments[0].Arguments[0].Kind);
        Assert.All(outer.Elements, row => Assert.IsType<FirArrayLiteral>(row));
        var result = Assert.Single(entry.Body.Statements.OfType<FirDeclare>(),
            declaration => declaration.Initializer is FirIndirectCall);
        Assert.IsType<FirIndirectCall>(result.Initializer);
    }

    [Fact]
    public void CapturingLambdaStillCannotEnterTheNativeArrayAbi()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(
            "object Main { func main(args: []String): () {\n"
            + "val seed = 41\n"
            + "val callbacks: []((int) -> int) = [{ value -> value + seed }]\n"
            + "} }")], TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
    }
}
