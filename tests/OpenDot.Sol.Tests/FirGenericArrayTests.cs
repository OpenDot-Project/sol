// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FirGenericArrayTests
{
    private static VerifiedFirProgram Compile(string body, string declarations)
    {
        var compilation = FirCompilation.Create([
            AstTestSources.Parse("import std.stdio\n" + declarations
                + "\nobject Main { func main(args: []String) throws IOException { " + body + " } }")],
            TestContext.Current.CancellationToken);
        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        return compilation.Program;
    }

    [Fact]
    public void GenericArrayLengthAndIndexLowerForClosedNativeElements()
    {
        var program = Compile(
            "println(arrayLength<int>([1, 2])); println(first<String>([\"value\"]))",
            "func <T> arrayLength(values: []T): int = values.length\n"
                + "func <T> first(values: []T): T = values[0]");

        var length = Assert.Single(program.Program.Functions,
            function => function.Name == "arrayLength" && !function.SpecializationArguments.IsEmpty);
        Assert.Equal("int", Assert.Single(length.SpecializationArguments).Name);
        Assert.Equal("int", Assert.Single(length.Parameters).Type.Arguments[0].Name);
        var lengthReturn = Assert.IsType<FirReturn>(Assert.Single(length.Body!.Statements));
        var lengthExpression = Assert.IsType<FirArrayLength>(lengthReturn.Value);
        Assert.Equal(OpenDot.Sol.Analysis.ResolvedTypeKind.Array, lengthExpression.Array.Type.Kind);
        Assert.Equal("int", Assert.Single(lengthExpression.Array.Type.Arguments).Name);

        var first = Assert.Single(program.Program.Functions,
            function => function.Name == "first" && !function.SpecializationArguments.IsEmpty);
        Assert.Equal("std.core.String", Assert.Single(first.SpecializationArguments).Name);
        Assert.Equal("std.core.String", Assert.Single(first.Parameters).Type.Arguments[0].Name);
        var firstReturn = Assert.IsType<FirReturn>(Assert.Single(first.Body!.Statements));
        var index = Assert.IsType<FirArrayIndex>(firstReturn.Value);
        Assert.Equal("std.core.String", index.Type.Name);
        Assert.Equal(index.Array.Type.Arguments[0], index.Type);
        Assert.NotEmpty(LoweringTrace.Inspect(program, cancellationToken: TestContext.Current.CancellationToken));
    }
}
