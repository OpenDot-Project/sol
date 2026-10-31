// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LambdaReceiverCaptureTests
{
    private static LexicalAnalysis Analyze(string source)
        => LexicalAnalysis.Create(AstTestSources.Session(source), TestContext.Current.CancellationToken);

    [Fact]
    public void ExplicitThisAndConstructorPropertyCaptureOneReceiverIdentity()
    {
        var analysis = Analyze("module app\nclass Box(val value: int) { "
            + "func callback(): () -> int = { this.value + value } }");
        var capture = Assert.Single(analysis.LambdaReceiverCaptures);

        Assert.Equal(LambdaReceiverAccess.ExplicitThis | LambdaReceiverAccess.ImplicitMember, capture.Access);
        Assert.Equal("Box", capture.Owner.Name);
        Assert.Equal(2, capture.References.Length);
        Assert.Empty(capture.Members);
        Assert.Empty(analysis.LambdaCaptures);
    }

    [Fact]
    public void ImplicitTypeMemberCarriesResolvedMemberIdentity()
    {
        var analysis = Analyze("module app\nclass Box { val value: int = 1; "
            + "func callback(): () -> int = { value } }");
        var capture = Assert.Single(analysis.LambdaReceiverCaptures);

        Assert.Equal(LambdaReceiverAccess.ImplicitMember, capture.Access);
        var member = Assert.Single(capture.Members);
        Assert.Equal(SymbolKind.Property, member.Kind);
        Assert.Equal("value", member.Name);
    }

    [Fact]
    public void ReceiverRequirementPropagatesThroughEveryNestedLambdaEnvironment()
    {
        var analysis = Analyze("module app\nclass Box(val value: int) { "
            + "func callback(): () -> () -> int = { { value } } }");
        var captures = analysis.LambdaReceiverCaptures.OrderBy(capture => capture.Lambda.Origin.Span.Start).ToArray();

        Assert.Equal(2, captures.Length);
        Assert.All(captures, capture =>
        {
            Assert.Equal("Box", capture.Owner.Name);
            Assert.Equal(LambdaReceiverAccess.ImplicitMember, capture.Access);
            Assert.Single(capture.References);
        });
    }

    [Fact]
    public void ExplicitSuperIsRecordedAsReceiverUse()
    {
        var analysis = Analyze("module app\nopen class Base { open func read(): int = 1 }\n"
            + "class Child: Base { func callback(): () -> int = { super.read() } }");
        var capture = Assert.Single(analysis.LambdaReceiverCaptures);

        Assert.Equal(LambdaReceiverAccess.ExplicitSuper, capture.Access);
        Assert.Equal("Child", capture.Owner.Name);
    }

    [Fact]
    public void ShadowingLocalParameterDoesNotCaptureTheReceiver()
    {
        var analysis = Analyze("module app\nclass Box { val value: int = 1; "
            + "func callback(value: int): () -> int = { value } }");

        Assert.Empty(analysis.LambdaReceiverCaptures);
        Assert.Single(analysis.LambdaCaptures);
        Assert.Equal("value", Assert.Single(analysis.LambdaCaptures).Local.Name);
    }

    [Fact]
    public void QualifiedAccessThroughAnotherCapturedObjectDoesNotCaptureThis()
    {
        var analysis = Analyze("module app\nclass Box { val value: int = 1; "
            + "func callback(other: Box): () -> int = { other.value } }");

        Assert.Empty(analysis.LambdaReceiverCaptures);
        Assert.Equal("other", Assert.Single(analysis.LambdaCaptures).Local.Name);
    }

    [Fact]
    public void ReceiverQueriesRejectForeignLambdaSnapshots()
    {
        var firstSession = AstTestSources.Session("module app\nclass Box { val value: int = 1; "
            + "func callback(): () -> int = { value } }");
        var secondSession = AstTestSources.Session("module app\nclass Box { val value: int = 1; "
            + "func callback(): () -> int = { value } }");
        var first = LexicalAnalysis.Create(firstSession, TestContext.Current.CancellationToken);
        var second = LexicalAnalysis.Create(secondSession, TestContext.Current.CancellationToken);
        var foreign = secondSession.Files.SelectMany(file => file.Root.Descendants()).OfType<AstLambda>().Single();

        Assert.Throws<System.ArgumentException>(() => first.GetLambdaReceiverCaptures(foreign));
        var owned = firstSession.Files.SelectMany(file => file.Root.Descendants()).OfType<AstLambda>().Single();
        Assert.Single(first.GetLambdaReceiverCaptures(owned));
    }
}
