// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class InheritedNonescapingContractTests
{
    [Fact]
    public void TypedDerivedReceiverUsesInheritedNonescapingParameterContract()
    {
        var analysis = Analyze("module app\nopen class Base() { open func run(callback: () -> int): int = callback() }\n"
            + "class Child: Base() {}\nfunc use(ref value: int): int { "
            + "val child: Child = Child(); child.run(callback = { -> value }) }");

        Assert.False(analysis.HasErrors, Describe(analysis));
        Assert.Contains(analysis.Sources.Lexical.LambdaEscapes,
            escape => escape.Disposition == LambdaEscapeDisposition.ProvenNonEscaping);
    }

    [Fact]
    public void ThisReceiverUsesInheritedNonescapingParameterContract()
    {
        var analysis = Analyze("module app\nopen class Base() { open func run(callback: () -> int): int = callback() }\n"
            + "class Child: Base() { func use(ref value: int): int { this.run { -> value } } }");

        Assert.False(analysis.HasErrors, Describe(analysis));
        Assert.Contains(analysis.Sources.Lexical.LambdaEscapes,
            escape => escape.Disposition == LambdaEscapeDisposition.ProvenNonEscaping);
    }

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session(source);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static string Describe(BodyAnalysis analysis) => string.Join('\n', analysis.Diagnostics.Select(diagnostic =>
        diagnostic.Code + " " + diagnostic.Message));
}
