// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalLifetimeTests
{
    [Fact]
    public void DirectLocalCallCanReadANonescapingRefCapture()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> { func run(ref value: T): T {\n"
            + "func <U> local(item: U): T = value\nlocal<int>(1)\n}\n}\n"
            + "func use(owner: Root<int>, ref value: int): int = owner.run(ref value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.True(GenericLexicalTestSource.Fact(local, "value").Assigned);
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
        Assert.DoesNotContain(local.SpecializedBody.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
    }

    [Fact]
    public void AnUnassignedOutCaptureCannotBeReadByADirectLocal()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> { func run(out value: T, initial: T): T {\n"
            + "func <U> local(item: U): T = value\nlocal<int>(1)\nvalue = initial\ninitial\n}\n}\n"
            + "func use(owner: Root<int>, out value: int, initial: int): int = owner.run(out value, initial)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        GenericLexicalTestSource.Invalid(analysis, root, "SOL2240");
        var local = GenericLexicalTestSource.Local(root);
        Assert.False(GenericLexicalTestSource.Fact(local, "value").Assigned);
        Assert.Contains(local.SpecializedBody!.Diagnostics, diagnostic => diagnostic.Code == "SOL2240");
    }

    [Fact]
    public void AssignmentOfTheOutCaptureBeforeCallIsRetained()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> { func run(out value: T, initial: T): T {\n"
            + "func <U> local(item: U): T = value\nvalue = initial\nlocal<int>(1)\n}\n}\n"
            + "func use(owner: Root<int>, out value: int, initial: int): int = owner.run(out value, initial)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.True(GenericLexicalTestSource.Fact(local, "value").Assigned);
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
    }

    [Fact]
    public void AnEscapingLocalCannotUseItsDirectCallToHideARefCapture()
    {
        var analysis = GenericMemberProofTestSource.Analyze("func accept(@escaping callback: (int) -> int) {}\n"
            + "class Root<T> { func run(ref value: int): int {\n"
            + "func <U> local(item: U): int = value\n"
            + "accept(local)\nlocal<int>(1)\n}\n}\n"
            + "func use(owner: Root<int>, ref value: int): int = owner.run(ref value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        GenericLexicalTestSource.Invalid(analysis, root, "SOL2262");
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal(BodyBindingStatus.Invalid, local.SpecializedBody!.Summary.Status);
        Assert.Contains(local.SpecializedBody.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
    }

    [Fact]
    public void UnknownCallableEscapeDoesNotCertifyARefLikeReceiver()
    {
        var analysis = GenericMemberProofTestSource.Analyze("func accept(@escaping callback: (int) -> int) {}\n"
            + "ref struct Root<T> { func identity(): int = 1\nfunc run(value: T): int {\n"
            + "func <U> local(item: U): int = this.identity()\naccept(local)\nlocal<int>(1)\n}\n}\n"
            + "func use(owner: Root<int>, value: int): int = owner.run(value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Invalid, root.Status);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Contains(local.SpecializedBody!.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
    }
}
