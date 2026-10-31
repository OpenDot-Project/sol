// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionFirMetadataIntegrityTests
{
    [Theory]
    [InlineData("name")]
    [InlineData("runtime")]
    [InlineData("export")]
    [InlineData("specialization")]
    public void VerifierRejectsSyntheticMetadataThatClaimsNamedFunctionIdentity(string field)
    {
        var program = Compile();
        var index = Array.FindIndex(program.Functions.ToArray(), function => function.AnonymousFunctionSource is not null);
        Assert.True(index >= 0);
        var function = program.Functions[index];
        var forged = field switch
        {
            "name" => function with { Name = "callback" },
            "runtime" => function with { RuntimeFunction = FirRuntimeFunction.Println },
            "export" => function with { ExportName = "callback" },
            "specialization" => function with
            {
                SpecializationArguments = [program.Analysis.Types.GetPrimitive("int")],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var result = FirVerifier.Verify(program with { Functions = program.Functions.SetItem(index, forged) },
            TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("identity or metadata", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsAnonymousParameterThatLosesParameterIdentity()
    {
        var program = Compile();
        int index = Array.FindIndex(program.Functions.ToArray(), function => function.AnonymousFunctionSource is not null);
        var function = program.Functions[index];
        var parameter = Assert.Single(function.Parameters);
        var forgedParameter = parameter with { IsParameter = false };
        var forged = function with
        {
            Parameters = function.Parameters.SetItem(0, forgedParameter),
            Slots = function.Slots.SetItem(parameter.Id, forgedParameter),
        };

        var result = FirVerifier.Verify(program with { Functions = program.Functions.SetItem(index, forged) },
            TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("parameter slot differs from its source contract", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsAnonymousParameterNameChangedWithoutChangingItsType()
    {
        var program = Compile();
        int index = Array.FindIndex(program.Functions.ToArray(), function => function.AnonymousFunctionSource is not null);
        var function = program.Functions[index];
        var parameter = Assert.Single(function.Parameters);
        var forgedParameter = parameter with { Name = "different" };
        var forged = function with
        {
            Parameters = function.Parameters.SetItem(0, forgedParameter),
            Slots = function.Slots.SetItem(parameter.Id, forgedParameter),
        };

        var result = FirVerifier.Verify(program with { Functions = program.Functions.SetItem(index, forged) },
            TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("parameter slot differs from its source contract", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsAnonymousParameterOriginDetachedFromItsSourceName()
    {
        var program = Compile();
        int index = Array.FindIndex(program.Functions.ToArray(), function => function.AnonymousFunctionSource is not null);
        var function = program.Functions[index];
        var parameter = Assert.Single(function.Parameters);
        var otherOrigin = Assert.Single(program.Functions, candidate => candidate.Name == "main").Parameters[0].Origin;
        var forgedParameter = parameter with { Origin = otherOrigin };
        var forged = function with
        {
            Parameters = function.Parameters.SetItem(0, forgedParameter),
            Slots = function.Slots.SetItem(parameter.Id, forgedParameter),
        };

        var result = FirVerifier.Verify(program with { Functions = program.Functions.SetItem(index, forged) },
            TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("parameter slot differs from its source contract", StringComparison.OrdinalIgnoreCase));
    }

    private static FirProgram Compile()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(
            "object Main { func main(args: []String) { "
            + "val callback = func(value: int): int = value + 1; val answer = callback(41) } }")],
            TestContext.Current.CancellationToken);
        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        return compilation.Program.Program;
    }
}
