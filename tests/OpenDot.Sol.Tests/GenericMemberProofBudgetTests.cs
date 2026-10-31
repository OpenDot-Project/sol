// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using System.Text;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericMemberProofBudgetTests
{
    [Theory]
    [InlineData(127, BodyBindingStatus.Bound)]
    [InlineData(128, BodyBindingStatus.Deferred)]
    public void DistinctDependencyDepthHasAnExactBoundary(int edges, BodyBindingStatus expected)
    {
        var source = new StringBuilder("class Root<T> {\n");
        for (int index = 0; index <= edges; index++)
        {
            source.Append("func node").Append(index).Append("(value: T): T = ");
            if (index == edges) { source.Append("value\n"); }
            else { source.Append("node").Append(index + 1).Append("(value)\n"); }
        }
        source.Append("}\nfunc use(owner: Root<int>, value: int): int = owner.node0(value)");
        var analysis = GenericMemberProofTestSource.Analyze(source.ToString());
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "node0");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(expected, proof.Status);
        if (expected == BodyBindingStatus.Bound)
        {
            Assert.Equal(edges + 1, GenericMemberProofTestSource.Walk(proof).Length);
            Assert.DoesNotContain(call.Call, analysis.UnverifiedNodes);
        }
        else
        {
            Assert.Contains(GenericMemberProofTestSource.Walk(proof), item =>
                item.Reason?.Contains("budget", System.StringComparison.Ordinal) == true);
            Assert.Contains(call.Call, analysis.UnverifiedNodes);
        }
    }

    [Theory]
    [InlineData(1023, BodyBindingStatus.Bound)]
    [InlineData(1024, BodyBindingStatus.Deferred)]
    public void WideGraphsCountCanonicalInstancesInsteadOfEdges(int leaves, BodyBindingStatus expected)
    {
        var source = new StringBuilder("class Root<T> {\n");
        for (int index = 0; index < leaves; index++)
        { source.Append("func leaf").Append(index).Append("(value: T): T = value\n"); }
        source.Append("func root(value: T): T {\n");
        for (int index = 0; index < leaves; index++)
        { source.Append("leaf").Append(index).Append("(value);\n"); }
        source.Append("value\n}\n}\nfunc use(owner: Root<int>, value: int): int = owner.root(value)");
        var analysis = GenericMemberProofTestSource.Analyze(source.ToString());
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "root");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(expected, proof.Status);
        Assert.Equal(leaves, proof.Dependencies.Length);
        Assert.Equal(expected == BodyBindingStatus.Deferred,
            analysis.UnverifiedNodes.Contains(call.Call));
    }

    [Fact]
    public void TypeExpandingRecursionStopsWithoutPublishingCompleteProof()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + "func <U> grow(value: U): int = grow<[]U>([value])\n}\n"
            + "func use(owner: Root<long>): int = owner.grow<int>(1)");
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "grow"
            && Assert.Single(binding.TypeArguments).Name == "int");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Deferred, proof.Status);
        Assert.Contains(call.Call, analysis.UnverifiedNodes);
        Assert.False(analysis.IsComplete);
        Assert.Contains(GenericMemberProofTestSource.Walk(proof), item => item.Dependencies.IsEmpty
            && item.Status == BodyBindingStatus.Deferred);
    }

    [Theory]
    [InlineData(127, BodyBindingStatus.Bound)]
    [InlineData(128, BodyBindingStatus.Deferred)]
    public void SharedInstanceGraphsHaveAnIndependentDependencyEdgeBudget(int perFan, BodyBindingStatus expected)
    {
        var source = new StringBuilder();
        for (int index = 0; index < 64; index++)
        { source.Append("class Key").Append(index).Append(" {}\n"); }
        source.Append("class Root<T> {\nfunc tail(value: T): T = value\nfunc <U> fan(value: T): T {\n");
        for (int index = 0; index < perFan; index++) { source.Append("tail(value);\n"); }
        source.Append("value\n}\nfunc run(value: T): T {\n");
        for (int index = 0; index < 64; index++)
        { source.Append("fan<Key").Append(index).Append(">(value);\n"); }
        source.Append("value\n}\n}\nfunc use(owner: Root<int>, value: int): int = owner.run(value)");
        var analysis = GenericMemberProofTestSource.Analyze(source.ToString());
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(expected, proof.Status);
        Assert.Equal(64, proof.Dependencies.Length);
        if (expected == BodyBindingStatus.Deferred)
        {
            Assert.Contains(GenericMemberProofTestSource.Walk(proof), item =>
                item.Reason?.Contains("edge budget", System.StringComparison.Ordinal) == true);
        }
        else
        {
            Assert.Equal(8193, GenericMemberProofTestSource.Walk(proof).Length);
        }
    }

    [Fact]
    public void DistinctInferredResultsCannotBypassTheInferenceWorkBudget()
    {
        var source = new StringBuilder("class Root<T> { func echo(value: T) = value }\n");
        for (int index = 0; index < 1025; index++)
        {
            source.Append("class Key").Append(index).Append(" {}\n")
                .Append("func use").Append(index).Append("(owner: Root<Key").Append(index)
                .Append(">, value: Key").Append(index).Append("): Key").Append(index)
                .Append(" = owner.echo(value)\n");
        }
        var analysis = GenericMemberProofTestSource.Analyze(source.ToString());
        var calls = analysis.Calls.Where(call => call.Target.Name == "echo")
            .OrderBy(call => call.Call.Origin.Span.Start).ToArray();
        Assert.Equal(1025, calls.Length);
        Assert.All(calls.Take(1024), call => Assert.NotNull(call.ReturnType));
        Assert.Null(calls[^1].ReturnType);
        Assert.Equal(BodyBindingStatus.Deferred, analysis.GetExpression(calls[^1].Call)!.Status);
        Assert.False(analysis.IsComplete);
        Assert.False(analysis.HasErrors);
    }
}
