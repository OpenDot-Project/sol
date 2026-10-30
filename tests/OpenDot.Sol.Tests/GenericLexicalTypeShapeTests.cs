// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalTypeShapeTests
{
    [Fact]
    public void CapturedTupleTypeIsRebuiltUnderTheClosedOwner()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> { func run(value: T): (T, int) {\n"
            + "val captured = (value, 1)\nfunc <U> local(item: U): (T, int) = captured\nlocal<int>(1)\n}\n}\n"
            + "func use(owner: Root<String>, value: String): (String, int) = owner.run(value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var tuple = GenericLexicalTestSource.Fact(local, "captured").Type!;
        Assert.Equal(ResolvedTypeKind.Tuple, tuple.Kind);
        Assert.Equal(new[] { "std.core.String", "int" }, tuple.Arguments.Select(type => type.Name));
        Assert.Same(tuple, local.SpecializedBody!.Summary.ReturnType);
    }

    [Fact]
    public void AClosedFunctionCaptureRetainsItsParameterAndResultTypes()
    {
        var (_, root) = GenericLexicalTestSource.Member("val captured: (T) -> T = { item -> item }\n"
            + "func <U> local(item: U): T = captured(value)\nlocal<int>(1)");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var function = GenericLexicalTestSource.Fact(local, "captured").Type!;
        Assert.Equal(ResolvedTypeKind.Function, function.Kind);
        Assert.Equal(new[] { "int", "int" }, function.Arguments.Select(type => type.Name));
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
    }

    [Fact]
    public void ConstructedNominalCaptureKeepsInvariantTypeArguments()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Box<V> {}\nclass Root<T> {\n"
            + "func run(value: Box<T>): Box<T> {\n"
            + "func <U> local(item: U): Box<T> = value\nlocal<int>(1)\n}\n}\n"
            + "func use(owner: Root<String>, value: Box<String>): Box<String> = owner.run(value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var captured = GenericLexicalTestSource.Fact(local, "value").Type!;
        Assert.Equal(ResolvedTypeKind.Nominal, captured.Kind);
        Assert.Equal("Box", captured.Declaration!.Name);
        Assert.Equal("std.core.String", Assert.Single(captured.Arguments).Name);
        Assert.Same(captured, local.SpecializedBody!.Summary.ReturnType);
    }

    [Fact]
    public void NestedArrayCaptureRetainsBothArrayLayers()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> { func run(value: [][]T): [][]T {\n"
            + "func <U> local(item: U): [][]T = value\nlocal<int>(1)\n}\n}\n"
            + "func use(owner: Root<int>, value: [][]int): [][]int = owner.run(value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var outer = GenericLexicalTestSource.Fact(local, "value").Type!;
        Assert.Equal(ResolvedTypeKind.Array, outer.Kind);
        var inner = Assert.Single(outer.Arguments);
        Assert.Equal(ResolvedTypeKind.Array, inner.Kind);
        Assert.Equal("int", Assert.Single(inner.Arguments).Name);
        Assert.Same(outer, local.SpecializedBody!.Summary.ReturnType);
    }

    [Fact]
    public void NullableArrayAndArrayOfNullableElementsRemainDifferentCaptures()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + "func run(first: ([]T)?, second: [](T?)): ([]T)? {\n"
            + "func <U> local(item: U): ([]T)? = first\nlocal<int>(1)\n}\n}\n"
            + "func use(owner: Root<int>, first: ([]int)?, second: [](int?)): ([]int)? = owner.run(first, second)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var first = GenericLexicalTestSource.Fact(local, "first").Type!;
        var second = GenericLexicalTestSource.Fact(local, "second").Type!;
        Assert.True(first.IsNullable);
        Assert.Equal(ResolvedTypeKind.Array, Assert.Single(first.Arguments).Kind);
        Assert.Equal(ResolvedTypeKind.Array, second.Kind);
        Assert.True(Assert.Single(second.Arguments).IsNullable);
        Assert.NotSame(first, second);
    }
}
