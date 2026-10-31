// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalBudgetTests
{
    [Theory]
    [InlineData(126, BodyBindingStatus.Bound)]
    [InlineData(127, BodyBindingStatus.Deferred)]
    public void LocalDependenciesConsumeTheSameDistinctDepthBudget(int edges, BodyBindingStatus expected)
    {
        var body = new StringBuilder("func <U> node0(item: U): T = value\n");
        for (int index = 1; index <= edges; index++)
        {
            body.Append("func <U> node").Append(index).Append("(item: U): T = node")
                .Append(index - 1).Append("<U>(item)\n");
        }
        body.Append("node").Append(edges).Append("<int>(1)");
        var (_, root) = GenericLexicalTestSource.Member(body.ToString());
        Assert.Equal(expected, root.Status);
        var all = GenericMemberProofTestSource.Walk(root);
        if (expected == BodyBindingStatus.Bound)
        {
            Assert.Equal(edges + 2, all.Length);
            Assert.All(all, proof => Assert.Equal(BodyBindingStatus.Bound, proof.Status));
        }
        else
        {
            Assert.Contains(all, proof => proof.SpecializedBody is null
                && proof.Reason!.Contains("budget", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TypeExpandingLocalRecursionCannotEvadeTheInstanceBounds()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> local(item: U): T = local<[]U>([item])\n"
            + "local<T>(value)");
        Assert.Equal(BodyBindingStatus.Deferred, root.Status);
        var all = GenericMemberProofTestSource.Walk(root);
        Assert.True(all.Length <= 129);
        Assert.DoesNotContain(all, proof => proof.IsRecursiveReference);
        Assert.Contains(all, proof => proof.Status == BodyBindingStatus.Deferred);
    }

    [Fact]
    public void RepeatedLocalCallSitesShareTheirInstanceButKeepTheirOrigins()
    {
        var body = new StringBuilder("func <U> local(item: U): T = value\n");
        for (int index = 0; index < 140; index++) { body.Append("local<int>(1)\n"); }
        body.Append("value");
        var (_, root) = GenericLexicalTestSource.Member(body.ToString());
        GenericLexicalTestSource.Bound(root);
        Assert.Equal(140, root.Dependencies.Length);
        var first = root.Dependencies[0];
        Assert.All(root.Dependencies, proof => Assert.Same(first.SpecializedBody, proof.SpecializedBody));
        Assert.Equal(140, root.Dependencies.Select(proof => proof.Binding.Call.Origin.Span.Start).Distinct().Count());
    }

    [Fact]
    public void ExplicitUnavailableContextCannotCreateACachedBody()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("func <U> local(item: U): T = value\nlocal<int>(1)");
        var local = GenericLexicalTestSource.Local(root);
        var context = local.Binding.LexicalContext!;
        var unavailable = context with { UnavailableReason = "Local callable lexical local budget exceeded." };
        Assert.Throws<ArgumentException>(() => analysis.GetLexicalBody((AstFunction)local.Binding.Target,
            local.Binding.TypeArguments, unavailable, TestContext.Current.CancellationToken));
        Assert.Same(local.SpecializedBody, analysis.GetLexicalBody((AstFunction)local.Binding.Target,
            local.Binding.TypeArguments, context, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OversizedLocalInventoryIsRejectedBeforeBinding()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("func <U> local(item: U): T = value\nlocal<int>(1)");
        var local = GenericLexicalTestSource.Local(root);
        var context = local.Binding.LexicalContext!;
        var oversized = context with { Locals = Enumerable.Repeat(context.Locals[0],
            LexicalBodyContext.MaximumLocals + 1).ToImmutableArray() };
        Assert.Throws<ArgumentException>(() => analysis.GetLexicalBody((AstFunction)local.Binding.Target,
            local.Binding.TypeArguments, oversized, TestContext.Current.CancellationToken));
        Assert.Equal(BodyBindingStatus.Bound, local.SpecializedBody!.Summary.Status);
    }

    [Fact]
    public void OversizedEnclosingArgumentVectorIsRejectedBeforeBinding()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("func <U> local(item: U): T = value\nlocal<int>(1)");
        var local = GenericLexicalTestSource.Local(root);
        var context = local.Binding.LexicalContext!;
        var oversized = context with { EnclosingArguments = Enumerable.Repeat(context.EnclosingArguments[0],
            LexicalBodyContext.MaximumEnclosingArguments + 1).ToImmutableArray() };
        Assert.Throws<ArgumentException>(() => analysis.GetLexicalBody((AstFunction)local.Binding.Target,
            local.Binding.TypeArguments, oversized, TestContext.Current.CancellationToken));
    }
}
