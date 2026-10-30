// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalBodyProofTests
{
    [Fact]
    public void LocalBodyRetainsOwnerAndEnclosingMethodArguments()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + "func <V> run(value: T, other: V): T {\n"
            + "func <U> local(item: U): T = value\nlocal<V>(other)\n}\n}\n"
            + "func use(owner: Root<int>, value: int): int = owner.run<String>(value, \"text\")");
        var root = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var proof = analysis.GetGenericMemberCallProof(root.Call)!;
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        var local = Assert.Single(proof.Dependencies);
        Assert.Equal("local", local.Binding.Target.Name);
        Assert.Equal("std.core.String", Assert.Single(local.Binding.TypeArguments).Name);
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
        var context = local.Binding.LexicalContext!;
        Assert.Equal(new[] { "int", "std.core.String" }, context.EnclosingArguments.Select(pair => pair.Argument.Name));
        Assert.All(context.Locals, fact => Assert.True(fact.Assigned));
    }

    [Fact]
    public void ClosedCapturedParameterCanChangeTheSelectedOverload()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Widget: Object {}\n"
            + "func choose(value: Object): int = 1\n"
            + "func choose(value: Widget): String = \"closed\"\n"
            + "class Root<T: Object> { func run(value: T): int {\n"
            + "func <U> local(item: U): int = choose(value)\nlocal<int>(1)\n}\n}\n"
            + "func use(owner: Root<Widget>, value: Widget): int = owner.run(value)");
        var root = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var proof = analysis.GetGenericMemberCallProof(root.Call)!;
        Assert.Equal(BodyBindingStatus.Invalid, proof.Status);
        var local = Assert.Single(proof.Dependencies);
        Assert.Equal(BodyBindingStatus.Invalid, local.Status);
        Assert.Equal("std.core.String", Assert.Single(local.SpecializedBody!.Calls).ReturnType!.Name);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
    }

    [Fact]
    public void FiniteLocalRecursionUsesOneLexicalInstance()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> { func run(value: T): T {\n"
            + "func <U> local(item: U): T = local<U>(item)\nlocal<int>(1)\n}\n}\n"
            + "func use(owner: Root<int>, value: int): int = owner.run(value)");
        var root = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var proof = analysis.GetGenericMemberCallProof(root.Call)!;
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        var local = Assert.Single(proof.Dependencies);
        Assert.True(Assert.Single(local.Dependencies).IsRecursiveReference);
    }

    [Fact]
    public void NongenericLocalBodyStillNeedsItsEnclosingOwnerSubstitution()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> { func run(value: T): T {\n"
            + "func local(): T = value\nlocal()\n}\n}\n"
            + "func use(owner: Root<int>, value: int): int = owner.run(value)");
        var root = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var proof = analysis.GetGenericMemberCallProof(root.Call)!;
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        var local = Assert.Single(proof.Dependencies);
        Assert.Empty(local.Binding.TypeArguments);
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
    }

    [Fact]
    public void DistinctClosedOwnersCannotShareALexicalBody()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> { func run(value: T): T {\n"
            + "func <U> local(item: U): T = value\nlocal<int>(1)\n}\n}\n"
            + "func first(owner: Root<int>, value: int): int = owner.run(value)\n"
            + "func second(owner: Root<String>, value: String): String = owner.run(value)");
        var calls = analysis.Calls.Where(binding => binding.Target.Name == "run" && binding.Receiver is { } receiver
            && receiver.Arguments.All(GenericMemberProofTestSource.IsClosed)).ToArray();
        Assert.Equal(2, calls.Length);
        var first = Assert.Single(analysis.GetGenericMemberCallProof(calls[0].Call)!.Dependencies);
        var second = Assert.Single(analysis.GetGenericMemberCallProof(calls[1].Call)!.Dependencies);
        Assert.NotSame(first.SpecializedBody, second.SpecializedBody);
        Assert.NotEqual(first.Binding.LexicalContext!.Identity, second.Binding.LexicalContext!.Identity);
        Assert.Equal(new[] { "int", "std.core.String" }, new[] { first, second }
            .Select(proof => proof.SpecializedBody!.Summary.ReturnType!.Name));
    }
}
