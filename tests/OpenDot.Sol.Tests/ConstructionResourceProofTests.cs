// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using System.Text;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class ConstructionResourceProofTests
{
    private static void DeferredRoot(string source)
    {
        var analysis = GenericMemberProofTestSource.Analyze(source);
        var proof = Assert.Single(analysis.ConstructionProofs.Values, proof =>
            proof.Selection.Owner.Declaration?.Name == "Root");
        Assert.Equal(BodyBindingStatus.Deferred, proof.Status);
        Assert.False(analysis.IsComplete);
    }

    [Fact]
    public void AConstructorBodyBeyondTheNodeBudgetCannotSucceed()
    {
        var source = new StringBuilder("class Root { constructor() {\n");
        for (int index = 0; index < 40000; index++) { source.Append("0;\n"); }
        source.Append("} }");
        DeferredRoot(source.ToString());
    }

    [Fact]
    public void DistinctNestedInstancesShareOneConstructionBudget()
    {
        var source = new StringBuilder();
        for (int index = 0; index < 1025; index++)
        { source.Append("class C").Append(index).Append(" {}\n"); }
        source.Append("class Root { constructor() {\n");
        for (int index = 0; index < 1025; index++)
        { source.Append("C").Append(index).Append("();\n"); }
        source.Append("} }");
        DeferredRoot(source.ToString());
    }

    [Fact]
    public void RepeatedCallSitesCannotEvadeTheDependencyEdgeBudget()
    {
        var source = new StringBuilder("class Root { constructor() {\n");
        for (int index = 0; index < 8193; index++) { source.Append("this.touch();\n"); }
        source.Append("}; private func touch() {} }");
        DeferredRoot(source.ToString());
    }
}
