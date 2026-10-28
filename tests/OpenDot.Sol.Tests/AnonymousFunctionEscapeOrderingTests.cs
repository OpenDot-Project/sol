// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using System.Collections.Generic;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionEscapeOrderingTests
{
    [Fact]
    public void MultiFileEscapeInventoryUsesPathOrderIndependentOfInputOrder()
    {
        const string firstPath = "zeta.sol";
        const string secondPath = "alpha.sol";
        const string firstSource = "module app\nfunc create(seed: int) { "
            + "val callback = func(): int { seed }; callback() }";
        const string secondSource = "module app\nfunc create(seed: int): () -> int = "
            + "func(): int { seed }";

        string[] Forward() => Analyze((firstPath, firstSource), (secondPath, secondSource))
            .AnonymousFunctionEscapes.Select(Identity).ToArray();
        string[] Reverse() => Analyze((secondPath, secondSource), (firstPath, firstSource))
            .AnonymousFunctionEscapes.Select(Identity).ToArray();

        var forward = Forward();
        var reverse = Reverse();

        Assert.Equal(new[]
        {
            "alpha.sol:MayEscape",
            "zeta.sol:ProvenNonEscaping",
        }, forward);
        Assert.Equal(forward, reverse);

        static string Identity(AnonymousFunctionEscape escape) =>
            $"{escape.Function.Origin.Source.Path}:{escape.Disposition}";
    }

    private static LexicalAnalysis Analyze(params (string Path, string Source)[] files)
    {
        IEnumerable<SyntaxTree> trees = files.Select(file => SyntaxTree.Parse(
            SourceText.FromString(file.Source, file.Path), cancellationToken: TestContext.Current.CancellationToken));
        var session = AnalysisSession.Create(trees, new() { RequireCore = false },
            cancellationToken: TestContext.Current.CancellationToken);
        return LexicalAnalysis.Create(session, TestContext.Current.CancellationToken);
    }
}
