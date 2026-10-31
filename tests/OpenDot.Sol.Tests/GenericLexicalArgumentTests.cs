// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalArgumentTests
{
    [Fact]
    public void NamedArgumentsPreserveTheirSourceToParameterMapping()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> local(first: U, second: T): T = second\n"
            + "local<int>(second = value, first = 1)");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal(new[] { 1, 0 }, local.Binding.Arguments.Select(argument => argument.ParameterIndex));
        Assert.Equal(new[] { 0, 1 }, local.Binding.Arguments.Select(argument => argument.ArgumentIndex));
        Assert.All(local.Binding.ParameterTypes, type => Assert.Equal("int", type.Name));
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
    }

    [Fact]
    public void ADefaultCanReadTheEarlierClosedLocalParameter()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> local(first: U, second: U = first): U = second\n"
            + "local<T>(value)");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal(new[] { 1 }, local.Binding.DefaultParameters);
        Assert.Single(local.Binding.Arguments);
        Assert.All(local.Binding.ParameterTypes, type => Assert.Equal("int", type.Name));
        Assert.All(GenericLexicalTestSource.Reads(local, "first"), read => Assert.Equal("int", read.Type!.Name));
    }

    [Fact]
    public void ExplicitValueDoesNotAppearAsAnOmittedDefault()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> local(first: U, second: U = first): U = second\n"
            + "local<T>(value, value)");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Empty(local.Binding.DefaultParameters);
        Assert.Equal(2, local.Binding.Arguments.Length);
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
    }

    [Fact]
    public void ClosedVarargParameterHasAnArrayTypeInsideTheLocalBody()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> local(vararg items: U): []U = items\n"
            + "local<T>(value, value)[0]");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal(2, local.Binding.Arguments.Length);
        Assert.Equal(ResolvedTypeKind.Array, local.SpecializedBody!.Summary.ReturnType!.Kind);
        var read = Assert.Single(GenericLexicalTestSource.Reads(local, "items"));
        Assert.Same(local.SpecializedBody.Summary.ReturnType, read.Type);
        Assert.Equal("int", Assert.Single(read.Type!.Arguments).Name);
    }

    [Fact]
    public void EmptyLocalVarargAndOmittedDefaultAreDifferentFacts()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> local(vararg items: U): int = 1\n"
            + "local<T>()", result: "int");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal(new[] { 0 }, local.Binding.EmptyVarargParameters);
        Assert.Empty(local.Binding.DefaultParameters);
        Assert.Empty(local.Binding.Arguments);
    }

    [Fact]
    public void SpreadLocalArgumentsKeepTheClosedElementType()
    {
        var (_, root) = GenericLexicalTestSource.Member("val items = [value]\n"
            + "func <U> local(vararg args: U): []U = args\nlocal<T>(*items)[0]");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.True(Assert.Single(local.Binding.Arguments).IsSpread);
        Assert.Equal("int", Assert.Single(local.Binding.ParameterTypes).Name);
        Assert.Equal("int", Assert.Single(local.SpecializedBody!.Summary.ReturnType!.Arguments).Name);
    }

    [Fact]
    public void LocalRefParameterKeepsExactClosedStorageIdentity()
    {
        var (_, root) = GenericLexicalTestSource.Member("var copy: T = value\n"
            + "func <U> local(ref item: U): U = item\nlocal<T>(ref copy)");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var parameterType = Assert.Single(local.Binding.ParameterTypes);
        Assert.Same(parameterType, local.SpecializedBody!.Summary.ReturnType);
        Assert.True(GenericLexicalTestSource.Fact(local, "copy").Assigned);
        Assert.All(GenericLexicalTestSource.Reads(local, "item"), read =>
        {
            Assert.Same(parameterType, read.Type);
            Assert.True(read.IsStorage);
            Assert.True(read.IsWritable);
        });
    }

    [Fact]
    public void LocalOutParameterMustBeAssignedOnEveryNormalReturn()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("var copy: T\n"
            + "func <U> local(out item: U): T = value\nlocal<T>(out copy)");
        Assert.Equal(BodyBindingStatus.Invalid, root.Status);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal(BodyBindingStatus.Invalid, local.SpecializedBody!.Summary.Status);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2242");
        Assert.False(GenericLexicalTestSource.Fact(local, "copy").Assigned);
    }

    [Fact]
    public void LocalOutWriteIsCheckedWithTheLocalAndEnclosingSubstitutions()
    {
        var (_, root) = GenericLexicalTestSource.Member("var copy: T\n"
            + "func <U> local(out item: U, initial: U): T { item = initial; value }\n"
            + "local<T>(out copy, value)");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal(2, local.Binding.ParameterTypes.Length);
        Assert.All(local.Binding.ParameterTypes, type => Assert.Equal("int", type.Name));
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
    }
}
