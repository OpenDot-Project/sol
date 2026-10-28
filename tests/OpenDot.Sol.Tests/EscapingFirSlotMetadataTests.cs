// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class EscapingFirSlotMetadataTests
{
    private const string Source = "func retain(@escaping callback: () -> int) { }\n"
        + "func invoke(callback: () -> int) { }\n"
        + "func retainPair(@escaping first, second: () -> int) { }\n"
        + "object Main { func main(args: []String) { "
        + "val withEscaping = func(@escaping callback: () -> int): () { }; "
        + "val withoutEscaping = func(callback: () -> int): () { }; "
        + "val map: (int) -> int = { value -> value + 1 }; val local = 1; "
        + "} }";

    [Fact]
    public void BuildersPreserveFunctionParameterEscapeMetadataAndKeepLocalsFalse()
    {
        var program = Compile();

        Assert.True(Parameter(program, "retain", "callback").IsEscaping);
        Assert.False(Parameter(program, "invoke", "callback").IsEscaping);
        Assert.All(new[] { "first", "second" }, name =>
            Assert.True(Parameter(program, "retainPair", name).IsEscaping));

        var anonymousFunctions = program.Functions.Where(function => function.AnonymousFunctionSource is not null)
            .OrderBy(function => function.AnonymousFunctionSource!.Origin.Span.Start).ToArray();
        Assert.Equal(2, anonymousFunctions.Length);
        Assert.True(Assert.Single(anonymousFunctions[0].Parameters).IsEscaping);
        Assert.False(Assert.Single(anonymousFunctions[1].Parameters).IsEscaping);

        var lambda = Assert.Single(program.Functions, function => function.LambdaSource is not null);
        Assert.False(Assert.Single(lambda.Parameters).IsEscaping);
        Assert.All(program.Functions.SelectMany(function => function.Slots)
            .Where(slot => !slot.IsParameter), slot => Assert.False(slot.IsEscaping));
    }

    [Fact]
    public void VerifierRejectsNormalFunctionEscapeContractTampering()
    {
        var program = Compile();
        var target = Assert.Single(program.Functions, function => function.Name == "retain");
        var forged = ReplaceParameter(target, "callback", escaping: false);

        var result = FirVerifier.Verify(Replace(program, forged), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("parameter metadata differs from its source declaration", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsAnonymousFunctionEscapeContractTampering()
    {
        var program = Compile();
        var target = Assert.Single(program.Functions, function => function.AnonymousFunctionSource is not null
            && function.Parameters.Any(parameter => parameter.Name == "callback" && parameter.IsEscaping));
        var forged = ReplaceParameter(target, "callback", escaping: false);

        var result = FirVerifier.Verify(Replace(program, forged), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("parameter slot differs from its source contract", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsEscapeMetadataInventedForLambdaParameter()
    {
        var program = Compile();
        var target = Assert.Single(program.Functions, function => function.LambdaSource is not null);
        var original = Assert.Single(target.Parameters);
        var forgedParameter = original with { IsEscaping = true };
        var forged = ReplaceParameter(target, original.Id, forgedParameter);

        var result = FirVerifier.Verify(Replace(program, forged), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("parameter slot differs from its contextual signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsEscapeMetadataInventedForLocalSlot()
    {
        var program = Compile();
        var target = Assert.Single(program.Functions, function => function.Name == "main");
        var original = Assert.Single(target.Slots, slot => slot.Name == "local");
        var forgedSlot = original with { IsEscaping = true };
        var forged = target with { Slots = target.Slots.SetItem(original.Id, forgedSlot) };

        var result = FirVerifier.Verify(Replace(program, forged), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("local FIR slot cannot carry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsEscapeMetadataRemovedFromOneNameInGroupedParameter()
    {
        var program = Compile();
        var target = Assert.Single(program.Functions, function => function.Name == "retainPair");
        var forged = ReplaceParameter(target, "second", escaping: false);

        var result = FirVerifier.Verify(Replace(program, forged), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("parameter metadata differs from its source declaration", StringComparison.OrdinalIgnoreCase));
    }

    private static FirSlot Parameter(FirProgram program, string functionName, string parameterName)
    {
        var function = Assert.Single(program.Functions, candidate => candidate.Name == functionName);
        return Assert.Single(function.Parameters, parameter => parameter.Name == parameterName);
    }

    private static FirProgram Compile()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(Source)],
            TestContext.Current.CancellationToken);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        Assert.Empty(compilation.Diagnostics);
        return compilation.Program.Program;
    }

    private static FirProgram Replace(FirProgram program, FirFunction replacement)
        => program with { Functions = program.Functions.SetItem(replacement.Id, replacement) };

    private static FirFunction ReplaceParameter(FirFunction function, string name, bool escaping)
    {
        var original = Assert.Single(function.Parameters, parameter => parameter.Name == name);
        return ReplaceParameter(function, original.Id, original with { IsEscaping = escaping });
    }

    private static FirFunction ReplaceParameter(FirFunction function, int parameterId, FirSlot replacement)
    {
        int index = Array.FindIndex(function.Parameters.ToArray(), parameter => parameter.Id == parameterId);
        Assert.True(index >= 0);
        var slotIndex = Array.FindIndex(function.Slots.ToArray(), slot => slot.Id == parameterId);
        Assert.True(slotIndex >= 0);
        return function with
        {
            Parameters = function.Parameters.SetItem(index, replacement),
            Slots = function.Slots.SetItem(slotIndex, replacement),
        };
    }
}
