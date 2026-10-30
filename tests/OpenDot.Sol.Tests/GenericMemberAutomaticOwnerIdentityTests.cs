// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericMemberAutomaticOwnerIdentityTests
{
    [Fact]
    public void IdenticalShortNamesInDifferentModulesNeverShareBodyProofs()
    {
        var analysis = AnalyzeFiles(
            "module first\nclass Root<T> { func echo(value: T): T = value }",
            "module second\nclass Root<T> { func echo(value: T): T = value }",
            "module app\nimport first\nimport second\nfunc use(a: first.Root<int>, b: second.Root<int>): int { a.echo(1); b.echo(2) }");
        var calls = analysis.Calls.Where(call => call.Target.Name == "echo").ToArray();
        Assert.Equal(2, calls.Length);
        var first = analysis.GetGenericMemberCallProof(calls[0].Call)!;
        var second = analysis.GetGenericMemberCallProof(calls[1].Call)!;
        Assert.Equal(BodyBindingStatus.Bound, first.Status);
        Assert.Equal(BodyBindingStatus.Bound, second.Status);
        Assert.NotSame(first.Projection!.Body, second.Projection!.Body);
        Assert.NotSame(first.Projection.Body!.ConstructedOwner!.Declaration,
            second.Projection.Body!.ConstructedOwner!.Declaration);
        Assert.Equal(new[] { "first.Root", "second.Root" }, calls.Select(call => call.Receiver!.Name).OrderBy(name => name));
    }

    [Fact]
    public void PartialOwnerMethodsUseTheMergedCanonicalParameterIdentity()
    {
        var analysis = AnalyzeFiles(
            "module app\npartial class Root<T> { func first(value: T): T = value }",
            "module app\npartial class Root<T> { func second(value: T): T = first(value) }",
            "module app\nfunc use(owner: Root<int>): int = owner.second(1)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "second");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        var dependency = Assert.Single(proof.Dependencies);
        Assert.Equal("first", dependency.Binding.Target.Name);
        Assert.Same(proof.Projection!.Body!.ConstructedOwner, dependency.Projection!.Body!.ConstructedOwner);
        Assert.Equal(2, proof.Projection.Body.ConstructedOwner!.Declaration!.Parts.Length);
    }

    [Fact]
    public void InheritedOwnerCompositionIsAppliedBeforeAutomaticSignatureChecking()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface Root<A, B> { func first(a: A, b: B): A = a }\n"
            + "interface Middle<X, Y>: Root<[]Y, X> {}\n"
            + "class Leaf<T, U>: Middle<U, T> {}\n"
            + "func use(owner: Leaf<int, long>, values: []int, marker: long): []int = owner.first(values, marker)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "first");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        Assert.Equal("app.Leaf", call.SourceReceiver!.Name);
        Assert.Equal("app.Root", call.Receiver!.Name);
        Assert.Equal(ResolvedTypeKind.Array, call.ParameterTypes[0].Kind);
        Assert.Equal("int", Assert.Single(call.ParameterTypes[0].Arguments).Name);
        Assert.Equal("long", call.ParameterTypes[1].Name);
        var path = Assert.Single(Assert.Single(proof.Projection!.Projection.Candidates).Paths);
        Assert.Equal(new[] { "app.Leaf", "app.Middle", "app.Root" }, path.Select(type => type.Name));
    }

    [Fact]
    public void InferredThisTypeUsesTheDeclaringOwnerRatherThanDerivedReceiver()
    {
        var analysis = GenericMemberProofTestSource.Analyze("open class Root<T> { func self() = this }\n"
            + "class Leaf<T>: Root<T> {}\n"
            + "func use(owner: Leaf<int>): Root<int> = owner.self()");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "self");
        Assert.Equal("app.Root", call.ReturnType!.Name);
        Assert.Equal("int", Assert.Single(call.ReturnType.Arguments).Name);
        Assert.Equal("app.Leaf", call.SourceReceiver!.Name);
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        Assert.Same(call.ReturnType, proof.Projection!.Body!.Summary.ReturnType);
    }

    private static BodyAnalysis AnalyzeFiles(params string[] files)
    {
        var session = AstTestSources.Session(files.Append(GenericMemberProofTestSource.Core).ToArray());
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }
}
