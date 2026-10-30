// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericCallableBridgeProofTests
{
    private const string InvalidFree = "class Widget: Object {}\n"
        + "func choose(value: Object): int = 1\n"
        + "func choose(value: Widget): String = \"closed\"\n"
        + "func <U: Object> free(value: U): int = choose(value)\n";

    [Fact]
    public void InvalidFreeSpecializationInvalidatesItsGenericMemberCaller()
    {
        var analysis = GenericMemberProofTestSource.Analyze(InvalidFree
            + "class Root<T: Object> { func run(value: T): int = free<T>(value) }\n"
            + "func use(owner: Root<Widget>, value: Widget): int = owner.run(value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Invalid, proof.Status);
        var free = Assert.Single(proof.Dependencies);
        Assert.Equal("free", free.Binding.Target.Name);
        Assert.Null(free.Projection);
        Assert.Equal(BodyBindingStatus.Invalid, free.SpecializedBody!.Summary.Status);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
        Assert.Equal(BodyBindingStatus.Invalid,
            Assert.Single(analysis.Bodies, body => body.Declaration.Name == "use").Status);
    }

    [Fact]
    public void FiniteRecursionCanCrossFreeFunctionAndMemberBoundaries()
    {
        var analysis = GenericMemberProofTestSource.Analyze("func <U> bridge(owner: Root<U>, value: U): U = owner.second(value)\n"
            + "class Root<T> {\n"
            + "func first(value: T): T = bridge<T>(this, value)\n"
            + "func second(value: T): T = first(value)\n}\n"
            + "func use(owner: Root<int>, value: int): int = owner.first(value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "first");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        var bridge = Assert.Single(proof.Dependencies);
        Assert.Equal("bridge", bridge.Binding.Target.Name);
        Assert.Null(bridge.Projection);
        Assert.NotNull(bridge.SpecializedBody);
        var second = Assert.Single(bridge.Dependencies);
        Assert.Equal("second", second.Binding.Target.Name);
        Assert.True(Assert.Single(second.Dependencies).IsRecursiveReference);
        Assert.All(GenericMemberProofTestSource.Walk(proof), item => Assert.Equal(BodyBindingStatus.Bound, item.Status));
    }

    [Fact]
    public void TransitiveFreeFailuresCannotPoisonOrCertifyTheLocalBodyCache()
    {
        var analysis = GenericMemberProofTestSource.Analyze(InvalidFree
            + "func <V: Object> outer(value: V): int = free<V>(value)\n"
            + "class Root<T: Object> { func run(value: T): int = outer<T>(value) }\n"
            + "func use(owner: Root<Widget>, value: Widget): int = owner.run(value)");
        var root = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var proof = Assert.Single(analysis.GetGenericMemberCallProof(root.Call)!.Dependencies);
        Assert.Equal(BodyBindingStatus.Invalid, proof.Status);
        Assert.Equal(BodyBindingStatus.Bound, proof.SpecializedBody!.Summary.Status);
        var target = proof.SpecializedBody.Declaration;
        var first = analysis.GetGenericBody(target, proof.Binding.TypeArguments, TestContext.Current.CancellationToken);
        var second = analysis.GetGenericBody(target, proof.Binding.TypeArguments, TestContext.Current.CancellationToken);
        Assert.NotSame(first, second);
        Assert.NotSame(proof.SpecializedBody, first);
        Assert.Equal(BodyBindingStatus.Invalid, first.Summary.Status);
        Assert.Equal(BodyBindingStatus.Bound, proof.SpecializedBody.Summary.Status);
    }

    [Fact]
    public void LocalGenericMemberFunctionsObtainClosedLexicalBodyProofs()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + "func run(value: T): T { func <U> local(item: U): U = item; local<T>(value) }\n}\n"
            + "func use(owner: Root<int>, value: int): int = owner.run(value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        var local = Assert.Single(proof.Dependencies);
        Assert.Equal("local", local.Binding.Target.Name);
        Assert.Equal(BodyBindingStatus.Bound, local.Status);
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
        Assert.False(analysis.HasErrors);
    }
}
