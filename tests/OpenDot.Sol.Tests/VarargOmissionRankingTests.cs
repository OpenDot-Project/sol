// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class VarargOmissionRankingTests
{
    private const string Overloads = "func <T> select(value: T): T = value\n"
        + "func select(value: int, vararg rest: int): int = value\n"
        + "func main(value: int): int { select(value) }";

    private static SourceAnalysis Sources(string source) => SourceAnalysis.Create(
        AstTestSources.Session("module app\n" + source), TestContext.Current.CancellationToken);

    [Fact]
    public void DirectCallRankingDoesNotTreatOmittedVarargAsExpansion()
    {
        var sources = Sources(Overloads);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Message)));
        var analysis = CallAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var binding = Assert.Single(analysis.Bindings);

        Assert.Equal(CallBindingStatus.Bound, binding.Status);
        var selected = Assert.IsType<AstFunction>(Assert.Single(binding.Selected!.Symbol.Parts));
        Assert.Contains(selected.Parameters, parameter => parameter.PassingMode == AstPassingMode.Vararg);
        Assert.Equal(0, binding.Selected.Symbol.Arity);

        var varargCandidate = Assert.Single(binding.Candidates,
            candidate => candidate.Symbol.Parts.OfType<AstFunction>()
                .Any(function => function.Parameters.Any(parameter => parameter.PassingMode == AstPassingMode.Vararg)));
        Assert.Equal(CallCandidateStatus.Applicable, varargCandidate.Status);
        Assert.False(varargCandidate.ExpandsVararg);
        Assert.Equal(new[] { 1 }, varargCandidate.EmptyVarargParameters);
    }

    [Fact]
    public void BodyRankingDoesNotTreatOmittedVarargAsExpansion()
    {
        var sources = Sources(Overloads);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var call = Assert.Single(analysis.Calls);

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
        Assert.Contains(call.Target.Parameters, parameter => parameter.PassingMode == AstPassingMode.Vararg);
        Assert.Equal(new[] { 1 }, call.EmptyVarargParameters);
    }

    [Fact]
    public void SupplyingAnElementStillCountsAsVarargExpansion()
    {
        var sources = Sources("func collect(vararg values: int): int = 0\n"
            + "func main(): int { collect(1) }");
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Message)));
        var analysis = CallAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var selected = Assert.Single(analysis.Bindings).Selected!;

        Assert.True(selected.ExpandsVararg);
        Assert.Empty(selected.EmptyVarargParameters);
        Assert.True(Assert.Single(selected.Arguments).IsExpandedVararg);
    }
}
