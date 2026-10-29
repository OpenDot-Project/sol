// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericMemberAutomaticCallProofTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}";

    [Theory]
    [InlineData("Root<int>")]
    [InlineData("Leaf<int>")]
    public void ClosedSelectedCallAutomaticallyObtainsBodyProof(string receiver)
    {
        var analysis = Analyze("open class Root<T> { func echo(value: T): T = value }\n"
            + "class Leaf<T>: Root<T> {}\n"
            + "func use(owner: " + receiver + ", value: int): int = owner.echo(value)");
        var binding = Assert.Single(analysis.Calls, call => call.Target.Name == "echo");
        var proof = analysis.GetGenericMemberCallProof(binding.Call);
        Assert.NotNull(proof);
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        Assert.Equal("app.Root", proof.Projection!.Body!.ConstructedOwner!.Name);
        Assert.Equal("int", proof.Projection.Body.Summary.ReturnType!.Name);
        Assert.Empty(proof.Dependencies);
    }

    [Fact]
    public void AnnotatedRecursiveInstanceHasAClosedDependencyGroupProof()
    {
        var analysis = Analyze("class Root<T> { func again(value: T): T = again(value) }\n"
            + "func use(owner: Root<int>, value: int): int = owner.again(value)");
        var closed = Assert.Single(analysis.Calls,
            call => call.Target.Name == "again" && call.Receiver!.Arguments[0].Name == "int");
        var proof = analysis.GetGenericMemberCallProof(closed.Call);
        Assert.NotNull(proof);
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        Assert.True(Assert.Single(proof.Dependencies).IsRecursiveReference);
        Assert.DoesNotContain(closed.Call, analysis.UnverifiedNodes);
        Assert.Equal(BodyBindingStatus.Bound, analysis.GetExpression(closed.Call)!.Status);
        Assert.Equal(BodyBindingStatus.Bound,
            Assert.Single(analysis.Bodies, body => body.Declaration.Name == "use").Status);
    }

    [Fact]
    public void OpenReceiverDoesNotPublishAClosedBodyProof()
    {
        var analysis = Analyze("class Root<T> { func echo(value: T): T = value }\n"
            + "func <U> use(owner: Root<U>, value: U): U = owner.echo(value)");
        var binding = Assert.Single(analysis.Calls, call => call.Target.Name == "echo");
        var proof = analysis.GetGenericMemberCallProof(binding.Call);
        Assert.NotNull(proof);
        Assert.Equal(BodyBindingStatus.Deferred, proof.Status);
        Assert.Null(proof.Projection);
        Assert.False(analysis.IsComplete);
    }

    [Fact]
    public void OrdinaryFreeFunctionCallsDoNotRequireOwnerProof()
    {
        var analysis = Analyze("func identity(value: int): int = value\n"
            + "func use(value: int): int = identity(value)");
        var binding = Assert.Single(analysis.Calls);
        Assert.Null(analysis.GetGenericMemberCallProof(binding.Call));
        Assert.True(analysis.IsComplete);
    }

    [Fact]
    public void ForeignCallCannotReadAnotherAnalysisProof()
    {
        const string source = "class Root<T> { func echo(value: T): T = value }\n"
            + "func use(owner: Root<int>): int = owner.echo(1)";
        var first = Analyze(source);
        var second = Analyze(source);
        var call = Assert.Single(second.Calls).Call;
        Assert.Throws<ArgumentException>(() => first.GetGenericMemberCallProof(call));
    }

    [Fact]
    public void InvalidClosedBodyInvalidatesItsCallingBodyAndExpression()
    {
        var analysis = Analyze("class Root<T> { func check(value: T): int = value }\n"
            + "func use(owner: Root<String>, value: String): int = owner.check(value)");
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "check");
        var proof = analysis.GetGenericMemberCallProof(call.Call);
        Assert.NotNull(proof);
        Assert.Equal(BodyBindingStatus.Invalid, proof.Status);
        Assert.True(analysis.HasErrors);
        Assert.False(analysis.IsComplete);
        Assert.Equal(BodyBindingStatus.Invalid, analysis.GetExpression(call.Call)!.Status);
        Assert.Equal(BodyBindingStatus.Invalid,
            Assert.Single(analysis.Bodies, body => body.Declaration.Name == "use").Status);
    }

    [Fact]
    public void MissingInterfaceBodyMakesAnEnclosingLambdaIncomplete()
    {
        var analysis = Analyze("interface Root<T> { func echo(value: T): T }\n"
            + "func use(owner: Root<int>): () -> int = { owner.echo(1) }");
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "echo");
        Assert.Equal(BodyBindingStatus.Deferred, analysis.GetGenericMemberCallProof(call.Call)!.Status);
        Assert.False(Assert.Single(analysis.LambdaBodies).IsComplete);
        Assert.Equal(BodyBindingStatus.Deferred,
            Assert.Single(analysis.Bodies, body => body.Declaration.Name == "use").Status);
    }

    private static BodyAnalysis Analyze(string declarations)
    {
        var session = AstTestSources.Session("module app\n" + declarations, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }
}
