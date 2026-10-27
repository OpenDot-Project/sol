// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FirGenericNestedArrayTests
{
    [Fact]
    public void ClosedNestedArrayInstancesBindIndexLengthIterationAndLowerReferenceElements()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("""
            import std.stdio
            func <T> first(values: [][]T): []T = values[0]
            func <T> firstValue(values: [][]T): T = values[0][0]
            func <T> firstLength(values: [][]T): int = values[0].length
            object Main {
                func main(args: []String) throws IOException {
                    val rows: [][]int = [[1, 2], [3]]
                    println(firstValue<int>(rows))
                    println(firstLength<int>(rows))
                    for val row in rows { println(row[0]) }
                    println(first<String>([["nested"]])[0])
                }
            }
            """)], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        Assert.Empty(compilation.Diagnostics);
        Assert.NotNull(compilation.Program);
        var verified = compilation.Program;
        var intInstance = Assert.Single(verified.Program.Functions,
            function => function.Name == "firstValue" && !function.SpecializationArguments.IsEmpty);
        Assert.Equal("int", Assert.Single(intInstance.SpecializationArguments).Name);
        Assert.Equal(ResolvedTypeKind.Array, Assert.Single(intInstance.Parameters).Type.Kind);
        Assert.Equal(ResolvedTypeKind.Array, Assert.Single(intInstance.Parameters).Type.Arguments[0].Kind);
        Assert.Equal("int", Assert.Single(Assert.Single(intInstance.Parameters).Type.Arguments[0].Arguments).Name);
        var returnValue = Assert.IsType<FirReturn>(Assert.Single(intInstance.Body!.Statements));
        var outerIndex = Assert.IsType<FirArrayIndex>(returnValue.Value);
        Assert.Equal(ResolvedTypeKind.Array, outerIndex.Array.Type.Kind);
        Assert.Equal("int", outerIndex.Type.Name);

        var lengthInstance = Assert.Single(verified.Program.Functions,
            function => function.Name == "firstLength" && !function.SpecializationArguments.IsEmpty);
        var lengthReturn = Assert.IsType<FirReturn>(Assert.Single(lengthInstance.Body!.Statements));
        var length = Assert.IsType<FirArrayLength>(lengthReturn.Value);
        Assert.Equal(ResolvedTypeKind.Array, length.Array.Type.Kind);

        var stringInstance = Assert.Single(verified.Program.Functions,
            function => function.Name == "first" && !function.SpecializationArguments.IsEmpty);
        Assert.Equal("std.core.String", Assert.Single(stringInstance.SpecializationArguments).Name);
        Assert.Contains(verified.Program.Functions.Where(function => function.Body is not null)
            .SelectMany(function => function.Body!.Statements), statement => statement is FirArrayForEach);

        var trace = LoweringTrace.Inspect(verified, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("sol_rt_array_new", trace);
        Assert.Contains("\"text\":\"11\"", trace);
    }

    [Fact]
    public void NestedReferenceArrayCanBeReturnedAndForwardedThroughMultipleInstances()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("""
            func <T> identity(value: T): T = value
            func <T> first(values: [][]T): []T = values[0]
            func <T> relay(values: [][]T): T = identity<T>(first<T>(values)[0])
            object Main { func main(args: []String) { relay<int>([[21]]) } }
            """)], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        Assert.Empty(compilation.Diagnostics);
        Assert.NotNull(compilation.Program);
        var verified = compilation.Program;
        var relay = Assert.Single(verified.Program.Functions,
            function => function.Name == "relay" && !function.SpecializationArguments.IsEmpty);
        var returned = Assert.IsType<FirReturn>(Assert.Single(relay.Body!.Statements));
        var identityCall = Assert.IsType<FirCall>(returned.Value);
        var indexed = Assert.IsType<FirArrayIndex>(Assert.Single(identityCall.Arguments).Value);
        var nestedCall = Assert.IsType<FirCall>(indexed.Array);
        Assert.Equal("first", verified.Program.Functions[nestedCall.Target].Name);
        Assert.Equal("int", Assert.Single(nestedCall.TypeArguments).Name);
        Assert.Contains(verified.Program.Functions,
            function => function.Name == "identity" && function.SpecializationArguments.Length == 1
                && function.SpecializationArguments[0].Name == "int");
        Assert.NotEmpty(LoweringTrace.Inspect(verified,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void UnsupportedArrayElementLeavesNoVerifiedProgram()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("""
            func <T> first(values: [][]T): T = values[0][0]
            object Main { func main(args: []String) { first<half>([[1.0]]) } }
            """)], TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.NotEmpty(compilation.Diagnostics);
    }
}
