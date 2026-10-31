// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalNullableTests
{
    [Fact]
    public void ImmutableCaptureKeepsAProvenNonNullSmartCast()
    {
        var (_, root) = GenericLexicalTestSource.Member("val captured: T? = value\n"
            + "func <U> local(item: U): T = captured\n"
            + "if captured != null { return local<int>(1) }\nvalue");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var fact = GenericLexicalTestSource.Fact(local, "captured");
        Assert.True(fact.Type!.IsNullable);
        Assert.NotNull(fact.Narrowed);
        Assert.Equal("int", fact.Narrowed.Name);
        Assert.Equal("int", Assert.Single(GenericLexicalTestSource.Reads(local, "captured")).Type!.Name);
    }

    [Fact]
    public void MutableCaptureDoesNotInheritTheCallersSmartCast()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("var captured: T? = value\n"
            + "func <U> local(item: U): T = captured\n"
            + "if captured != null { return local<int>(1) }\nvalue");
        Assert.Equal(BodyBindingStatus.Invalid, root.Status);
        var local = GenericLexicalTestSource.Local(root);
        var fact = GenericLexicalTestSource.Fact(local, "captured");
        Assert.True(fact.Type!.IsNullable);
        Assert.Null(fact.Narrowed);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
    }

    [Fact]
    public void RemovingOnlyTheSmartCastCannotReuseTheNarrowedBody()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("val captured: T? = value\n"
            + "func <U> local(item: U): T = captured\n"
            + "if captured != null { return local<int>(1) }\nvalue");
        var local = GenericLexicalTestSource.Local(root);
        var context = local.Binding.LexicalContext!;
        var changed = context with { Locals = context.Locals.Select(fact => fact.Local.Name == "captured"
            ? fact with { Narrowed = null } : fact).ToImmutableArray() };
        var body = analysis.GetLexicalBody((AstFunction)local.Binding.Target, local.Binding.TypeArguments,
            changed, TestContext.Current.CancellationToken);
        Assert.NotEqual(context.Identity, changed.Identity);
        Assert.NotSame(local.SpecializedBody, body);
        Assert.Equal(BodyBindingStatus.Invalid, body.Summary.Status);
        Assert.Equal(BodyBindingStatus.Bound, local.SpecializedBody!.Summary.Status);
    }

    [Fact]
    public void NullableLocalReturnRetainsOneNormalizedNullableLayer()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> local(item: U): U? = item\n"
            + "local<T>(value)", result: "T?", type: "int");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.True(local.Binding.ReturnType!.IsNullable);
        Assert.Equal("int", Assert.Single(local.Binding.ReturnType.Arguments).Name);
        Assert.Same(local.Binding.ReturnType, local.SpecializedBody!.Summary.ReturnType);
    }

    [Fact]
    public void ClosedNullableArgumentDoesNotCreateNestedNullableWrappers()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> { func run(value: T): T? {\n"
            + "func <U> local(item: U): U? = item\nlocal<T>(value)\n}\n}\n"
            + "func use(owner: Root<int?>, value: int?): int? = owner.run(value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.True(Assert.Single(local.Binding.TypeArguments).IsNullable);
        Assert.True(local.Binding.ReturnType!.IsNullable);
        Assert.False(Assert.Single(local.Binding.ReturnType.Arguments).IsNullable);
        Assert.Same(local.Binding.ReturnType, local.SpecializedBody!.Summary.ReturnType);
    }

    [Fact]
    public void NullableCaptureStorageIsKeptWhenTheLocalAlsoReturnsNullable()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> { func run(value: T): T? {\n"
            + "val captured: T? = value\nfunc <U> local(item: U): T? = captured\nlocal<int>(1)\n}\n}\n"
            + "func use(owner: Root<int>, value: int): int? = owner.run(value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var fact = GenericLexicalTestSource.Fact(local, "captured");
        Assert.True(fact.Type!.IsNullable);
        Assert.Null(fact.Narrowed);
        Assert.Same(fact.Type, local.SpecializedBody!.Summary.ReturnType);
    }
}
