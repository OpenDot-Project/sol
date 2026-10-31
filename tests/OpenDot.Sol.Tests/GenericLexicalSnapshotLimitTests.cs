// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Text;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalSnapshotLimitTests
{
    [Theory]
    [InlineData(128, BodyBindingStatus.Bound)]
    [InlineData(129, BodyBindingStatus.Deferred)]
    public void SourceEnclosingArgumentBudgetHasAnExactBoundary(int count, BodyBindingStatus expected)
    {
        var parameters = string.Join(", ", Enumerable.Range(0, count).Select(index => "T" + index));
        var arguments = string.Join(", ", Enumerable.Repeat("int", count));
        var analysis = GenericMemberProofTestSource.Analyze("class Root<" + parameters + "> {\n"
            + "func run(value: T0): T0 { func <U> local(item: U): T0 = value; local<int>(1) }\n}\n"
            + "func use(owner: Root<" + arguments + ">, value: int): int = owner.run(value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(expected, root.Status);
        var local = Assert.Single(root.Dependencies);
        var context = local.Binding.LexicalContext!;
        if (expected == BodyBindingStatus.Bound)
        {
            Assert.Equal(count, context.EnclosingArguments.Length);
            Assert.Null(context.UnavailableReason);
            Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
        }
        else
        {
            Assert.Empty(context.EnclosingArguments);
            Assert.Empty(context.Locals);
            Assert.Contains("argument budget", context.UnavailableReason!);
            Assert.Null(local.SpecializedBody);
        }
    }

    [Theory]
    [InlineData(4096, BodyBindingStatus.Bound)]
    [InlineData(4097, BodyBindingStatus.Deferred)]
    public void SourceLocalInventoryBudgetHasAnExactBoundary(int count, BodyBindingStatus expected)
    {
        // The outer value parameter is also part of the lexical state inventory.
        var source = new StringBuilder();
        for (int index = 1; index < count; index++)
        { source.Append("val unused").Append(index).Append(" = 1\n"); }
        source.Append("func <U> local(item: U): T = value\nlocal<int>(1)");
        var (_, root) = GenericLexicalTestSource.Member(source.ToString());
        Assert.Equal(expected, root.Status);
        var local = Assert.Single(root.Dependencies);
        var context = local.Binding.LexicalContext!;
        if (expected == BodyBindingStatus.Bound)
        {
            Assert.Equal(count, context.Locals.Length);
            Assert.All(context.Locals, fact => Assert.True(fact.Assigned));
            Assert.Null(context.UnavailableReason);
        }
        else
        {
            Assert.Empty(context.Locals);
            Assert.Empty(context.EnclosingArguments);
            Assert.Contains("local budget", context.UnavailableReason!);
            Assert.Null(local.SpecializedBody);
        }
    }

    [Fact]
    public void ContextLimitFailureDoesNotPublishAPartialInstance()
    {
        var parameters = string.Join(", ", Enumerable.Range(0, 129).Select(index => "T" + index));
        var arguments = string.Join(", ", Enumerable.Repeat("int", 129));
        var analysis = GenericMemberProofTestSource.Analyze("class Root<" + parameters + "> {\n"
            + "func run(value: T0): T0 { func <U> local(item: U): T0 = value; local<int>(1) }\n}\n"
            + "func use(owner: Root<" + arguments + ">, value: int): int = owner.run(value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Deferred, root.Status);
        var local = Assert.Single(root.Dependencies);
        Assert.Empty(local.Dependencies);
        Assert.Null(local.SpecializedBody);
        Assert.False(local.IsRecursiveReference);
        Assert.Equal(BodyBindingStatus.Deferred, analysis.GetExpression(call.Call)!.Status);
        Assert.Contains(call.Call, analysis.UnverifiedNodes);
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2402");
    }
}
