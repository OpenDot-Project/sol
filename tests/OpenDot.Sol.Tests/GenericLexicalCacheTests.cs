// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalCacheTests
{
    [Fact]
    public void EquivalentLexicalContextsReuseTheCompletedLocalSnapshot()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("func <U> local(item: U): T = value\nlocal<int>(1)");
        var local = GenericLexicalTestSource.Local(root);
        var function = (AstFunction)local.Binding.Target;
        var context = local.Binding.LexicalContext!;
        var first = analysis.GetLexicalBody(function, local.Binding.TypeArguments, context,
            TestContext.Current.CancellationToken);
        var second = analysis.GetLexicalBody(function, local.Binding.TypeArguments,
            context with { Locals = context.Locals.ToImmutableArray() }, TestContext.Current.CancellationToken);
        Assert.Same(first, second);
        Assert.Same(local.SpecializedBody, first);
        Assert.Equal(BodyBindingStatus.Bound, first.Summary.Status);
    }

    [Fact]
    public void InvalidLexicalBodiesNeverEnterTheSuccessfulCache()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("var captured: T\n"
            + "func <U> local(item: U): T = captured\nlocal<int>(1)");
        var local = GenericLexicalTestSource.Local(root);
        var function = (AstFunction)local.Binding.Target;
        var first = analysis.GetLexicalBody(function, local.Binding.TypeArguments, local.Binding.LexicalContext!,
            TestContext.Current.CancellationToken);
        var second = analysis.GetLexicalBody(function, local.Binding.TypeArguments, local.Binding.LexicalContext!,
            TestContext.Current.CancellationToken);
        Assert.NotSame(first, second);
        Assert.Equal(BodyBindingStatus.Invalid, first.Summary.Status);
        Assert.Equal(first.Diagnostics, second.Diagnostics);
        Assert.Contains(first.Diagnostics, diagnostic => diagnostic.Code == "SOL2240");
    }

    [Fact]
    public void ChangingOnlyAssignmentFactsDoesNotReuseTheOldProof()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("var captured: T = value\n"
            + "func <U> local(item: U): T = captured\nlocal<int>(1)");
        var local = GenericLexicalTestSource.Local(root);
        var context = local.Binding.LexicalContext!;
        var unassigned = context with { Locals = context.Locals.Select(fact => fact.Local.Name == "captured"
            ? fact with { Assigned = false } : fact).ToImmutableArray() };
        var body = analysis.GetLexicalBody((AstFunction)local.Binding.Target, local.Binding.TypeArguments,
            unassigned, TestContext.Current.CancellationToken);
        Assert.NotEqual(context.Identity, unassigned.Identity);
        Assert.Equal(BodyBindingStatus.Invalid, body.Summary.Status);
        Assert.Equal(BodyBindingStatus.Bound, local.SpecializedBody!.Summary.Status);
        Assert.DoesNotContain(local.SpecializedBody.Diagnostics, diagnostic => diagnostic.Code == "SOL2240");
    }

    [Fact]
    public void DistinctLocalMethodArgumentsUseDistinctBodyInstances()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> local(item: U): U = item\n"
            + "local<int>(1)\nlocal<String>(\"text\")\nvalue");
        GenericLexicalTestSource.Bound(root);
        var locals = root.Dependencies.Where(proof => proof.Binding.Target.Name == "local").ToArray();
        Assert.Equal(2, locals.Length);
        Assert.Equal(locals[0].Binding.LexicalContext!.Identity, locals[1].Binding.LexicalContext!.Identity);
        Assert.NotSame(locals[0].SpecializedBody, locals[1].SpecializedBody);
        Assert.Equal("int", locals[0].SpecializedBody!.Summary.ReturnType!.Name);
        Assert.Equal("std.core.String", locals[1].SpecializedBody!.Summary.ReturnType!.Name);
    }

    [Fact]
    public void SameSpelledSiblingDeclarationsNeverShareAnInstance()
    {
        var (_, root) = GenericLexicalTestSource.Member("{ func <U> local(item: U): int = 1; local<T>(value) }\n"
            + "{ func <U> local(item: U): String = \"other\"; local<T>(value) }\nvalue");
        GenericLexicalTestSource.Bound(root);
        var locals = root.Dependencies.Where(proof => proof.Binding.Target.Name == "local").ToArray();
        Assert.Equal(2, locals.Length);
        Assert.NotSame(locals[0].Binding.Target, locals[1].Binding.Target);
        Assert.NotSame(locals[0].SpecializedBody, locals[1].SpecializedBody);
        Assert.Equal("int", locals[0].SpecializedBody!.Summary.ReturnType!.Name);
        Assert.Equal("std.core.String", locals[1].SpecializedBody!.Summary.ReturnType!.Name);
    }

    [Fact]
    public void TransitiveFailureDoesNotMutateTheReusableLexicalSnapshot()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("func <U> local(item: U): int = broken<U>(item)\n"
            + "local<T>(value)", "func <V> broken(item: V): int = \"wrong\"", result: "int");
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal(BodyBindingStatus.Invalid, local.Status);
        var snapshot = analysis.GetLexicalBody((AstFunction)local.Binding.Target, local.Binding.TypeArguments,
            local.Binding.LexicalContext!, TestContext.Current.CancellationToken);
        Assert.Same(local.SpecializedBody, snapshot);
        Assert.Equal(BodyBindingStatus.Bound, snapshot.Summary.Status);
        Assert.Empty(snapshot.Diagnostics);
        Assert.Empty(snapshot.UnverifiedNodes);
        Assert.Null(snapshot.GetGenericMemberCallProof(Assert.Single(snapshot.Calls).Call));
    }

    [Fact]
    public void DistinctOuterMethodArgumentsRemainInTheCacheIdentity()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + "func <V> run(value: V): V { func <U> local(item: U): V = value; local<int>(1) }\n}\n"
            + "func first(owner: Root<int>): int = owner.run<int>(1)\n"
            + "func second(owner: Root<int>): String = owner.run<String>(\"text\")");
        var roots = analysis.Calls.Where(call => call.Target.Name == "run").Select(call =>
            analysis.GetGenericMemberCallProof(call.Call)!).ToArray();
        Assert.Equal(2, roots.Length);
        Assert.All(roots, GenericLexicalTestSource.Bound);
        var first = GenericLexicalTestSource.Local(roots[0]);
        var second = GenericLexicalTestSource.Local(roots[1]);
        Assert.NotEqual(first.Binding.LexicalContext!.Identity, second.Binding.LexicalContext!.Identity);
        Assert.NotSame(first.SpecializedBody, second.SpecializedBody);
        Assert.Equal("int", first.SpecializedBody!.Summary.ReturnType!.Name);
        Assert.Equal("std.core.String", second.SpecializedBody!.Summary.ReturnType!.Name);
    }
}
