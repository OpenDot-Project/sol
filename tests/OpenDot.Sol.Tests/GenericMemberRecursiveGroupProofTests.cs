// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericMemberRecursiveGroupProofTests
{
    [Fact]
    public void MutuallyRecursiveAnnotatedMembersShareACompleteFiniteGroup()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + " func first(value: T): T = second(value)\n"
            + " func second(value: T): T = first(value)\n}\n"
            + "func use(owner: Root<int>, value: int): int = owner.first(value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "first");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        var second = Assert.Single(proof.Dependencies);
        Assert.Equal("second", second.Binding.Target.Name);
        Assert.Equal(BodyBindingStatus.Bound, second.Status);
        var cycle = Assert.Single(second.Dependencies);
        Assert.True(cycle.IsRecursiveReference);
        Assert.Equal("first", cycle.Binding.Target.Name);
        Assert.Equal(BodyBindingStatus.Bound, cycle.Status);
        Assert.DoesNotContain(call.Call, analysis.UnverifiedNodes);
    }

    [Fact]
    public void InvalidClosedBodyPropagatesAroundARecursiveGroup()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Widget: Object {}\n"
            + "class Root<T: Object> {\n"
            + " func choose(value: Object): int = 1\n"
            + " func choose(value: Widget): String = \"closed\"\n"
            + " func first(value: T): int = second(value)\n"
            + " func second(value: T): int { first(value); choose(value) }\n}\n"
            + "func use(owner: Root<Widget>, value: Widget): int = owner.first(value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "first");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Invalid, proof.Status);
        var members = GenericMemberProofTestSource.Walk(proof)
            .Where(item => item.Binding.Target.Name is "first" or "second").ToArray();
        Assert.All(members, item => Assert.Equal(BodyBindingStatus.Invalid, item.Status));
        Assert.True(analysis.HasErrors);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
        Assert.Equal(BodyBindingStatus.Invalid,
            Assert.Single(analysis.Bodies, body => body.Declaration.Name == "use").Status);
    }

    [Fact]
    public void MissingDependencyPropagatesAroundARecursiveGroup()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface Contract<T> { func absent(value: T): T }\n"
            + "class Root<T> {\n"
            + " func first(other: Contract<T>, value: T): T = second(other, value)\n"
            + " func second(other: Contract<T>, value: T): T { first(other, value); other.absent(value) }\n}\n"
            + "func use(owner: Root<int>, other: Contract<int>, value: int): int = owner.first(other, value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "first");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Deferred, proof.Status);
        Assert.Contains(GenericMemberProofTestSource.Walk(proof), item => item.Binding.Target.Name == "absent"
            && item.Status == BodyBindingStatus.Deferred && item.Projection is null);
        Assert.False(analysis.IsComplete);
        Assert.Contains(call.Call, analysis.UnverifiedNodes);
    }

    [Fact]
    public void RecursiveMethodArgumentIdentityRemainsSeparateFromOwnerArguments()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + " func <U> first(value: U): U = second<U>(value)\n"
            + " func <U> second(value: U): U = first<U>(value)\n}\n"
            + "func use(owner: Root<long>, value: int): int = owner.first<int>(value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "first");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        Assert.All(GenericMemberProofTestSource.Walk(proof), item =>
        {
            Assert.Equal("long", Assert.Single(item.Projection!.Body!.OwnerTypeArguments).Name);
            Assert.Equal("int", Assert.Single(item.Projection.Body.TypeArguments).Name);
        });
    }

    [Fact]
    public void SharedTailIsCheckedForBothRootsWithoutInventingACycle()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + " func tail(value: T): T = value\n"
            + " func left(value: T): T = tail(value)\n"
            + " func right(value: T): T = tail(value)\n}\n"
            + "func use(owner: Root<int>, value: int): int { owner.left(value); owner.right(value) }");
        foreach (string name in new[] { "left", "right" })
        {
            var call = GenericMemberProofTestSource.ClosedCall(analysis, name);
            var proof = analysis.GetGenericMemberCallProof(call.Call)!;
            Assert.Equal(BodyBindingStatus.Bound, proof.Status);
            Assert.Equal("tail", Assert.Single(proof.Dependencies).Binding.Target.Name);
            Assert.All(GenericMemberProofTestSource.Walk(proof), item => Assert.False(item.IsRecursiveReference));
        }
    }
}
