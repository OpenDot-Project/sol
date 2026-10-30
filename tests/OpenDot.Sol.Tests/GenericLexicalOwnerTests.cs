// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalOwnerTests
{
    [Theory]
    [InlineData("class")]
    [InlineData("struct")]
    [InlineData("ref struct")]
    [InlineData("interface")]
    public void ClosedOwnerKindsAllRetainTheLocalReceiverContext(string kind)
    {
        var analysis = GenericMemberProofTestSource.Analyze(kind + " Root<T> {\n"
            + "func run(value: T): T { func <U> local(item: U): T = value; local<int>(1) }\n}\n"
            + "func use(owner: Root<int>, value: int): int = owner.run(value)");
        var rootCall = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(rootCall.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal("int", Assert.Single(local.Binding.LexicalContext!.EnclosingArguments).Argument.Name);
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
        Assert.Null(local.Projection);
    }

    [Fact]
    public void InheritedMemberProjectionRetainsTheDeclaringOwnersLocalParameters()
    {
        var analysis = GenericMemberProofTestSource.Analyze("open class Root<T> {\n"
            + "func run(value: T): T { func <U> local(item: U): T = value; local<int>(1) }\n}\n"
            + "class Leaf<V>: Root<[]V> {}\n"
            + "func use(owner: Leaf<int>, value: []int): []int = owner.run(value)");
        var rootCall = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(rootCall.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var argument = Assert.Single(local.Binding.LexicalContext!.EnclosingArguments).Argument;
        Assert.Equal(ResolvedTypeKind.Array, argument.Kind);
        Assert.Equal("int", Assert.Single(argument.Arguments).Name);
        Assert.Same(argument, local.SpecializedBody!.Summary.ReturnType);
        Assert.NotSame(root.Binding.SourceReceiver, root.Binding.Receiver);
    }

    [Fact]
    public void PartialOwnersUseTheCanonicalParameterIdentity()
    {
        var analysis = GenericMemberProofTestSource.Analyze("partial class Root<T> {}\n"
            + "partial class Root<T> { func run(value: T): T {\n"
            + "func <U> local(item: U): T = value\nlocal<int>(1)\n}\n}\n"
            + "func use(owner: Root<int>, value: int): int = owner.run(value)");
        var rootCall = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(rootCall.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var argument = Assert.Single(local.Binding.LexicalContext!.EnclosingArguments);
        var owner = root.Projection!.Body!.ConstructedOwner!;
        Assert.Same(Assert.Single(owner.Arguments), argument.Argument);
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
    }

    [Fact]
    public void ExplicitThisInALocalBodyUsesTheClosedReceiver()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + "func identity(value: T): T = value\nfunc run(value: T): T {\n"
            + "func <U> local(item: U): T = this.identity(value)\nlocal<int>(1)\n}\n}\n"
            + "func use(owner: Root<int>, value: int): int = owner.run(value)");
        var rootCall = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(rootCall.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var member = Assert.Single(local.Dependencies);
        Assert.Equal("identity", member.Binding.Target.Name);
        Assert.Equal("int", Assert.Single(member.Binding.Receiver!.Arguments).Name);
        var receiver = Assert.Single(local.SpecializedBody!.Expressions, binding =>
            binding.Expression is AstAtom { Spelling: "this" });
        Assert.Same(member.Binding.Receiver, receiver.Type);
    }

    [Fact]
    public void ImplicitMemberAccessInALocalBodyKeepsTheClosedOwner()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + "func identity(value: T): T = value\nfunc run(value: T): T {\n"
            + "func <U> local(item: U): T = identity(value)\nlocal<int>(1)\n}\n}\n"
            + "func use(owner: Root<int>, value: int): int = owner.run(value)");
        var rootCall = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(rootCall.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var member = Assert.Single(local.Dependencies);
        Assert.Equal("identity", member.Binding.Target.Name);
        Assert.Equal("int", member.Binding.ReturnType!.Name);
        Assert.Same(root.Binding.Receiver, member.Binding.Receiver);
    }
}
