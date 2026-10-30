// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalCaptureFlowTests
{
    [Fact]
    public void InitializedImmutableLocalIsAssignedAtTheCall()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("val captured = value\n"
            + "func <U> local(item: U): T = captured\nlocal<int>(1)");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var fact = GenericLexicalTestSource.Fact(local, "captured");
        Assert.True(fact.Assigned);
        Assert.Equal("int", fact.Type!.Name);
        Assert.All(GenericLexicalTestSource.Reads(local, "captured"), read =>
        {
            Assert.Equal("int", read.Type!.Name);
            Assert.Equal(BodyBindingStatus.Bound, read.Status);
        });
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2240");
    }

    [Fact]
    public void UnassignedMutableCaptureCannotBecomeACompleteProof()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("var captured: T\n"
            + "func <U> local(item: U): T = captured\nlocal<int>(1)");
        GenericLexicalTestSource.Invalid(analysis, root, "SOL2240");
        var local = GenericLexicalTestSource.Local(root);
        Assert.False(GenericLexicalTestSource.Fact(local, "captured").Assigned);
        Assert.Equal(BodyBindingStatus.Invalid, local.SpecializedBody!.Summary.Status);
        var diagnostic = Assert.Single(local.SpecializedBody.Diagnostics,
            item => item.Code == "SOL2240");
        var read = Assert.Single(GenericLexicalTestSource.Reads(local, "captured"));
        Assert.Equal(read.Expression.Origin.Span, diagnostic.Span);
    }

    [Fact]
    public void AssignmentBeforeCallIsKeptForAMutableCapture()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("var captured: T\n"
            + "func <U> local(item: U): T = captured\ncaptured = value\nlocal<int>(1)");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var fact = GenericLexicalTestSource.Fact(local, "captured");
        Assert.True(fact.Assigned);
        Assert.True(fact.Local.IsMutable);
        Assert.Null(fact.Narrowed);
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2240");
    }

    [Fact]
    public void BothBranchAssignmentsReachTheLocalCall()
    {
        var (_, root) = GenericLexicalTestSource.Member("var captured: T\n"
            + "if true { captured = value } else { captured = value }\n"
            + "func <U> local(item: U): T = captured\nlocal<int>(1)");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.True(GenericLexicalTestSource.Fact(local, "captured").Assigned);
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
    }

    [Fact]
    public void OneBranchAssignmentDoesNotProveTheCaptureAssigned()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("var captured: T\n"
            + "if true { captured = value }\n"
            + "func <U> local(item: U): T = captured\nlocal<int>(1)");
        GenericLexicalTestSource.Invalid(analysis, root, "SOL2240");
        var local = GenericLexicalTestSource.Local(root);
        Assert.False(GenericLexicalTestSource.Fact(local, "captured").Assigned);
        Assert.Equal(BodyBindingStatus.Invalid, local.Status);
    }

    [Fact]
    public void AnOutArgumentInvalidatesCaptureAssignmentBeforeInvocation()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("var captured: T = value\n"
            + "func <U> local(out item: U): T { item = 1; captured }\nlocal<int>(out captured)");
        GenericLexicalTestSource.Invalid(analysis, root, "SOL2240");
        var local = GenericLexicalTestSource.Local(root);
        Assert.False(GenericLexicalTestSource.Fact(local, "captured").Assigned);
        Assert.Contains(local.SpecializedBody!.Diagnostics, diagnostic => diagnostic.Code == "SOL2240");
    }

    [Fact]
    public void SuccessfulOutCallMakesTheCaptureAvailableToAFollowingLocalCall()
    {
        var (_, root) = GenericLexicalTestSource.Member("var captured: int\nfill(out captured)\n"
            + "func <U> local(item: U): int = captured\nlocal<T>(value)",
            "func fill(out target: int) { target = 42 }", result: "int");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.True(GenericLexicalTestSource.Fact(local, "captured").Assigned);
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
    }

    [Fact]
    public void AssignmentAfterTheFirstCallCannotRetroactivelyCertifyIt()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("var captured: T\n"
            + "func <U> local(item: U): T = captured\n"
            + "local<int>(1)\ncaptured = value\nlocal<int>(2)");
        GenericLexicalTestSource.Invalid(analysis, root, "SOL2240");
        var calls = root.Dependencies.Where(proof => proof.Binding.Target.Name == "local").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal(BodyBindingStatus.Invalid, calls[0].Status);
        Assert.Equal(BodyBindingStatus.Bound, calls[1].Status);
        Assert.False(GenericLexicalTestSource.Fact(calls[0], "captured").Assigned);
        Assert.True(GenericLexicalTestSource.Fact(calls[1], "captured").Assigned);
        Assert.NotSame(calls[0].SpecializedBody, calls[1].SpecializedBody);
    }

    [Fact]
    public void ParameterlessLocalCanReadAClosedInferredArrayCapture()
    {
        var (_, root) = GenericLexicalTestSource.Member("val captured = [value]\n"
            + "func local(): []T = captured\nlocal()[0]");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var fact = GenericLexicalTestSource.Fact(local, "captured");
        Assert.Equal(ResolvedTypeKind.Array, fact.Type!.Kind);
        Assert.Equal("int", Assert.Single(fact.Type.Arguments).Name);
        Assert.Same(fact.Type, local.SpecializedBody!.Summary.ReturnType);
    }
}
