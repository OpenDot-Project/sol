// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalInferenceContextTests
{
    [Fact]
    public void ExpectedResultCanSolveTheLocalMethodParameterBeforeBodyProof()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> local(item: U): U = item\nlocal(value)");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal("int", Assert.Single(local.Binding.TypeArguments).Name);
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
        Assert.Single(local.Binding.Arguments);
    }

    [Fact]
    public void LocalExpressionResultUsesItsOwnSolvedParameter()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> local(item: U) = item\nlocal(value)");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal("int", Assert.Single(local.Binding.TypeArguments).Name);
        Assert.Same(local.Binding.ReturnType, local.SpecializedBody!.Summary.ReturnType);
        Assert.Equal("int", Assert.Single(GenericLexicalTestSource.Reads(local, "item")).Type!.Name);
    }

    [Fact]
    public void LambdaArgumentEvidenceRemainsCandidateLocalForALexicalFunction()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> local(item: U, callback: (U) -> U): U = callback(item)\n"
            + "local(value) { item -> item }");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal("int", Assert.Single(local.Binding.TypeArguments).Name);
        Assert.Equal(2, local.Binding.ParameterTypes.Length);
        var callback = local.Binding.ParameterTypes[1];
        Assert.Equal(ResolvedTypeKind.Function, callback.Kind);
        Assert.All(callback.Arguments, type => Assert.Equal("int", type.Name));
    }

    [Fact]
    public void CapturedCallbackAndLocalParameterHaveIndependentGenericOwners()
    {
        var (_, root) = GenericLexicalTestSource.Member("val callback: (T) -> T = { item -> item }\n"
            + "func <U> local(item: U): T = callback(value)\nlocal<String>(\"text\")");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal("std.core.String", Assert.Single(local.Binding.TypeArguments).Name);
        var callback = GenericLexicalTestSource.Fact(local, "callback").Type!;
        Assert.All(callback.Arguments, type => Assert.Equal("int", type.Name));
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
    }

    [Fact]
    public void LocalArrayLiteralEvidenceUsesTheClosedEnclosingElementType()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> local(items: []U): U = items[0]\n"
            + "local([value])");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal("int", Assert.Single(local.Binding.TypeArguments).Name);
        var array = Assert.Single(local.Binding.ParameterTypes);
        Assert.Equal(ResolvedTypeKind.Array, array.Kind);
        Assert.Equal("int", Assert.Single(array.Arguments).Name);
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
    }

    [Fact]
    public void RefInferenceDoesNotSilentlyWidenClosedLocalStorage()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("var copy: T = value\n"
            + "func <U> local(ref item: U): U = item\nlocal<long>(ref copy)\nvalue");
        Assert.Equal(BodyBindingStatus.Invalid, root.Status);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.DoesNotContain(root.Dependencies, proof => proof.Binding.Target.Name == "local"
            && proof.Status == BodyBindingStatus.Bound);
    }
}
