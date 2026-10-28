// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LambdaReceiverScopeTests
{
    private static LexicalAnalysis Analyze(string source)
        => LexicalAnalysis.Create(AstTestSources.Session(source), TestContext.Current.CancellationToken);

    [Fact]
    public void NestedTypeMemberUsesNearestNominalReceiver()
    {
        var analysis = Analyze("module app\nclass Outer { val value: int = 1; class Inner { val value: int = 2; "
            + "func callback(): () -> int = { value } } }");
        var capture = Assert.Single(analysis.LambdaReceiverCaptures);

        Assert.Equal("Inner", capture.Owner.Name);
        Assert.Equal(LambdaReceiverAccess.ImplicitMember, capture.Access);
        Assert.Equal("value", Assert.Single(capture.Members).Name);
        Assert.Equal("value", Assert.Single(capture.References).Spelling);
    }

    [Fact]
    public void LambdaDeclaredInsideLocalFunctionStillUsesEnclosingTypeReceiver()
    {
        var analysis = Analyze("module app\nclass Box(val value: int) { func callback(): () -> int { "
            + "func local(): () -> int = { value }; local() } }");
        var capture = Assert.Single(analysis.LambdaReceiverCaptures);

        Assert.Equal("Box", capture.Owner.Name);
        Assert.Equal(LambdaReceiverAccess.ImplicitMember, capture.Access);
        Assert.Equal("value", Assert.Single(capture.References).Spelling);
    }

    [Fact]
    public void LambdaInsideAnonymousFunctionRetainsEnclosingTypeReceiverRequirement()
    {
        var analysis = Analyze("module app\nclass Box(val value: int) { func callback(): () -> int = "
            + "func(): () -> int { { value } } }");
        var capture = Assert.Single(analysis.LambdaReceiverCaptures);

        Assert.Equal("Box", capture.Owner.Name);
        Assert.Equal(LambdaReceiverAccess.ImplicitMember, capture.Access);
        Assert.Equal("value", Assert.Single(capture.References).Spelling);
    }

    [Fact]
    public void BackingFieldReferenceInsideGetterLambdaRequiresThePropertyReceiver()
    {
        var analysis = Analyze("module app\nclass Box { val value: int { get = { field } } }");
        var capture = Assert.Single(analysis.LambdaReceiverCaptures);

        Assert.Equal("Box", capture.Owner.Name);
        Assert.Equal(LambdaReceiverAccess.ImplicitMember, capture.Access);
        Assert.Equal("field", Assert.Single(capture.References).Spelling);
        Assert.Empty(capture.Members);
    }

    [Fact]
    public void OnlySiblingLambdaThatReadsInstanceStateCapturesReceiver()
    {
        var analysis = Analyze("module app\nclass Box(val value: int) { func callbacks(): () { "
            + "val reads = { value }; val constant = { 42 } } }");
        var capture = Assert.Single(analysis.LambdaReceiverCaptures);
        var lambdas = analysis.Declarations.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstLambda>().OrderBy(lambda => lambda.Origin.Span.Start).ToArray();

        Assert.Equal(2, lambdas.Length);
        Assert.Same(lambdas[0], capture.Lambda);
        Assert.Empty(analysis.GetLambdaReceiverCaptures(lambdas[1]));
        Assert.Equal("value", Assert.Single(capture.References).Spelling);
    }

    [Fact]
    public void ExplicitReceiverReferencesAreKeptInSourceOrderAndMembersInStableNameOrder()
    {
        var analysis = Analyze("module app\nclass Box { val zulu: int = 1; val alpha: int = 2; "
            + "func callback(): () -> int = { this.zulu + alpha + zulu } }");
        var capture = Assert.Single(analysis.LambdaReceiverCaptures);

        Assert.Equal(LambdaReceiverAccess.ExplicitThis | LambdaReceiverAccess.ImplicitMember, capture.Access);
        Assert.Equal(new[] { "this", "alpha", "zulu" }, capture.References.Select(reference => reference.Spelling));
        Assert.Equal(new[] { "alpha", "zulu" }, capture.Members.Select(member => member.Name));
    }

    [Fact]
    public void ReceiverQueryForOwnedLambdaWithoutInstanceUseIsEmptyAndRepeatable()
    {
        var session = AstTestSources.Session("module app\nclass Box { func callback(): () -> int = { 42 } }");
        var analysis = LexicalAnalysis.Create(session, TestContext.Current.CancellationToken);
        var lambda = session.Files.SelectMany(file => file.Root.Descendants()).OfType<AstLambda>().Single();

        var first = analysis.GetLambdaReceiverCaptures(lambda);
        var second = analysis.GetLambdaReceiverCaptures(lambda);

        Assert.Empty(first);
        Assert.Equal(first, second);
        Assert.Empty(analysis.LambdaReceiverCaptures);
    }

    [Fact]
    public void LocalParameterShadowingInstanceMemberKeepsCaptureLocal()
    {
        var analysis = Analyze("module app\nclass Box { val value: int = 1; "
            + "func callback(value: int): () -> int = { value } }");
        var lambdaCapture = Assert.Single(analysis.LambdaCaptures);

        Assert.Equal("value", lambdaCapture.Local.Name);
        Assert.Empty(analysis.LambdaReceiverCaptures);
    }

    [Fact]
    public void LocalParameterShadowingInheritedMemberKeepsCaptureLocal()
    {
        var analysis = Analyze("module app\nopen class Base { val value: int = 1 }\n"
            + "class Child: Base { func callback(value: int): () -> int = { value } }");
        var lambdaCapture = Assert.Single(analysis.LambdaCaptures);

        Assert.Equal(LocalSymbolKind.Parameter, lambdaCapture.Local.Kind);
        Assert.Equal("value", lambdaCapture.Local.Name);
        Assert.Empty(analysis.LambdaReceiverCaptures);
    }
}
