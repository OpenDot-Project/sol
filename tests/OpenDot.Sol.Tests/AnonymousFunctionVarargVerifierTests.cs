// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionVarargVerifierTests
{
    private static FirProgram Compile()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(
            "object Main { func main(args: []String) { "
            + "val callback: ([]int) -> int = func(vararg values): int { values[0] }; "
            + "val result = callback([42]) } }")], TestContext.Current.CancellationToken);
        Assert.NotNull(compilation.Program);
        return compilation.Program.Program;
    }

    [Fact]
    public void VarargSourceParameterProducesOneArrayValuedValueSlot()
    {
        var program = Compile();
        var function = Assert.Single(program.Functions,
            candidate => candidate.AnonymousFunctionSource is not null);
        var parameter = Assert.Single(function.Parameters);

        Assert.Equal(AstPassingMode.Value, parameter.Mode);
        Assert.Equal(ResolvedTypeKind.Array, parameter.Type.Kind);
        Assert.Equal("int", parameter.Type.Arguments[0].Name);
        Assert.Equal(parameter, Assert.Single(function.Slots.Where(slot => slot.IsParameter)));
        Assert.NotNull(FirVerifier.Verify(program, TestContext.Current.CancellationToken).Program);
    }

    [Fact]
    public void VerifierRejectsVarargSlotNarrowedToItsElementType()
    {
        var program = Compile();
        var function = Assert.Single(program.Functions,
            candidate => candidate.AnonymousFunctionSource is not null);
        var elementType = program.Analysis.Types.GetPrimitive("int");
        var forgedParameter = Assert.Single(function.Parameters) with { Type = elementType };
        var forged = function with
        {
            Parameters = function.Parameters.SetItem(0, forgedParameter),
            Slots = function.Slots.SetItem(0, forgedParameter),
        };

        var result = FirVerifier.Verify(program with
        { Functions = program.Functions.SetItem(function.Id, forged) }, TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402");
    }

    [Fact]
    public void VerifierRejectsVarargArraySlotChangedToOutMode()
    {
        var program = Compile();
        var function = Assert.Single(program.Functions,
            candidate => candidate.AnonymousFunctionSource is not null);
        var parameter = Assert.Single(function.Parameters) with { Mode = AstPassingMode.Out };
        var forged = function with
        {
            Parameters = function.Parameters.SetItem(0, parameter),
            Slots = function.Slots.SetItem(0, parameter),
        };

        var result = FirVerifier.Verify(program with
        { Functions = program.Functions.SetItem(function.Id, forged) }, TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402");
    }
}
