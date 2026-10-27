// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FirGenericMethodOwnerGraphTamperTests
{
    [Fact]
    public void VerifierRejectsReorderedGenericMethodCallTypeArguments()
    {
        var program = Compile(ValidSource);
        var (main, leftCall, _) = GetCalls(program);
        var stringType = program.Analysis.Types.Types.Single(type => type.Name == "std.core.String");
        var intType = program.Analysis.Types.Types.Single(type => type.Name == "int");
        var alteredCall = leftCall with { TypeArguments = [stringType, intType] };
        var forged = Replace(program, ReplaceCall(main, leftCall, alteredCall));

        var result = FirVerifier.Verify(forged, TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("type arguments", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsGenericMethodInstanceWithChangedSpecializationArguments()
    {
        var program = Compile(ValidSource);
        var (_, leftCall, _) = GetCalls(program);
        var instance = program.Functions[leftCall.Target];
        var forgedInstance = instance with
        {
            SpecializationArguments = instance.SpecializationArguments.Reverse().ToImmutableArray(),
        };
        var forged = program with { Functions = program.Functions.SetItem(instance.Id, forgedInstance) };

        var result = FirVerifier.Verify(forged, TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && (diagnostic.Message.Contains("specialization", StringComparison.OrdinalIgnoreCase)
                || diagnostic.Message.Contains("type arguments", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void VerifierRejectsCallOriginBorrowedFromAnotherOwnerEdge()
    {
        var program = Compile(ValidSource);
        var (main, leftCall, rightCall) = GetCalls(program);
        var forgedCall = leftCall with { Origin = rightCall.Origin };
        var forged = Replace(program, ReplaceCall(main, leftCall, forgedCall));

        var result = FirVerifier.Verify(forged, TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("origin differs", StringComparison.OrdinalIgnoreCase));
    }

    private const string ValidSource = """
        object Left { func <A, B> select(first: A, second: B): A = first }
        object Right { func <A, B> select(first: A, second: B): A = first }
        object Main { func main(args: []String) {
            val left = Left.select<int, String>(1, "left")
            val right = Right.select<int, String>(2, "right")
        } }
        """;

    private static FirProgram Compile(string source)
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(source)], TestContext.Current.CancellationToken);
        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        return compilation.Program.Program;
    }

    private static (FirFunction Main, FirCall Left, FirCall Right) GetCalls(FirProgram program)
    {
        var main = Assert.Single(program.Functions, function => function.Name == "main");
        var declarations = main.Body!.Statements.OfType<FirDeclare>().ToArray();
        return (main, Assert.IsType<FirCall>(declarations[0].Initializer),
            Assert.IsType<FirCall>(declarations[1].Initializer));
    }

    private static FirFunction ReplaceCall(FirFunction main, FirCall before, FirCall after)
    {
        var statements = main.Body!.Statements.ToBuilder();
        int index = -1;
        for (int candidate = 0; candidate < statements.Count; candidate++)
        {
            if (statements[candidate] is FirDeclare { Initializer: { } initializer }
                && ReferenceEquals(initializer, before)) { index = candidate; break; }
        }
        Assert.True(index >= 0);
        var declaration = (FirDeclare)statements[index];
        statements[index] = declaration with { Initializer = after };
        return main with { Body = main.Body with { Statements = statements.ToImmutable() } };
    }

    private static FirProgram Replace(FirProgram program, FirFunction replacement)
        => program with { Functions = program.Functions.SetItem(replacement.Id, replacement) };
}
