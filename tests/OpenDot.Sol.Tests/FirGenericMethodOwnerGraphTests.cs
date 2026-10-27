// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FirGenericMethodOwnerGraphTests
{
    [Fact]
    public void VerifierRejectsMethodInstanceDuplicatedWithinOneOwnerGraph()
    {
        var program = Compile("""
            object Utility { func <T> identity(value: T): T = value }
            object Main { func main(args: []String) { val value = Utility.identity<int>(1) } }
            """);
        var instance = Assert.Single(program.Functions,
            function => function.Name == "identity" && !function.SpecializationArguments.IsEmpty);
        var duplicate = instance with { Id = program.Functions.Length };
        var forged = program with { Functions = program.Functions.Add(duplicate) };

        var result = FirVerifier.Verify(forged, TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("duplicate closed method instance", StringComparison.Ordinal));
    }

    [Fact]
    public void VerifierRejectsOrphanedGenericMethodInstanceAfterItsOnlyCallIsRemoved()
    {
        var program = Compile("""
            object Utility { func <T> identity(value: T): T = value }
            object Main { func main(args: []String) { val value = Utility.identity<int>(1) } }
            """);
        var main = Assert.Single(program.Functions, function => function.Name == "main");
        var changedMain = main with { Body = new FirBlock([], main.Body!.Origin) };
        var forged = Replace(program, changedMain);

        var result = FirVerifier.Verify(forged, TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("orphaned from every source-bound FIR call edge", StringComparison.Ordinal));
    }

    [Fact]
    public void VerifierRejectsGenericMethodCallRetargetedAcrossOwners()
    {
        var program = Compile("""
            object Left { func <T> identity(value: T): T = value }
            object Right { func <T> identity(value: T): T = value }
            object Main { func main(args: []String) {
                val left = Left.identity<int>(1)
                val right = Right.identity<int>(2)
            } }
            """);
        var main = Assert.Single(program.Functions, function => function.Name == "main");
        var declarations = main.Body!.Statements.OfType<FirDeclare>().ToArray();
        var leftCall = Assert.IsType<FirCall>(declarations[0].Initializer);
        var rightCall = Assert.IsType<FirCall>(declarations[1].Initializer);
        var retargeted = leftCall with { Target = rightCall.Target };
        var changedMain = main with
        {
            Body = main.Body with
            {
                Statements = main.Body.Statements.SetItem(0,
                    declarations[0] with { Initializer = retargeted }),
            },
        };

        var result = FirVerifier.Verify(Replace(program, changedMain), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("receiver differs from its specialized target owner", StringComparison.Ordinal));
    }

    private static FirProgram Compile(string source)
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(source)], TestContext.Current.CancellationToken);
        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        return compilation.Program.Program;
    }

    private static FirProgram Replace(FirProgram program, FirFunction replacement)
        => program with { Functions = program.Functions.SetItem(replacement.Id, replacement) };
}
