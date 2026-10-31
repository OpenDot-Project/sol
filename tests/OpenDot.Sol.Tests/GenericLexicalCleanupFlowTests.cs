// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalCleanupFlowTests
{
    [Fact]
    public void LocalCallInDeferUsesTheAssignmentAtScopeExit()
    {
        var (_, root) = GenericLexicalTestSource.Member("var captured: T\n"
            + "func <U> local(item: U): T = captured\n"
            + "defer { local<int>(1) }\ncaptured = value\nvalue");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.True(GenericLexicalTestSource.Fact(local, "captured").Assigned);
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
    }

    [Fact]
    public void LocalCallInFinallyUsesTheAssignmentBeforeTheTryExits()
    {
        var (_, root) = GenericLexicalTestSource.Member("var captured: T\n"
            + "func <U> local(item: U): T = captured\n"
            + "try { captured = value } finally { local<int>(1) }\nvalue");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.True(GenericLexicalTestSource.Fact(local, "captured").Assigned);
        Assert.Empty(local.SpecializedBody!.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinallyJoinsCapturedAssignmentAcrossAllReturnPaths(bool assignedPathFirst)
    {
        string assigned = "captured = value; return value";
        string unassigned = "return value";
        string first = assignedPathFirst ? assigned : unassigned;
        string second = assignedPathFirst ? unassigned : assigned;
        var (analysis, root) = GenericLexicalTestSource.Member("var captured: T\n"
            + "func <U> local(item: U): T = captured\n"
            + "try { if true { " + first + " } else { " + second + " } }\n"
            + "finally { local<int>(1) }");
        Assert.NotEqual(BodyBindingStatus.Bound, root.Status);
        var local = GenericLexicalTestSource.Local(root);
        Assert.False(GenericLexicalTestSource.Fact(local, "captured").Assigned);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2240");
        Assert.Equal(BodyBindingStatus.Invalid, local.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferJoinsCapturedAssignmentAcrossAllReturnPaths(bool assignedPathFirst)
    {
        string assigned = "captured = value; return value";
        string unassigned = "return value";
        string first = assignedPathFirst ? assigned : unassigned;
        string second = assignedPathFirst ? unassigned : assigned;
        var (analysis, root) = GenericLexicalTestSource.Member("var captured: T\n"
            + "func <U> local(item: U): T = captured\n"
            + "defer { local<int>(1) }\n"
            + "if true { " + first + " } else { " + second + " }");
        Assert.NotEqual(BodyBindingStatus.Bound, root.Status);
        var local = GenericLexicalTestSource.Local(root);
        Assert.False(GenericLexicalTestSource.Fact(local, "captured").Assigned);
        Assert.Equal(BodyBindingStatus.Invalid, local.Status);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2240");
    }

    [Fact]
    public void AssignmentInEveryExitLetsTheFinallyCallBind()
    {
        var (_, root) = GenericLexicalTestSource.Member("var captured: T\n"
            + "func <U> local(item: U): T = captured\n"
            + "try { if true { captured = value; return value } "
            + "else { captured = value; return value } } finally { local<int>(1) }");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.True(GenericLexicalTestSource.Fact(local, "captured").Assigned);
        Assert.Single(root.Dependencies);
    }

    [Fact]
    public void ALoopBodyAssignmentDoesNotCertifyTheCaptureAfterTheLoop()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("var captured: T\n"
            + "func <U> local(item: U): T = captured\n"
            + "for value == value { captured = value; break }\nlocal<int>(1)");
        GenericLexicalTestSource.Invalid(analysis, root, "SOL2240");
        Assert.False(GenericLexicalTestSource.Fact(GenericLexicalTestSource.Local(root), "captured").Assigned);
    }
}
