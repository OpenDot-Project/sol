// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalDiagnosticTests
{
    [Fact]
    public void DiagnosticsRetainTheOriginalLocalBodySourceSpan()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("var captured: T\n"
            + "func <U> local(item: U): T = captured\nlocal<int>(1)");
        var local = GenericLexicalTestSource.Local(root);
        var read = Assert.Single(GenericLexicalTestSource.Reads(local, "captured"));
        var diagnostic = Assert.Single(local.SpecializedBody!.Diagnostics, item => item.Code == "SOL2240");
        Assert.Same(read.Expression.Origin.Source, diagnostic.Source);
        Assert.Equal(read.Expression.Origin.Span, diagnostic.Span);
        Assert.Contains(diagnostic, analysis.Diagnostics);
        Assert.NotEqual(root.Binding.Call.Origin.Span, diagnostic.Span);
    }

    [Fact]
    public void SharedInvalidInstanceDoesNotDuplicateItsDiagnosticPerCallSite()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("var captured: T\n"
            + "func <U> local(item: U): T = captured\nlocal<int>(1)\nlocal<int>(2)");
        Assert.Equal(BodyBindingStatus.Invalid, root.Status);
        Assert.Equal(2, root.Dependencies.Length);
        var first = root.Dependencies[0];
        var second = root.Dependencies[1];
        Assert.Same(first.SpecializedBody, second.SpecializedBody);
        var diagnostic = Assert.Single(first.SpecializedBody!.Diagnostics, item => item.Code == "SOL2240");
        Assert.Single(analysis.Diagnostics, item => item == diagnostic);
        Assert.NotSame(first.Binding.Call, second.Binding.Call);
    }

    [Fact]
    public void AnInvalidLocalDoesNotChangeAnUnrelatedCallerSummary()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> { func run(value: T): T {\n"
            + "var captured: T\nfunc <U> local(item: U): T = captured\nlocal<int>(1)\n}\n}\n"
            + "func bad(owner: Root<int>, value: int): int = owner.run(value)\n"
            + "func good(value: int): int = value");
        var bad = Assert.Single(analysis.Bodies, body => body.Declaration.Name == "bad");
        var good = Assert.Single(analysis.Bodies, body => body.Declaration.Name == "good");
        Assert.Equal(BodyBindingStatus.Invalid, bad.Status);
        Assert.Equal(BodyBindingStatus.Bound, good.Status);
        Assert.Same(good, analysis.GetBody(good.Declaration));
        Assert.DoesNotContain(analysis.UnverifiedNodes, node =>
            good.Declaration.Origin.Span.Start <= node.Origin.Span.Start
            && node.Origin.Span.End <= good.Declaration.Origin.Span.End);
    }

    [Fact]
    public void ExpressionAndBodyInventoriesAgreeWithTheirLookupMethodsAfterFailure()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("var captured: T\n"
            + "func <U> local(item: U): T = captured\nlocal<int>(1)");
        Assert.Equal(BodyBindingStatus.Invalid, root.Status);
        Assert.All(analysis.Expressions, expression => Assert.Same(expression, analysis.GetExpression(expression.Expression)));
        Assert.All(analysis.Bodies, body => Assert.Same(body, analysis.GetBody(body.Declaration)));
        Assert.Equal(analysis.UnverifiedNodes.OrderBy(node => node.Origin.Source.Path,
            System.StringComparer.Ordinal).ThenBy(node => node.Origin.Span.Start), analysis.UnverifiedNodes);
        var local = GenericLexicalTestSource.Local(root);
        Assert.All(local.SpecializedBody!.Expressions, expression =>
            Assert.Same(expression, local.SpecializedBody.GetExpression(expression.Expression)));
    }

    [Fact]
    public void InvalidStatusDominatesASeparateDeferredDependency()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("var captured: T\n"
            + "func <U> local(item: U): T { missing<U>(item); captured }\nlocal<int>(1)",
            "func <V> missing(value: V): int\n");
        Assert.Equal(BodyBindingStatus.Invalid, root.Status);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal(BodyBindingStatus.Invalid, local.Status);
        Assert.Equal(BodyBindingStatus.Deferred, Assert.Single(local.Dependencies).Status);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2240");
    }
}
