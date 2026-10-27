// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FirGenericVarargTests
{
    private static FirCompilation Compile(string body, string declarations = "") => FirCompilation.Create([
        AstTestSources.Parse("import std.stdio\n" + declarations + "\nobject Main { func main(args: []String) throws IOException { " + body + " } }")],
        TestContext.Current.CancellationToken);

    [Fact]
    public void ClosedGenericVarargsPackScalarsAndForwardOneArray()
    {
        var compilation = Compile("println(first(1, 2)); println(relay(7, 8)); val values = [3, 4]; println(first(*values)); println(first(values = [5, 6])); println(size<int>())",
            "func <T> first(vararg values: T): T = values[0]\nfunc <U> relay(left, right: U): U = first<U>(left, right)\nfunc <T> size(vararg values: T): int = values.length");

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var program = compilation.Program.Program;
        var firstInstances = program.Functions.Where(function => function.Name == "first" && !function.SpecializationArguments.IsEmpty).ToArray();
        Assert.Single(firstInstances);
        var first = firstInstances[0];
        var values = Assert.Single(first.Parameters);
        Assert.Equal(ResolvedTypeKind.Array, values.Type.Kind);
        Assert.Equal("int", values.Type.Arguments[0].Name);
        Assert.Equal(AstPassingMode.Value, values.Mode);
        var index = Assert.IsType<FirArrayIndex>(Assert.IsType<FirReturn>(Assert.Single(first.Body!.Statements)).Value);
        Assert.Equal("int", index.Type.Name);

        var size = Assert.Single(program.Functions.Where(function => function.Name == "size" && !function.SpecializationArguments.IsEmpty));
        Assert.IsType<FirArrayLength>(Assert.IsType<FirReturn>(Assert.Single(size.Body!.Statements)).Value);
        Assert.Contains("sol_rt_array_new", LoweringTrace.Inspect(compilation.Program,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void EmptyGenericVarargRequiresExplicitTypeArgument()
    {
        var valid = Compile("println(size<int>())", "func <T> size(vararg values: T): int = values.length");
        Assert.NotNull(valid.Program);
        Assert.Empty(valid.Diagnostics);

        var missingType = Compile("println(size())", "func <T> size(vararg values: T): int = values.length");
        Assert.Null(missingType.Program);
        Assert.NotEmpty(missingType.Diagnostics);
    }

    [Fact]
    public void GenericVarargCallLowersWithoutTheStdioAdapter()
    {
        var compilation = Compile("val count = countItems(1, 2)",
            "func <T> countItems(vararg values: T): int = values.length");

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var program = compilation.Program.Program;
        var main = program.Functions.Single(function => function.Name == "main");
        var declaration = Assert.IsType<FirDeclare>(main.Body!.Statements[0]);
        var call = Assert.IsType<FirCall>(declaration.Initializer);
        var target = program.Functions[call.Target];
        Assert.Equal("countItems", target.Name);
        Assert.Equal(FirRuntimeFunction.None, target.RuntimeFunction);
        Assert.IsType<FirArrayLiteral>(Assert.Single(call.Arguments).Value);
    }

    [Fact]
    public void MixedScalarAndSpreadVarargsAreRejectedBeforeVerification()
    {
        var compilation = Compile("val values = [2, 3]; println(size(1, *values))",
            "func <T> size(vararg values: T): int = values.length");

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
    }

    [Fact]
    public void NestedArrayElementUsesTheReferenceArrayVarargLayout()
    {
        var compilation = Compile("println(size([1]))", "func <T> size(vararg values: T): int = values.length");

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var program = compilation.Program.Program;
        var size = Assert.Single(program.Functions,
            function => function.Name == "size" && !function.SpecializationArguments.IsEmpty);
        var packedType = Assert.Single(size.Parameters).Type;
        Assert.Equal(ResolvedTypeKind.Array, packedType.Kind);
        var elementType = Assert.Single(packedType.Arguments);
        Assert.Equal(ResolvedTypeKind.Array, elementType.Kind);
        Assert.Equal("int", Assert.Single(elementType.Arguments).Name);
        Assert.Contains("sol_rt_array_new", LoweringTrace.Inspect(compilation.Program,
            cancellationToken: TestContext.Current.CancellationToken));
    }
}
