// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericMemberRecursiveSubstitutionTests
{
    [Fact]
    public void RecursiveTargetUsesItsOwnMethodVectorInsteadOfCallerVector()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + "func <U> hop(value: U): int { val next: long = 1; hop<long>(next) }\n}\n"
            + "func use(owner: Root<int>): int = owner.hop<int>(1)");
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "hop"
            && Assert.Single(binding.TypeArguments).Name == "int");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        var next = Assert.Single(proof.Dependencies);
        Assert.Equal("long", Assert.Single(next.Binding.TypeArguments).Name);
        Assert.Equal("long", Assert.Single(next.Binding.ParameterTypes).Name);
        var recursive = Assert.Single(next.Dependencies);
        Assert.True(recursive.IsRecursiveReference);
        Assert.Equal("long", Assert.Single(recursive.Binding.TypeArguments).Name);
        Assert.False(analysis.HasErrors);
    }

    [Fact]
    public void RefStorageIsCheckedAgainstTheRecursiveCalleeVector()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + "func <U> hop(ref value: U): int { var next: long = 1; hop<long>(ref next) }\n}\n"
            + "func use(owner: Root<int>): int { var value: int = 1; owner.hop<int>(ref value) }");
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "hop"
            && Assert.Single(binding.TypeArguments).Name == "int");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        Assert.Equal("long", Assert.Single(Assert.Single(proof.Dependencies).Binding.ParameterTypes).Name);
        Assert.False(analysis.HasErrors);
    }

    [Fact]
    public void RecursiveLambdaContextUsesTheCalleeFunctionSignature()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + "func <U> hop(value: U, callback: (U) -> U): int {\n"
            + "val next: long = 1; hop<long>(next, { x -> x })\n}\n}\n"
            + "func use(owner: Root<int>): int = owner.hop<int>(1, { x -> x })");
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "hop"
            && Assert.Single(binding.TypeArguments).Name == "int");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        var target = Assert.Single(proof.Dependencies).Binding;
        Assert.Equal("long", target.ParameterTypes[0].Name);
        Assert.Equal(ResolvedTypeKind.Function, target.ParameterTypes[1].Kind);
        Assert.All(target.ParameterTypes[1].Arguments, type => Assert.Equal("long", type.Name));
        Assert.False(analysis.HasErrors);
    }

    [Fact]
    public void SameSpelledOwnerAndMethodArgumentsStayIndependentDuringRecursion()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + "func <T> hop(value: T): int { val next: long = 1; hop<long>(next) }\n}\n"
            + "func use(owner: Root<short>): int = owner.hop<int>(1)");
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "hop"
            && Assert.Single(binding.TypeArguments).Name == "int");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        Assert.Equal("short", Assert.Single(proof.Projection!.Body!.OwnerTypeArguments).Name);
        var child = Assert.Single(proof.Dependencies);
        Assert.Equal("short", Assert.Single(child.Projection!.Body!.OwnerTypeArguments).Name);
        Assert.Equal("long", Assert.Single(child.Projection.Body.TypeArguments).Name);
        Assert.False(analysis.HasErrors);
    }
}
