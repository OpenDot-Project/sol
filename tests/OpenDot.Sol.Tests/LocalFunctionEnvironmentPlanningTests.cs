// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LocalFunctionEnvironmentPlanningTests
{
    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session(source);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static AstFunction Function(BodyAnalysis analysis, string name) => Assert.Single(
        analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes).OfType<AstFunction>(),
        function => function.Name == name);

    [Fact]
    public void DirectCallRetainsOrderedFieldsWhileCapturedBodyFlowIsIncomplete()
    {
        var analysis = Analyze("module app\nfunc run(seed: int): int { var state = seed; "
            + "val snapshot = seed; func nested(): int { state += 1; snapshot + state }; nested() }");
        var function = Function(analysis, "nested");
        var plan = LocalFunctionEnvironmentPlanner.Create(analysis, function,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalFunctionEscapeDisposition.ProvenNonEscaping, plan.Escape.Disposition);
        Assert.Contains(LocalFunctionEnvironmentUncertainty.BodyAnalysisIncomplete, plan.Uncertainties);
        Assert.Equal(LocalFunctionEnvironmentLifetimeCandidate.Unknown, plan.LifetimeCandidate);
        Assert.Equal(new[] { "state", "snapshot" }, plan.Fields.Select(field => field.Local!.Name));
        Assert.Equal(new[] { 0, 1 }, plan.Fields.Select(field => field.Ordinal));
        Assert.Equal(new[] { CallableEnvironmentFieldStorage.SharedMutableCell,
            CallableEnvironmentFieldStorage.ValueCopy },
            plan.Fields.Select(field => field.Storage));
        Assert.All(plan.Fields, field => Assert.Equal("int", field.SemanticType!.Name));
        Assert.False(plan.HasVerifiedAbi);
        Assert.False(plan.HasVerifiedLayout);
    }

    [Fact]
    public void ReturningNamedFunctionWithACompleteCaptureBodyGetsAHeapCandidate()
    {
        var analysis = Analyze("module app\nfunc create(seed: int): () -> int { "
            + "func read(): int = seed; return read }");
        var function = Function(analysis, "read");
        var plan = LocalFunctionEnvironmentPlanner.Create(analysis, function,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, plan.Escape.Disposition);
        Assert.Equal(LocalFunctionEnvironmentLifetimeCandidate.Heap, plan.LifetimeCandidate);
        Assert.DoesNotContain(LocalFunctionEnvironmentUncertainty.BodyAnalysisIncomplete, plan.Uncertainties);
        Assert.Collection(plan.Fields, field =>
        {
            Assert.Equal(LocalFunctionEnvironmentFieldKind.LocalCapture, field.Kind);
            Assert.Equal("seed", field.Local!.Name);
            Assert.Equal(CallableEnvironmentFieldStorage.ValueCopy, field.Storage);
            Assert.Equal("int", field.SemanticType!.Name);
        });
        Assert.False(plan.HasVerifiedAbi);
        Assert.False(plan.HasVerifiedLayout);
    }

    [Fact]
    public void CompleteDirectInvocationWithoutCapturesNeedsNoEnvironmentCandidate()
    {
        var analysis = Analyze("module app\nfunc run(): int { func nested(): int = 41; nested() }");
        var function = Function(analysis, "nested");
        var plan = LocalFunctionEnvironmentPlanner.Create(analysis, function,
            TestContext.Current.CancellationToken);

        Assert.Equal(BodyBindingStatus.Bound, analysis.GetBody(function)!.Status);
        Assert.Equal(LocalFunctionEscapeDisposition.ProvenNonEscaping, plan.Escape.Disposition);
        Assert.Empty(plan.Fields);
        Assert.True(plan.CandidateFactsComplete);
        Assert.Equal(LocalFunctionEnvironmentLifetimeCandidate.None, plan.LifetimeCandidate);
        Assert.False(plan.HasVerifiedAbi);
        Assert.False(plan.HasVerifiedLayout);
    }

    [Fact]
    public void ReturnedCaptureFreeLocalFunctionDoesNotInventAHeapEnvironment()
    {
        var analysis = Analyze("module app\nfunc create(): () -> int { "
            + "func read(): int = 42; return read }");
        var plan = LocalFunctionEnvironmentPlanner.Create(analysis, Function(analysis, "read"),
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, plan.Escape.Disposition);
        Assert.Empty(plan.Fields);
        Assert.Equal(LocalFunctionEnvironmentLifetimeCandidate.None, plan.LifetimeCandidate);
        Assert.True(plan.CandidateFactsComplete);
    }

    [Fact]
    public void CompleteLocalAliasInvocationRetainsBorrowedStorageOnTheStackCandidate()
    {
        var analysis = Analyze("module app\nfunc run(ref value: int): int { "
            + "func read(): int = value; val alias = read; alias() }");
        var function = Function(analysis, "read");
        var plan = LocalFunctionEnvironmentPlanner.Create(analysis, function,
            TestContext.Current.CancellationToken);
        var field = Assert.Single(plan.Fields);

        Assert.Equal(LocalFunctionEscapeDisposition.ProvenNonEscaping, plan.Escape.Disposition);
        Assert.Equal(CallableEnvironmentFieldStorage.BorrowedLocation, field.Storage);
        Assert.Equal(LocalFunctionEnvironmentLifetimeCandidate.Stack, plan.LifetimeCandidate);
        Assert.True(plan.CandidateFactsComplete);
        Assert.False(plan.HasVerifiedAbi);
        Assert.False(plan.HasVerifiedLayout);
    }

    [Fact]
    public void ReturningAnAliasUsesTheSameHeapCandidateAsReturningTheFunctionName()
    {
        var analysis = Analyze("module app\nfunc create(seed: int): () -> int { "
            + "func read(): int = seed; val alias = read; return alias }");
        var function = Function(analysis, "read");
        var plan = LocalFunctionEnvironmentPlanner.Create(analysis, function,
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, plan.Escape.Disposition);
        Assert.Equal(LocalFunctionEnvironmentLifetimeCandidate.Heap, plan.LifetimeCandidate);
        Assert.Contains(plan.Escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.LocalStorage);
        Assert.Contains(plan.Escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.ReturnValue);
        Assert.Equal("seed", Assert.Single(plan.Fields).Local!.Name);
        Assert.False(plan.HasVerifiedAbi);
        Assert.False(plan.HasVerifiedLayout);
    }

    [Fact]
    public void ReturningAnAliasOfBorrowedStorageKeepsThePlanUnknown()
    {
        var analysis = Analyze("module app\nfunc create(ref value: int): () -> int { "
            + "func read(): int = value; val alias = read; return alias }");
        var plan = LocalFunctionEnvironmentPlanner.Create(analysis, Function(analysis, "read"),
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, plan.Escape.Disposition);
        Assert.Equal(CallableEnvironmentFieldStorage.BorrowedLocation,
            Assert.Single(plan.Fields).Storage);
        Assert.Contains(LocalFunctionEnvironmentUncertainty.EscapingBorrowedCapture, plan.Uncertainties);
        Assert.Equal(LocalFunctionEnvironmentLifetimeCandidate.Unknown, plan.LifetimeCandidate);
    }

    [Fact]
    public void AliasedMutableCaptureRemainsASharedCellWhenBodyFlowIsIncomplete()
    {
        var analysis = Analyze("module app\nfunc run(seed: int): int { var count = seed; "
            + "func read(): int = count; val alias = read; alias() }");
        var plan = LocalFunctionEnvironmentPlanner.Create(analysis, Function(analysis, "read"),
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalFunctionEscapeDisposition.ProvenNonEscaping, plan.Escape.Disposition);
        Assert.Equal(LocalFunctionEnvironmentLifetimeCandidate.Unknown, plan.LifetimeCandidate);
        Assert.Contains(LocalFunctionEnvironmentUncertainty.BodyAnalysisIncomplete, plan.Uncertainties);
        Assert.Equal(CallableEnvironmentFieldStorage.SharedMutableCell,
            Assert.Single(plan.Fields).Storage);
    }

    [Fact]
    public void EscapingBorrowedCaptureKeepsLifetimeUnknown()
    {
        var analysis = Analyze("module app\nfunc create(ref value: int): () -> int { "
            + "func read(): int = value; return read }");
        var function = Function(analysis, "read");
        var plan = LocalFunctionEnvironmentPlanner.Create(analysis, function,
            TestContext.Current.CancellationToken);
        var field = Assert.Single(plan.Fields);

        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, plan.Escape.Disposition);
        Assert.Equal(CallableEnvironmentFieldStorage.BorrowedLocation, field.Storage);
        Assert.Contains(LocalFunctionEnvironmentUncertainty.EscapingBorrowedCapture, plan.Uncertainties);
        Assert.Equal(LocalFunctionEnvironmentLifetimeCandidate.Unknown, plan.LifetimeCandidate);
        Assert.False(plan.HasVerifiedLayout);
    }

    [Fact]
    public void EscapingRefLikeCaptureKeepsLifetimeUnknown()
    {
        var analysis = Analyze("module app\nref struct Buffer { val length: int }\n"
            + "func create(buffer: Buffer): () -> int { "
            + "func read(): int = buffer.length; return read }");
        var function = Function(analysis, "read");
        var plan = LocalFunctionEnvironmentPlanner.Create(analysis, function,
            TestContext.Current.CancellationToken);
        var field = Assert.Single(plan.Fields);

        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, plan.Escape.Disposition);
        Assert.True(field.SemanticType!.IsRefLike);
        Assert.Contains(LocalFunctionEnvironmentUncertainty.EscapingRefLikeCapture, plan.Uncertainties);
        Assert.Equal(LocalFunctionEnvironmentLifetimeCandidate.Unknown, plan.LifetimeCandidate);
    }

    [Fact]
    public void ReceiverDependentLocalFunctionPlansASeparateReceiverField()
    {
        var analysis = Analyze("module app\nclass Owner(val value: int) { "
            + "func create(): () -> int { func read(): int = value; return read } }");
        var function = Function(analysis, "read");
        var plan = LocalFunctionEnvironmentPlanner.Create(analysis, function,
            TestContext.Current.CancellationToken);
        var field = Assert.Single(plan.Fields);

        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, plan.Escape.Disposition);
        Assert.Equal(LocalFunctionEnvironmentFieldKind.Receiver, field.Kind);
        Assert.Equal(CallableEnvironmentFieldStorage.ReceiverCapture, field.Storage);
        Assert.Equal("app.Owner", field.SemanticType!.Name);
        Assert.Equal("Owner", field.Receiver!.Owner.Name);
        Assert.Equal(LocalFunctionEnvironmentLifetimeCandidate.Heap, plan.LifetimeCandidate);
        Assert.True(plan.CandidateFactsComplete);
        Assert.False(plan.HasVerifiedAbi);
        Assert.False(plan.HasVerifiedLayout);
    }

    [Fact]
    public void ReceiverAndLocalCapturesHaveDistinctOrderedFields()
    {
        var analysis = Analyze("module app\nclass Owner(val value: int) { func create(seed: int): () -> int { "
            + "func read(): int = value + seed; return read } }");
        var plan = LocalFunctionEnvironmentPlanner.Create(analysis, Function(analysis, "read"),
            TestContext.Current.CancellationToken);

        Assert.Equal(new[] { LocalFunctionEnvironmentFieldKind.Receiver,
            LocalFunctionEnvironmentFieldKind.LocalCapture }, plan.Fields.Select(field => field.Kind));
        Assert.Equal(CallableEnvironmentFieldStorage.ReceiverCapture, plan.Fields[0].Storage);
        Assert.Equal("Owner", plan.Fields[0].Receiver!.Owner.Name);
        Assert.Equal("seed", plan.Fields[1].Local!.Name);
        Assert.Equal(CallableEnvironmentFieldStorage.ValueCopy, plan.Fields[1].Storage);
        Assert.Equal(LocalFunctionEnvironmentLifetimeCandidate.Heap, plan.LifetimeCandidate);
    }

    [Fact]
    public void ReceiverRequirementFlowsIntoEveryNestedLocalFunctionEnvironmentPlan()
    {
        var analysis = Analyze("module app\nclass Owner(val value: int) { func run(): int { "
            + "func middle(): int { func inner(): int = value; inner() }; middle() } }");
        var middle = LocalFunctionEnvironmentPlanner.Create(analysis, Function(analysis, "middle"),
            TestContext.Current.CancellationToken);
        var inner = LocalFunctionEnvironmentPlanner.Create(analysis, Function(analysis, "inner"),
            TestContext.Current.CancellationToken);

        Assert.Equal(CallableEnvironmentFieldStorage.ReceiverCapture, Assert.Single(middle.Fields).Storage);
        Assert.Equal(CallableEnvironmentFieldStorage.ReceiverCapture, Assert.Single(inner.Fields).Storage);
        Assert.Equal("Owner", middle.Fields[0].Receiver!.Owner.Name);
        Assert.Same(middle.Fields[0].Receiver!.References[0], inner.Fields[0].Receiver!.References[0]);
    }

    [Fact]
    public void EscapingRefLikeReceiverKeepsTheEnvironmentLifetimeUnknown()
    {
        var analysis = Analyze("module app\nref struct Owner { val value: int = 1; func create(): () -> int { "
            + "func read(): int = value; return read } }");
        var plan = LocalFunctionEnvironmentPlanner.Create(analysis, Function(analysis, "read"),
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, plan.Escape.Disposition);
        Assert.True(Assert.Single(plan.Fields).SemanticType!.IsRefLike);
        Assert.Contains(LocalFunctionEnvironmentUncertainty.EscapingRefLikeReceiver, plan.Uncertainties);
        Assert.DoesNotContain(LocalFunctionEnvironmentUncertainty.EscapingRefLikeCapture, plan.Uncertainties);
        Assert.Equal(LocalFunctionEnvironmentLifetimeCandidate.Unknown, plan.LifetimeCandidate);
    }

    [Fact]
    public void NestedLocalFunctionCaptureRetainsCallableStorageAndUnknownTypeGap()
    {
        var analysis = Analyze("module app\nfunc run(seed: int): int { "
            + "func helper(): int = seed; func invoke(): int = helper(); invoke() }");
        var function = Function(analysis, "invoke");
        var plan = LocalFunctionEnvironmentPlanner.Create(analysis, function,
            TestContext.Current.CancellationToken);
        var field = Assert.Single(plan.Fields);

        Assert.Equal("helper", field.Local!.Name);
        Assert.Equal(LocalFunctionEnvironmentFieldKind.LocalCapture, field.Kind);
        Assert.Equal(CallableEnvironmentFieldStorage.NestedCallableEnvironment, field.Storage);
        Assert.Null(field.SemanticType);
        Assert.Contains(LocalFunctionEnvironmentUncertainty.CaptureTypeUnavailable, plan.Uncertainties);
        Assert.Equal(LocalFunctionEnvironmentLifetimeCandidate.Unknown, plan.LifetimeCandidate);
        Assert.False(plan.HasVerifiedAbi);
        Assert.False(plan.HasVerifiedLayout);
    }

    [Fact]
    public void CreateReturnsEveryLocalFunctionAndRejectsTopLevelQueries()
    {
        var analysis = Analyze("module app\nfunc top() {}\nfunc outer() { func first() {}; func second() {} }");
        var plans = LocalFunctionEnvironmentPlanner.Create(analysis, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "first", "second" }, plans.Select(plan => plan.Function.Name));
        Assert.All(plans, plan => Assert.Same(plan.FunctionLocal,
            plan.Escape.Local));
        Assert.Throws<ArgumentException>(() => LocalFunctionEnvironmentPlanner.Create(analysis,
            Function(analysis, "top"), TestContext.Current.CancellationToken));
    }
}
