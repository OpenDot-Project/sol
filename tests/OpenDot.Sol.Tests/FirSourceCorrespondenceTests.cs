// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FirSourceCorrespondenceTests
{
    [Fact]
    public void GenericReadCannotBeReplacedBySameTypeLiteralAtTheSameOrigin()
    {
        var program = Compile("""
            func <T> identity(value: T): T = value
            object Main { func main(args: []String) { identity<int>(7) } }
            """);
        var identity = Assert.Single(program.Program.Functions,
            function => function.Name == "identity" && !function.SpecializationArguments.IsEmpty);
        var body = Assert.IsType<FirBlock>(identity.Body);
        var returned = Assert.IsType<FirReturn>(Assert.Single(body.Statements));
        var sourceRead = Assert.IsType<FirRead>(returned.Value);
        var forged = new FirConstant("7", sourceRead.Type, sourceRead.Origin);
        var changedFunction = identity with
        {
            Body = body with { Statements = body.Statements.SetItem(0, returned with { Value = forged }) },
        };

        var result = FirVerifier.Verify(Replace(program.Program, changedFunction), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("expression kind", System.StringComparison.Ordinal));
    }

    [Fact]
    public void SameTypeBinaryOperatorCannotBeChangedAfterBinding()
    {
        var program = Compile("""
            func add(value: int): int = value + 1
            object Main { func main(args: []String) { add(2) } }
            """);
        var add = Assert.Single(program.Program.Functions, function => function.Name == "add");
        var body = Assert.IsType<FirBlock>(add.Body);
        var returned = Assert.IsType<FirReturn>(Assert.Single(body.Statements));
        var binary = Assert.IsType<FirBinary>(returned.Value);
        var changedFunction = add with
        {
            Body = body with
            {
                Statements = body.Statements.SetItem(0, returned with { Value = binary with { Operation = "-" } }),
            },
        };

        var result = FirVerifier.Verify(Replace(program.Program, changedFunction), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("binary operator", System.StringComparison.Ordinal));
    }

    [Fact]
    public void GenericSpecializationCannotOmitAnEffectfulSourceCall()
    {
        var program = Compile("""
            import std.stdio
            func effect(): () throws IOException { println("effect") }
            func <T> effectThenReturn(value: T): T throws IOException { effect(); value }
            object Main { func main(args: []String) throws IOException { effectThenReturn<int>(3) } }
            """);
        var specialized = Assert.Single(program.Program.Functions,
            function => function.Name == "effectThenReturn" && !function.SpecializationArguments.IsEmpty);
        var body = Assert.IsType<FirBlock>(specialized.Body);
        Assert.IsType<FirEvaluate>(body.Statements[0]);
        var changedFunction = specialized with { Body = body with { Statements = body.Statements.RemoveAt(0) } };

        var result = FirVerifier.Verify(Replace(program.Program, changedFunction), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("source-bound call is absent", System.StringComparison.Ordinal));
    }

    [Fact]
    public void GenericArrayIterationChecksItsSubstitutedClosedArrayType()
    {
        var program = Compile("""
            func <T> first(values: []T): T {
                for val item in values { return item }
                return values[0]
            }
            object Main { func main(args: []String) { first<int>([5]) } }
            """);

        var first = Assert.Single(program.Program.Functions,
            function => function.Name == "first" && !function.SpecializationArguments.IsEmpty);
        Assert.Equal("int", Assert.Single(first.SpecializationArguments).Name);
        Assert.Contains(first.Body!.Statements, statement => statement is FirArrayForEach);
    }

    [Fact]
    public void GenericVarargSyntheticArraysAreCheckedAgainstTheirCallArguments()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("""
            import std.stdio
            func <T> first(vararg values: T): T = values[0]
            func <T> size(vararg values: T): int = values.length
            object Main { func main(args: []String) throws IOException {
                val values = [3, 4]
                println(first<int>(1, 2))
                println(first<int>(*values))
                println(first<int>(values = values))
                println(size<int>())
            } }
            """)], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        Assert.Empty(compilation.Diagnostics);
    }

    private static VerifiedFirProgram Compile(string source)
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(source)], TestContext.Current.CancellationToken);
        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        return compilation.Program;
    }

    private static FirProgram Replace(FirProgram program, FirFunction replacement)
        => program with { Functions = program.Functions.SetItem(replacement.Id, replacement) };
}
