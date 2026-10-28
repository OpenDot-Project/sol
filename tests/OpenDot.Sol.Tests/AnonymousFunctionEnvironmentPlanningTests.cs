// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionEnvironmentPlanningTests
{
    [Fact]
    public void LocalFieldsUseStableDeclarationOrderAndCopyOrSharedCellCandidates()
    {
        var analysis = Analyze("module app\nfunc run(seed: int): int {\n"
            + "var state = seed\n"
            + "val snapshot = seed\n"
            + "val callback = func(): int { state = state + 1; snapshot + state }\n"
            + "callback()\n}");
        var function = AnonymousFunction(analysis);
        var plan = AnonymousFunctionEnvironmentPlanner.Create(analysis, function,
            TestContext.Current.CancellationToken);
        var repeated = AnonymousFunctionEnvironmentPlanner.Create(analysis, function,
            TestContext.Current.CancellationToken);

        AssertNoErrors(analysis);
        Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, plan.Escape.Disposition);
        Assert.Equal(AnonymousFunctionEnvironmentLifetimeCandidate.Stack, plan.LifetimeCandidate);
        Assert.True(plan.CandidateFactsComplete);
        Assert.Equal(new[] { "state", "snapshot" }, plan.Fields.Select(field => field.Local!.Name));
        Assert.Equal(new[] { 0, 1 }, plan.Fields.Select(field => field.Ordinal));
        Assert.Equal(new[] { CallableEnvironmentFieldStorage.SharedMutableCell, CallableEnvironmentFieldStorage.ValueCopy },
            plan.Fields.Select(field => field.Storage));
        Assert.All(plan.Fields, field => Assert.Equal("int", field.SemanticType!.Name));
        Assert.Equal(plan.Fields.Select(field => (field.Ordinal, field.Local!.Id)),
            repeated.Fields.Select(field => (field.Ordinal, field.Local!.Id)));
        Assert.False(plan.HasVerifiedAbi);
        Assert.False(plan.HasVerifiedLayout);
    }

    [Fact]
    public void ReceiverIsFirstAndUsesItsResolvedNominalTypeBeforeLocalFields()
    {
        var analysis = Analyze("module app\nclass Box(val seed: int) {\n"
            + "func immediate(): int {\n"
            + "val offset = 2\n"
            + "val callback = func(): int { this.seed + offset }\n"
            + "callback()\n}\n}");
        var plan = AnonymousFunctionEnvironmentPlanner.Create(analysis, AnonymousFunction(analysis),
            TestContext.Current.CancellationToken);

        AssertNoErrors(analysis);
        Assert.Equal(AnonymousFunctionEnvironmentLifetimeCandidate.Stack, plan.LifetimeCandidate);
        Assert.Collection(plan.Fields,
            receiver =>
            {
                Assert.Equal(0, receiver.Ordinal);
                Assert.Equal(AnonymousFunctionEnvironmentFieldKind.Receiver, receiver.Kind);
                Assert.Equal(CallableEnvironmentFieldStorage.ReceiverCapture, receiver.Storage);
                Assert.Equal("app.Box", receiver.SemanticType!.Name);
                Assert.True(receiver.Receiver!.IsComplete);
            },
            local =>
            {
                Assert.Equal(1, local.Ordinal);
                Assert.Equal(AnonymousFunctionEnvironmentFieldKind.LocalCapture, local.Kind);
                Assert.Equal("offset", local.Local!.Name);
                Assert.Equal("int", local.SemanticType!.Name);
            });
    }

    [Fact]
    public void CapturedLocalCallableRetainsItsNestedEnvironmentStorageCandidate()
    {
        var analysis = Analyze("module app\nfunc run(seed: int): int {\n"
            + "func helper(): int = seed\n"
            + "val callback = func(): int { helper() }\n"
            + "callback()\n}");
        var plan = AnonymousFunctionEnvironmentPlanner.Create(analysis, AnonymousFunction(analysis),
            TestContext.Current.CancellationToken);
        var field = Assert.Single(plan.Fields);

        Assert.Equal("helper", field.Local!.Name);
        Assert.Equal(CallableEnvironmentFieldStorage.NestedCallableEnvironment, field.Storage);
        Assert.Same(Assert.Single(analysis.Sources.Lexical.GetAnonymousFunctionCaptures(plan.Function)), field.Capture);
        Assert.False(plan.HasVerifiedAbi);
        Assert.False(plan.HasVerifiedLayout);
    }

    [Fact]
    public void EscapingBorrowedCaptureCannotBePromotedToAHeapCandidate()
    {
        var analysis = Analyze("module app\nfunc create(ref value: int): () -> int {\n"
            + "val callback = func(): int { value }\n"
            + "return callback\n}");
        var plan = AnonymousFunctionEnvironmentPlanner.Create(analysis, AnonymousFunction(analysis),
            TestContext.Current.CancellationToken);
        var borrowed = Assert.Single(plan.Fields);

        Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape, plan.Escape.Disposition);
        Assert.Equal(CallableEnvironmentFieldStorage.BorrowedLocation, borrowed.Storage);
        Assert.Equal(AnonymousFunctionEnvironmentLifetimeCandidate.Unknown, plan.LifetimeCandidate);
        Assert.Contains(AnonymousFunctionEnvironmentUncertainty.EscapingBorrowedCapture, plan.Uncertainties);
        Assert.NotEqual(AnonymousFunctionEnvironmentLifetimeCandidate.Heap, plan.LifetimeCandidate);
        Assert.False(plan.HasVerifiedAbi);
        Assert.False(plan.HasVerifiedLayout);
    }

    [Fact]
    public void MissingResolvedCaptureTypeProducesAnUnknownFieldAndLifetime()
    {
        var analysis = Analyze("module app\nfunc run(): int {\n"
            + "val unresolved = missingValue\n"
            + "val callback = func(): int { unresolved }\n"
            + "callback()\n}");
        var plan = AnonymousFunctionEnvironmentPlanner.Create(analysis, AnonymousFunction(analysis),
            TestContext.Current.CancellationToken);
        var field = Assert.Single(plan.Fields);

        Assert.Null(analysis.GetLocalType(field.Local!));
        Assert.Null(field.SemanticType);
        Assert.False(field.HasSemanticType);
        Assert.Contains(AnonymousFunctionEnvironmentUncertainty.CaptureTypeUnavailable, plan.Uncertainties);
        Assert.Equal(AnonymousFunctionEnvironmentLifetimeCandidate.Unknown, plan.LifetimeCandidate);
        Assert.False(plan.HasVerifiedLayout);
    }

    [Fact]
    public void IncompleteInheritedReceiverLookupMakesThePlanUnknown()
    {
        var sources = new List<string> { "module app", "open class C0 { val far: int = 1 }" };
        for (int index = 1; index <= 132; index++)
        { sources.Add($"open class C{index}: C{index - 1} {{ }}"); }
        sources.Add("class Leaf: C132 { func use(): int { val callback = func(): int { far }; callback() } }");
        var analysis = Analyze(string.Join("\n", sources));
        var plan = AnonymousFunctionEnvironmentPlanner.Create(analysis, AnonymousFunction(analysis),
            TestContext.Current.CancellationToken);
        var receiver = Assert.Single(plan.Fields);

        Assert.Equal(AnonymousFunctionEnvironmentFieldKind.Receiver, receiver.Kind);
        Assert.False(receiver.Receiver!.IsComplete);
        Assert.Contains(AnonymousFunctionEnvironmentUncertainty.ReceiverLookupIncomplete, plan.Uncertainties);
        Assert.Equal(AnonymousFunctionEnvironmentLifetimeCandidate.Unknown, plan.LifetimeCandidate);
        Assert.False(plan.HasVerifiedAbi);
        Assert.False(plan.HasVerifiedLayout);
    }

    [Fact]
    public void EscapingAnonymousFunctionRetainsClassReceiverWithoutMisclassifyingItAsBorrowed()
    {
        var analysis = Analyze("module app\nclass Box(val value: int) {\n"
            + "func create(): () -> int { return func(): int { this.value } }\n} ");
        var plan = AnonymousFunctionEnvironmentPlanner.Create(analysis, AnonymousFunction(analysis),
            TestContext.Current.CancellationToken);
        var receiver = Assert.Single(plan.Fields);

        Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape, plan.Escape.Disposition);
        Assert.Equal(CallableEnvironmentFieldStorage.ReceiverCapture, receiver.Storage);
        Assert.Equal("app.Box", receiver.SemanticType!.Name);
        Assert.False(receiver.SemanticType.IsRefLike);
        Assert.DoesNotContain(AnonymousFunctionEnvironmentUncertainty.EscapingBorrowedCapture,
            plan.Uncertainties);
        Assert.Equal(AnonymousFunctionEnvironmentLifetimeCandidate.Heap, plan.LifetimeCandidate);
        Assert.False(plan.HasVerifiedAbi);
        Assert.False(plan.HasVerifiedLayout);
    }

    [Fact]
    public void EscapingCaptureFreeAnonymousFunctionDoesNotInventAnEnvironment()
    {
        var analysis = Analyze("module app\nfunc create(): () -> int { return func(): int { 42 } }");
        var plan = AnonymousFunctionEnvironmentPlanner.Create(analysis, AnonymousFunction(analysis),
            TestContext.Current.CancellationToken);

        AssertNoErrors(analysis);
        Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape, plan.Escape.Disposition);
        Assert.Empty(plan.Fields);
        Assert.Equal(AnonymousFunctionEnvironmentLifetimeCandidate.None, plan.LifetimeCandidate);
        Assert.True(plan.CandidateFactsComplete);
    }

    [Fact]
    public void ReturnedCaptureFreeLambdaDoesNotNeedAHeapEnvironmentCandidate()
    {
        var analysis = Analyze("module app\nfunc create(): () -> int = { 42 }");
        var plan = LambdaEnvironmentPlanner.Create(analysis, Lambda(analysis),
            TestContext.Current.CancellationToken);

        AssertNoErrors(analysis);
        Assert.Equal(LambdaEscapeDisposition.MayEscape, plan.Escape.Disposition);
        Assert.Empty(plan.Fields);
        Assert.Equal(LambdaEnvironmentLifetimeCandidate.None, plan.LifetimeCandidate);
        Assert.True(plan.CandidateFactsComplete);
    }

    [Fact]
    public void EscapingAnonymousFunctionWithRefLikeReceiverStaysUnknown()
    {
        var analysis = Analyze("module app\nref struct Buffer {\n"
            + "val length: int = 4\n"
            + "func create(): () -> int { return func(): int { this.length } }\n}");
        var plan = AnonymousFunctionEnvironmentPlanner.Create(analysis, AnonymousFunction(analysis),
            TestContext.Current.CancellationToken);
        var receiver = Assert.Single(plan.Fields);

        Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape, plan.Escape.Disposition);
        Assert.Equal(CallableEnvironmentFieldStorage.ReceiverCapture, receiver.Storage);
        Assert.True(receiver.SemanticType!.IsRefLike);
        Assert.Contains(AnonymousFunctionEnvironmentUncertainty.EscapingRefLikeReceiver, plan.Uncertainties);
        Assert.Equal(AnonymousFunctionEnvironmentLifetimeCandidate.Unknown, plan.LifetimeCandidate);
    }

    [Fact]
    public void LambdaPlannerCombinesReceiverAndLocalFieldsInStableOrder()
    {
        var analysis = Analyze("module app\nclass Box(val base: int) {\n"
            + "func make(seed: int): () -> int = { base + seed }\n}");
        var lambda = Lambda(analysis);
        var plan = LambdaEnvironmentPlanner.Create(analysis, lambda,
            TestContext.Current.CancellationToken);

        AssertNoErrors(analysis);
        Assert.Equal(LambdaEscapeDisposition.MayEscape, plan.Escape.Disposition);
        Assert.Equal(LambdaEnvironmentLifetimeCandidate.Heap, plan.LifetimeCandidate);
        Assert.DoesNotContain(LambdaEnvironmentUncertainty.EscapingBorrowedCapture, plan.Uncertainties);
        Assert.Collection(plan.Fields,
            receiver =>
            {
                Assert.Equal(0, receiver.Ordinal);
                Assert.Equal(LambdaEnvironmentFieldKind.Receiver, receiver.Kind);
                Assert.Equal(CallableEnvironmentFieldStorage.ReceiverCapture, receiver.Storage);
                Assert.Equal("app.Box", receiver.SemanticType!.Name);
                Assert.True(receiver.Receiver!.IsComplete);
            },
            local =>
            {
                Assert.Equal(1, local.Ordinal);
                Assert.Equal(LambdaEnvironmentFieldKind.LocalCapture, local.Kind);
                Assert.Equal("seed", local.Local!.Name);
                Assert.Equal("int", local.SemanticType!.Name);
            });
        Assert.False(plan.HasVerifiedAbi);
        Assert.False(plan.HasVerifiedLayout);
    }

    [Fact]
    public void ImmediateLambdaUsesValueCopyAndSharedCellCandidates()
    {
        var analysis = Analyze("module app\nfunc run(seed: int): int {\n"
            + "var state = seed\n"
            + "val snapshot = seed\n"
            + "({ item: int -> state = state + item; snapshot + state })(1)\n}");
        var plan = LambdaEnvironmentPlanner.Create(analysis, Lambda(analysis),
            TestContext.Current.CancellationToken);

        AssertNoErrors(analysis);
        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, plan.Escape.Disposition);
        Assert.Equal(LambdaEnvironmentLifetimeCandidate.Stack, plan.LifetimeCandidate);
        Assert.Equal(new[] { "state", "snapshot" }, plan.Fields.Select(field => field.Local!.Name));
        Assert.Equal(new[] { CallableEnvironmentFieldStorage.SharedMutableCell, CallableEnvironmentFieldStorage.ValueCopy },
            plan.Fields.Select(field => field.Storage));
        Assert.All(plan.Fields, field => Assert.Equal("int", field.SemanticType!.Name));
        Assert.True(plan.CandidateFactsComplete);
    }

    [Fact]
    public void LambdaPlanUsesItsOwnBodySummaryWhenAnotherCallableHasAnError()
    {
        var analysis = Analyze("module app\nfunc valid(): int { "
            + "val callback: () -> int = { 42 }; callback() }\n"
            + "func invalid(): int { val callback = { unresolvedName }; 0 }");
        var lambdas = analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstLambda>().OrderBy(lambda => lambda.Origin.Span.Start).ToArray();
        var validLambda = Assert.Single(lambdas, lambda =>
            analysis.GetLambdaBody(lambda)?.IsComplete == true);
        var plan = LambdaEnvironmentPlanner.Create(analysis, validLambda,
            TestContext.Current.CancellationToken);

        Assert.True(analysis.HasErrors);
        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(LambdaEnvironmentUncertainty.BodyAnalysisIncomplete, plan.Uncertainties);
        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, plan.Escape.Disposition);
        Assert.Equal(LambdaEnvironmentLifetimeCandidate.None, plan.LifetimeCandidate);
    }

    [Fact]
    public void EscapingLambdaWithBorrowedRefCaptureRemainsUnknown()
    {
        var analysis = Analyze("module app\nfunc create(ref value: int): () -> int = { value }");
        var plan = LambdaEnvironmentPlanner.Create(analysis, Lambda(analysis),
            TestContext.Current.CancellationToken);
        var field = Assert.Single(plan.Fields);

        Assert.Equal(LambdaEscapeDisposition.MayEscape, plan.Escape.Disposition);
        Assert.Equal(CallableEnvironmentFieldStorage.BorrowedLocation, field.Storage);
        Assert.Equal(LambdaEnvironmentLifetimeCandidate.Unknown, plan.LifetimeCandidate);
        Assert.Contains(LambdaEnvironmentUncertainty.EscapingBorrowedCapture, plan.Uncertainties);
        Assert.False(plan.HasVerifiedLayout);
    }

    [Fact]
    public void EscapingLambdaWithRefLikeReceiverStaysUnknown()
    {
        var analysis = Analyze("module app\nref struct Buffer {\n"
            + "val length: int = 4\n"
            + "func create(): () -> int = { length }\n}");
        var plan = LambdaEnvironmentPlanner.Create(analysis, Lambda(analysis),
            TestContext.Current.CancellationToken);
        var receiver = Assert.Single(plan.Fields);

        Assert.Equal(LambdaEscapeDisposition.MayEscape, plan.Escape.Disposition);
        Assert.Equal(CallableEnvironmentFieldStorage.ReceiverCapture, receiver.Storage);
        Assert.True(receiver.SemanticType!.IsRefLike);
        Assert.Contains(LambdaEnvironmentUncertainty.EscapingRefLikeReceiver, plan.Uncertainties);
        Assert.Equal(LambdaEnvironmentLifetimeCandidate.Unknown, plan.LifetimeCandidate);
    }

    [Fact]
    public void LambdaCaptureWithoutResolvedTypeProducesUnknownLifetime()
    {
        var analysis = Analyze("module app\nfunc run(): int {\n"
            + "val unresolved = missingValue\n"
            + "val callback = { unresolved }\n"
            + "callback()\n}");
        var plan = LambdaEnvironmentPlanner.Create(analysis, Lambda(analysis),
            TestContext.Current.CancellationToken);
        var field = Assert.Single(plan.Fields);

        Assert.Null(analysis.GetLocalType(field.Local!));
        Assert.Null(field.SemanticType);
        Assert.Contains(LambdaEnvironmentUncertainty.CaptureTypeUnavailable, plan.Uncertainties);
        Assert.Equal(LambdaEnvironmentLifetimeCandidate.Unknown, plan.LifetimeCandidate);
        Assert.False(plan.HasVerifiedAbi);
        Assert.False(plan.HasVerifiedLayout);
    }

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session(source);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static AstAnonymousFunction AnonymousFunction(BodyAnalysis analysis) =>
        Assert.Single(analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstAnonymousFunction>());

    private static AstLambda Lambda(BodyAnalysis analysis) =>
        Assert.Single(analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstLambda>());

    private static void AssertNoErrors(BodyAnalysis analysis)
    {
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.True(analysis.IsComplete);
    }
}
