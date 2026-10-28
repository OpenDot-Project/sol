// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class NonescapingAliasBranchJoinTests
{
    [Fact]
    public void AllIfElseBranchesMayReplaceTheBorrowedCallableBeforeReturn()
    {
        var analysis = Analyze("module app\nfunc replace(flag: bool, callback: () -> int): () -> int { "
            + "var slot = callback; if flag { slot = { -> 42 } } else { slot = { -> 43 } }; return slot }");

        Assert.False(analysis.HasErrors, Describe(analysis));
    }

    [Fact]
    public void BranchThatRetainsTheBorrowedCallableKeepsTheLaterReturnInvalid()
    {
        var analysis = Analyze("module app\nfunc maybe(flag: bool, callback: () -> int): () -> int { "
            + "var slot = callback; if flag { slot = { -> 42 } } else { slot = callback }; return slot }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2264");
    }

    [Fact]
    public void NestedExhaustiveIfElseJoinPreservesDefiniteReplacement()
    {
        var analysis = Analyze("module app\nfunc replace(first: bool, second: bool, callback: () -> int): () -> int { "
            + "var slot = callback; if first { slot = { -> 1 } } else if second { slot = { -> 2 } } "
            + "else { slot = { -> 3 } }; return slot }");

        Assert.False(analysis.HasErrors, Describe(analysis));
    }

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session(source);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static string Describe(BodyAnalysis analysis) => string.Join('\n',
        analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message));
}
