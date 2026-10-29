// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericMemberProofPropagationTests
{
    private const string Missing = "interface Root<T> { func echo(value: T): T }\n";

    [Fact]
    public void IncompleteNestedArgumentInvalidatesContainingCallExpression()
    {
        var analysis = GenericMemberProofTestSource.Analyze(Missing
            + "func consume(value: int): int = value\n"
            + "func use(owner: Root<int>): int = consume(owner.echo(1))");
        var member = Assert.Single(analysis.Calls, call => call.Target.Name == "echo");
        var outer = Assert.Single(analysis.Calls, call => call.Target.Name == "consume");
        Assert.Equal(BodyBindingStatus.Deferred, analysis.GetExpression(member.Call)!.Status);
        Assert.Equal(BodyBindingStatus.Deferred, analysis.GetExpression(outer.Call)!.Status);
        Assert.Equal(BodyBindingStatus.Deferred,
            Assert.Single(analysis.Bodies, body => body.Declaration.Name == "use").Status);
        Assert.Contains(member.Call, analysis.UnverifiedNodes);
        Assert.False(analysis.HasErrors);
    }

    [Fact]
    public void UnrelatedSiblingBodyRetainsItsCompleteStatus()
    {
        var analysis = GenericMemberProofTestSource.Analyze(Missing
            + "func pending(owner: Root<int>): int = owner.echo(1)\n"
            + "func sibling(value: int): int = value + 1");
        Assert.Equal(BodyBindingStatus.Deferred,
            Assert.Single(analysis.Bodies, body => body.Declaration.Name == "pending").Status);
        Assert.Equal(BodyBindingStatus.Bound,
            Assert.Single(analysis.Bodies, body => body.Declaration.Name == "sibling").Status);
        Assert.False(analysis.IsComplete);
        Assert.False(analysis.HasErrors);
    }

    [Fact]
    public void AnonymousFunctionAndItsEnclosingBodyShareTheProofGap()
    {
        var analysis = GenericMemberProofTestSource.Analyze(Missing
            + "func use(owner: Root<int>): () -> int = func(): int { owner.echo(1) }");
        var summary = Assert.Single(analysis.AnonymousFunctionBodies);
        Assert.False(summary.IsComplete);
        Assert.Equal(BodyBindingStatus.Deferred,
            Assert.Single(analysis.Bodies, body => body.Declaration.Name == "use").Status);
        var call = Assert.Single(analysis.Calls);
        Assert.Equal(BodyBindingStatus.Deferred, analysis.GetExpression(call.Call)!.Status);
    }

    [Fact]
    public void NestedLambdaGapReachesEveryEnclosingLambdaSummary()
    {
        var analysis = GenericMemberProofTestSource.Analyze(Missing
            + "func use(owner: Root<int>): () -> (() -> int) = { { owner.echo(1) } }");
        Assert.Equal(2, analysis.LambdaBodies.Length);
        Assert.All(analysis.LambdaBodies, summary => Assert.False(summary.IsComplete));
        Assert.All(analysis.LambdaBodies, summary =>
            Assert.Same(summary, analysis.GetLambdaBody(summary.Lambda)));
    }

    [Fact]
    public void PublicArraysAndLookupMethodsExposeTheSameUpdatedFacts()
    {
        var analysis = GenericMemberProofTestSource.Analyze(Missing
            + "func use(owner: Root<int>): int = owner.echo(1)");
        Assert.All(analysis.Expressions, expression =>
            Assert.Same(expression, analysis.GetExpression(expression.Expression)));
        Assert.All(analysis.Bodies, body => Assert.Same(body, analysis.GetBody(body.Declaration)));
        var call = Assert.Single(analysis.Calls);
        Assert.Equal(BodyBindingStatus.Deferred, analysis.GetExpression(call.Call)!.Status);
    }

    [Fact]
    public void InvalidProofDominatesADeferredSiblingCall()
    {
        var analysis = GenericMemberProofTestSource.Analyze(Missing
            + "class Widget: Object {}\n"
            + "class Invalid<T: Object> {\n"
            + "func choose(value: Object): int = 1\n"
            + "func choose(value: Widget): String = \"changed\"\n"
            + "func run(value: T): int = choose(value)\n}\n"
            + "func use(owner: Root<int>, other: Invalid<Widget>, value: Widget): int {\n"
            + " owner.echo(1); other.run(value)\n}");
        var missing = Assert.Single(analysis.Calls, call => call.Target.Name == "echo");
        var invalid = Assert.Single(analysis.Calls, call => call.Target.Name == "run");
        Assert.Equal(BodyBindingStatus.Deferred, analysis.GetGenericMemberCallProof(missing.Call)!.Status);
        Assert.Equal(BodyBindingStatus.Invalid, analysis.GetGenericMemberCallProof(invalid.Call)!.Status);
        var caller = Assert.Single(analysis.Bodies, body => body.Declaration.Name == "use");
        Assert.Equal(BodyBindingStatus.Invalid, caller.Status);
        Assert.True(analysis.HasErrors);
        Assert.Contains(missing.Call, analysis.UnverifiedNodes);
        Assert.Contains(invalid.Call, analysis.UnverifiedNodes);
    }

    [Fact]
    public void ProofQueriesDoNotMutatePublishedCollections()
    {
        var analysis = GenericMemberProofTestSource.Analyze(Missing
            + "func use(owner: Root<int>): int = owner.echo(1)");
        var diagnostics = analysis.Diagnostics;
        var expressions = analysis.Expressions;
        var bodies = analysis.Bodies;
        var unverified = analysis.UnverifiedNodes;
        var call = Assert.Single(analysis.Calls);
        for (int index = 0; index < 25; index++)
        { Assert.Equal(BodyBindingStatus.Deferred, analysis.GetGenericMemberCallProof(call.Call)!.Status); }
        Assert.Equal(diagnostics, analysis.Diagnostics);
        Assert.Equal(expressions, analysis.Expressions);
        Assert.Equal(bodies, analysis.Bodies);
        Assert.Equal(unverified, analysis.UnverifiedNodes);
    }
}
