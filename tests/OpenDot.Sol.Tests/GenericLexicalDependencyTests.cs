// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalDependencyTests
{
    [Fact]
    public void LocalToFreeToMemberDependenciesShareTheFiniteGraph()
    {
        var analysis = GenericMemberProofTestSource.Analyze("func <V> bridge(owner: Root<V>, value: V): V = owner.finish(value)\n"
            + "class Root<T> {\nfunc run(value: T): T {\n"
            + "func <U> local(item: U): T = bridge<T>(this, value)\nlocal<int>(1)\n}\n"
            + "func finish(value: T): T = value\n}\n"
            + "func use(owner: Root<int>, value: int): int = owner.run(value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = Assert.Single(root.Dependencies);
        var free = Assert.Single(local.Dependencies);
        var finish = Assert.Single(free.Dependencies);
        Assert.Equal("local", local.Binding.Target.Name);
        Assert.Equal("bridge", free.Binding.Target.Name);
        Assert.Equal("finish", finish.Binding.Target.Name);
        Assert.NotNull(local.Binding.LexicalContext);
        Assert.Null(free.Binding.LexicalContext);
        Assert.NotNull(finish.Projection);
    }

    [Fact]
    public void InvalidFreeBodyPropagatesThroughALocalCaller()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("func <U> local(item: U): int = broken<int>(1)\n"
            + "local<T>(value)", "func <V> broken(value: V): int = \"wrong\"\n", result: "int");
        GenericLexicalTestSource.Invalid(analysis, root, "SOL2206");
        var local = GenericLexicalTestSource.Local(root);
        var broken = Assert.Single(local.Dependencies);
        Assert.Equal(BodyBindingStatus.Invalid, broken.Status);
        Assert.Equal(BodyBindingStatus.Bound, local.SpecializedBody!.Summary.Status);
        Assert.Equal(BodyBindingStatus.Invalid, local.Status);
    }

    [Fact]
    public void MissingGenericSourceBodyKeepsTheLocalCallerDeferred()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> local(item: U): int = missing<U>(item)\n"
            + "local<T>(value)", "func <V> missing(value: V): int\n", result: "int");
        Assert.Equal(BodyBindingStatus.Deferred, root.Status);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal(BodyBindingStatus.Deferred, local.Status);
        var missing = Assert.Single(local.Dependencies);
        Assert.Null(missing.SpecializedBody);
        Assert.Contains("source body", missing.Reason!);
    }

    [Fact]
    public void MutuallyRecursiveLocalFunctionsFormACompleteGroup()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> first(item: U): T {\n"
            + "func <V> second(other: V): T = first<V>(other)\nsecond<U>(item)\n}\nfirst<int>(1)");
        GenericLexicalTestSource.Bound(root);
        var first = GenericLexicalTestSource.Local(root, "first");
        var second = Assert.Single(first.Dependencies);
        Assert.Equal("second", second.Binding.Target.Name);
        var back = Assert.Single(second.Dependencies);
        Assert.Equal("first", back.Binding.Target.Name);
        Assert.True(back.IsRecursiveReference);
        Assert.Same(first.SpecializedBody, back.SpecializedBody);
    }

    [Fact]
    public void InvalidOutgoingEdgeInvalidatesTheEntireLocalCycle()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("func <U> first(item: U): T {\n"
            + "func <V> second(other: V): T { broken<V>(other); first<V>(other) }\nsecond<U>(item)\n}\nfirst<int>(1)",
            "func <W> broken(item: W): int = \"wrong\"\n");
        GenericLexicalTestSource.Invalid(analysis, root, "SOL2206");
        var all = GenericMemberProofTestSource.Walk(root);
        Assert.Contains(all, proof => proof.IsRecursiveReference);
        Assert.All(all, proof => Assert.Equal(BodyBindingStatus.Invalid, proof.Status));
        Assert.Contains(all, proof => proof.Binding.Target.Name == "broken");
    }

    [Fact]
    public void ADeferredOutgoingEdgeCannotBeHiddenByFiniteRecursion()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> first(item: U): T {\n"
            + "func <V> second(other: V): T { missing<V>(other); first<V>(other) }\nsecond<U>(item)\n}\nfirst<int>(1)",
            "func <W> missing(item: W): int\n");
        Assert.Equal(BodyBindingStatus.Deferred, root.Status);
        var all = GenericMemberProofTestSource.Walk(root);
        Assert.Contains(all, proof => proof.IsRecursiveReference);
        Assert.All(all, proof => Assert.Equal(BodyBindingStatus.Deferred, proof.Status));
    }

    [Fact]
    public void NestedLocalBodyRetainsBothEnclosingCallableVectors()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> middle(item: U): T {\n"
            + "func <V> local(other: V): T = value\nlocal<U>(item)\n}\nmiddle<int>(1)");
        GenericLexicalTestSource.Bound(root);
        var middle = GenericLexicalTestSource.Local(root, "middle");
        var local = Assert.Single(middle.Dependencies);
        Assert.Equal("local", local.Binding.Target.Name);
        Assert.Equal(new[] { "int", "int" }, local.Binding.LexicalContext!.EnclosingArguments
            .Select(pair => pair.Argument.Name));
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
        Assert.True(GenericLexicalTestSource.Fact(local, "item").Assigned);
    }

    [Fact]
    public void LocalCapturingAnotherLocalFunctionUsesItsOwnLexicalScope()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> helper(item: U): T = value\n"
            + "func <V> local(item: V): T = helper<V>(item)\nlocal<int>(1)");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var helper = Assert.Single(local.Dependencies);
        Assert.Equal("helper", helper.Binding.Target.Name);
        Assert.Single(helper.Binding.LexicalContext!.EnclosingArguments);
        Assert.DoesNotContain(helper.Binding.LexicalContext.Locals, fact => fact.Local.Name == "item");
        Assert.Equal("int", helper.SpecializedBody!.Summary.ReturnType!.Name);
    }

    [Fact]
    public void LocalDefinedInsideAClosedFreeBodyReceivesTheFreeArgument()
    {
        var analysis = GenericMemberProofTestSource.Analyze("func <V> outer(value: V): V {\n"
            + "func <U> local(item: U): V = value\nlocal<int>(1)\n}\n"
            + "class Root<T> { func run(value: T): T = outer<T>(value) }\n"
            + "func use(owner: Root<int>, value: int): int = owner.run(value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        GenericLexicalTestSource.Bound(root);
        var outer = Assert.Single(root.Dependencies);
        var local = Assert.Single(outer.Dependencies);
        Assert.Equal("outer", outer.Binding.Target.Name);
        Assert.Equal("int", Assert.Single(local.Binding.LexicalContext!.EnclosingArguments).Argument.Name);
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
    }
}
