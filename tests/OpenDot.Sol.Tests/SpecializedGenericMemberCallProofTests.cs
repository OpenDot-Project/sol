// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class SpecializedGenericMemberCallProofTests
{
    [Fact]
    public void ClosingFreeFunctionAutomaticallyChecksItsMemberCalls()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> { func echo(value: T): T = value }\n"
            + "func <U> use(owner: Root<U>, value: U): U = owner.echo(value)");
        var body = GenericMemberProofTestSource.Specialize(analysis, "use", "int");
        var call = Assert.Single(body.Calls);
        var proof = body.GetGenericMemberCallProof(call.Call);
        Assert.NotNull(proof);
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        Assert.Equal("int", proof.Projection!.Body!.Summary.ReturnType!.Name);
        Assert.Equal(BodyBindingStatus.Bound, body.Summary.Status);
        Assert.Empty(body.UnverifiedNodes);
    }

    [Fact]
    public void MissingMemberBodyPreventsSuccessfulFreeBodyCaching()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface Root<T> { func echo(value: T): T }\n"
            + "func <U> use(owner: Root<U>, value: U): U = owner.echo(value)");
        var first = GenericMemberProofTestSource.Specialize(analysis, "use", "int");
        var second = GenericMemberProofTestSource.Specialize(analysis, "use", "int");
        Assert.Equal(BodyBindingStatus.Deferred, first.Summary.Status);
        Assert.NotSame(first, second);
        var call = Assert.Single(first.Calls);
        Assert.Equal(BodyBindingStatus.Deferred, first.GetExpression(call.Call)!.Status);
        Assert.Contains(call.Call, first.UnverifiedNodes);
        Assert.Equal(BodyBindingStatus.Deferred, first.GetGenericMemberCallProof(call.Call)!.Status);
    }

    [Fact]
    public void CompleteSpecializationsReuseTheirMemberProofs()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> { func echo(value: T): T = value }\n"
            + "func <U> use(owner: Root<U>, value: U): U = owner.echo(value)");
        var first = GenericMemberProofTestSource.Specialize(analysis, "use", "int");
        var second = GenericMemberProofTestSource.Specialize(analysis, "use", "int");
        Assert.Same(first, second);
        var call = Assert.Single(first.Calls);
        Assert.Same(first.GetGenericMemberCallProof(call.Call), second.GetGenericMemberCallProof(call.Call));
        var other = GenericMemberProofTestSource.Specialize(analysis, "use", "long");
        Assert.NotSame(first, other);
        Assert.Equal("long", Assert.Single(other.Calls).ReturnType!.Name);
    }

    [Fact]
    public void ForeignCallCannotQuerySpecializedProofs()
    {
        const string source = "class Root<T> { func echo(value: T): T = value }\n"
            + "func <U> use(owner: Root<U>, value: U): U = owner.echo(value)";
        var first = GenericMemberProofTestSource.Analyze(source);
        var second = GenericMemberProofTestSource.Analyze(source);
        var body = GenericMemberProofTestSource.Specialize(first, "use", "int");
        Assert.Throws<ArgumentException>(() => body.GetGenericMemberCallProof(Assert.Single(second.Calls).Call));
    }
}
