// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class ConstructionDelegateProofTests
{
    private static BodyAnalysis Analyze(string provider)
    {
        var session = AstTestSources.Session("module app\nimport std.reflect\n"
            + "class D { operator func getValue(thisRef: C, property: SolProperty<int>): int = 1; "
            + "operator func provideDelegate(thisRef: C, property: SolProperty<int>): D { " + provider + " } }\n"
            + "class C { val value: int by D(); val later: int = 1 }\nfunc use(): C = C()",
            GenericMemberProofTestSource.Core, "module std.reflect\nclass SolProperty<T> {}");
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void AProviderThatDoesNotObserveTheReceiverCanComplete()
    {
        var analysis = Analyze("return this");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.Equal(BodyBindingStatus.Bound, analysis.ConstructionProofs.Values.Single(proof =>
            proof.Selection.Site is AstCall { Origin.Text: "C()" }).Status);
    }

    [Fact]
    public void AProviderCannotReadALaterOwnerField()
    {
        var analysis = Analyze("val copy = thisRef.later; return this");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2271");
        Assert.Equal(BodyBindingStatus.Invalid, analysis.ConstructionProofs.Values.Single(proof =>
            proof.Selection.Site is AstCall { Origin.Text: "C()" }).Status);
    }
}
