// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalCallableSummaryTests
{
    [Fact]
    public void ALocalDefinedInsideALambdaReceivesItsTypedParameter()
    {
        var (_, root) = GenericLexicalTestSource.Member("val callback: (T) -> T = { item ->\n"
            + "func <U> local(other: U): T = item\nlocal<int>(1)\n}\ncallback(value)");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var item = GenericLexicalTestSource.Fact(local, "item");
        Assert.True(item.Assigned);
        Assert.Equal("int", item.Type!.Name);
        var lambda = Assert.Single(root.SpecializedBody!.LambdaBodies);
        Assert.True(lambda.IsComplete);
        Assert.Same(lambda, root.SpecializedBody.GetLambdaBody(lambda.Lambda));
    }

    [Fact]
    public void ALocalDefinedInsideAnAnonymousFunctionReceivesItsParameter()
    {
        var (_, root) = GenericLexicalTestSource.Member("val callback = func(item: T): T {\n"
            + "func <U> local(other: U): T = item\nlocal<int>(1)\n}\ncallback(value)");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.True(GenericLexicalTestSource.Fact(local, "item").Assigned);
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
        var anonymous = Assert.Single(root.SpecializedBody!.AnonymousFunctionBodies);
        Assert.True(anonymous.IsComplete);
        Assert.Same(anonymous, root.SpecializedBody.GetAnonymousFunctionBody(anonymous.Function));
    }

    [Fact]
    public void ATransitiveLocalFailureClearsTheSpecializedLambdaSummary()
    {
        var analysis = GenericMemberProofTestSource.Analyze("func <V> outer(value: V): V {\n"
            + "func <U> local(item: U): int = broken<U>(item)\n"
            + "val callback: (V) -> int = { item -> local<V>(item) }\ncallback(value)\nvalue\n}\n"
            + "func <W> broken(item: W): int = \"wrong\"\n"
            + "class Root<T> { func run(value: T): T = outer<T>(value) }\n"
            + "func use(owner: Root<int>, value: int): int = owner.run(value)");
        var function = GenericMemberProofTestSource.Function(analysis, "outer");
        var body = analysis.GetGenericBody(function, [analysis.Types.GetPrimitive("int")],
            TestContext.Current.CancellationToken);
        Assert.Equal(BodyBindingStatus.Invalid, body.Summary.Status);
        var lambda = Assert.Single(body.LambdaBodies);
        Assert.False(lambda.IsComplete);
        Assert.Same(lambda, body.GetLambdaBody(lambda.Lambda));
        var localCall = Assert.Single(body.Calls, call => call.Target.Name == "local");
        Assert.Equal(BodyBindingStatus.Invalid, body.GetExpression(localCall.Call)!.Status);
        Assert.Contains(localCall.Call, body.UnverifiedNodes);
    }

    [Fact]
    public void DependencyCheckedAnonymousSummaryDoesNotMutateTheLocalSnapshot()
    {
        var analysis = GenericMemberProofTestSource.Analyze("func <V> outer(value: V): V {\n"
            + "func <U> local(item: U): int = broken<U>(item)\n"
            + "val callback = func(item: V): int = local<V>(item)\ncallback(value)\nvalue\n}\n"
            + "func <W> broken(item: W): int = \"wrong\"\n"
            + "class Root<T> { func run(value: T): T = outer<T>(value) }\n"
            + "func use(owner: Root<int>, value: int): int = owner.run(value)");
        var rootCall = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var outerProof = Assert.Single(analysis.GetGenericMemberCallProof(rootCall.Call)!.Dependencies);
        var localSnapshot = outerProof.SpecializedBody!;
        Assert.True(Assert.Single(localSnapshot.AnonymousFunctionBodies).IsComplete);
        var checkedBody = analysis.GetGenericBody(localSnapshot.Declaration,
            outerProof.Binding.TypeArguments, TestContext.Current.CancellationToken);
        var anonymous = Assert.Single(checkedBody.AnonymousFunctionBodies);
        Assert.False(anonymous.IsComplete);
        Assert.Same(anonymous, checkedBody.GetAnonymousFunctionBody(anonymous.Function));
        Assert.True(Assert.Single(localSnapshot.AnonymousFunctionBodies).IsComplete);
        Assert.NotSame(localSnapshot, checkedBody);
    }

    [Fact]
    public void UnrelatedLambdaSummaryKeepsItsCompleteFlag()
    {
        var analysis = GenericMemberProofTestSource.Analyze("func <V> outer(value: V): V {\n"
            + "func <U> local(item: U): int = broken<U>(item)\n"
            + "val bad: (V) -> int = { item -> local<V>(item) }\n"
            + "val good: (V) -> V = { item -> item }\nbad(value)\ngood(value)\n}\n"
            + "func <W> broken(item: W): int = \"wrong\"\n"
            + "class Root<T> { func run(value: T): T = outer<T>(value) }\n"
            + "func use(owner: Root<int>, value: int): int = owner.run(value)");
        var body = GenericMemberProofTestSource.Specialize(analysis, "outer", "int");
        Assert.Equal(2, body.LambdaBodies.Length);
        Assert.False(body.LambdaBodies[0].IsComplete);
        Assert.True(body.LambdaBodies[1].IsComplete);
        Assert.Equal(BodyBindingStatus.Invalid, body.Summary.Status);
        Assert.All(body.LambdaBodies, summary => Assert.Same(summary, body.GetLambdaBody(summary.Lambda)));
    }

    [Fact]
    public void SpecializedCallableSummaryQueriesRejectForeignNodes()
    {
        const string source = "val first: (T) -> T = { item -> item }\n"
            + "val second = func(item: T): T = item\nvalue";
        var (_, root) = GenericLexicalTestSource.Member(source);
        var (_, other) = GenericLexicalTestSource.Member(source);
        var body = root.SpecializedBody!;
        var foreignLambda = Assert.Single(other.SpecializedBody!.LambdaBodies).Lambda;
        var foreignAnonymous = Assert.Single(other.SpecializedBody.AnonymousFunctionBodies).Function;
        Assert.Throws<ArgumentException>(() => body.GetLambdaBody(foreignLambda));
        Assert.Throws<ArgumentException>(() => body.GetAnonymousFunctionBody(foreignAnonymous));
        Assert.Throws<ArgumentNullException>(() => body.GetLambdaBody(null!));
        Assert.Throws<ArgumentNullException>(() => body.GetAnonymousFunctionBody(null!));
    }
}
