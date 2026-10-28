// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LambdaEscapeAnalysisTests
{
    private static LexicalAnalysis Analyze(string source)
        => LexicalAnalysis.Create(AstTestSources.Session(source), TestContext.Current.CancellationToken);

    [Fact]
    public void ImmediateInvocationIsTheOnlyProvenNonEscapingUse()
    {
        var analysis = Analyze("module app\nfunc f(seed: int) { val result = ({ seed })() }");
        var escape = Assert.Single(analysis.LambdaEscapes);

        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Equal(LambdaEscapeUseKind.ImmediateInvocation, Assert.Single(escape.Uses).Kind);
        Assert.Equal("seed", Assert.Single(escape.Captures).Local.Name);
    }

    [Fact]
    public void ParenthesizedAndLabelledImmediateLambdasKeepTheInvocationProof()
    {
        var analysis = Analyze("module app\nfunc f() { val first = (({ 1 }))(); val second = run@ { 2 }() }");

        Assert.Equal(2, analysis.LambdaEscapes.Length);
        Assert.All(analysis.LambdaEscapes, escape =>
        {
            Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
            Assert.Equal(LambdaEscapeUseKind.ImmediateInvocation, Assert.Single(escape.Uses).Kind);
        });
    }

    [Fact]
    public void PassingLambdaToAnEscapingParameterIsEscapeCapable()
    {
        var analysis = Analyze("module app\nfunc consume(@escaping action: () -> int) {}\n"
            + "func f(seed: int) { consume({ seed }) }");
        var escape = Assert.Single(analysis.LambdaEscapes);

        Assert.Equal(LambdaEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Equal(LambdaEscapeUseKind.CallArgument, Assert.Single(escape.Uses).Kind);
    }

    [Fact]
    public void ReturningLambdaFromExpressionBodyIsEscapeCapable()
    {
        var analysis = Analyze("module app\nfunc create(seed: int) = { seed }");
        var escape = Assert.Single(analysis.LambdaEscapes);

        Assert.Equal(LambdaEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Equal(LambdaEscapeUseKind.ReturnValue, Assert.Single(escape.Uses).Kind);
    }

    [Fact]
    public void ReturningLambdaFromBlockTailIsEscapeCapable()
    {
        var analysis = Analyze("module app\nfunc create(seed: int): () -> int { { seed } }");
        var escape = Assert.Single(analysis.LambdaEscapes);

        Assert.Equal(LambdaEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Equal(LambdaEscapeUseKind.ReturnValue, Assert.Single(escape.Uses).Kind);
    }

    [Fact]
    public void PassingLocalAliasToFunctionIsEscapeCapable()
    {
        var analysis = Analyze("module app\nfunc run(@escaping callback: () -> int) {}\n"
            + "func f(seed: int) { val callback = { seed }; run(callback) }");
        var escape = Assert.Single(analysis.LambdaEscapes);

        Assert.Equal(LambdaEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.LocalStorage);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.CallArgument);
    }

    [Fact]
    public void ImmutableAliasChainUsedOnlyForLocalInvocationProvesNonEscape()
    {
        var analysis = Analyze("module app\nfunc f(seed: int) { val first = { seed }; val second = first; second() }");
        var escape = Assert.Single(analysis.LambdaEscapes);

        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.LocalAlias);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.LocalInvocation);
        Assert.Equal("seed", Assert.Single(escape.Captures).Local.Name);
    }

    [Fact]
    public void TwoAliasesOfOneLambdaRemainOneCaptureIdentity()
    {
        var analysis = Analyze("module app\nfunc f(seed: int) { val first = { seed }; "
            + "val left = first; val right = first; left(); right() }");
        var escape = Assert.Single(analysis.LambdaEscapes);

        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Single(escape.Captures.Select(capture => capture.Local.Id).Distinct());
        Assert.Equal(2, escape.Uses.Count(use => use.Kind == LambdaEscapeUseKind.LocalInvocation));
    }

    [Fact]
    public void TrailingLambdaArgumentIsEscapeCapable()
    {
        var analysis = Analyze("module app\nfunc consume(@escaping action: () -> int) {}\n"
            + "func f(seed: int) { consume { seed } }");
        var escape = Assert.Single(analysis.LambdaEscapes);

        Assert.Equal(LambdaEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.CallArgument);
    }

    [Fact]
    public void MemberInitializerIsHeapStorageEvenWithoutAFunctionOwner()
    {
        var analysis = Analyze("module app\nclass Holder { val callback: () -> int = { 1 } }");
        var escape = Assert.Single(analysis.LambdaEscapes);

        Assert.Equal(LambdaEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Equal(LambdaEscapeUseKind.HeapStorage, Assert.Single(escape.Uses).Kind);
    }

    [Fact]
    public void ReturnedNestedClosureRetainsTheOuterLambdaParameter()
    {
        const string source = "module app\nfunc f() { val outer: (int) -> () -> int = { value -> { value } }; "
            + "val inner = outer(1); inner() }";
        var session = AstTestSources.Session(source);
        var analysis = LexicalAnalysis.Create(session, TestContext.Current.CancellationToken);
        var nested = Assert.Single(analysis.LambdaEscapes.Where(escape => escape.Captures
            .Any(capture => capture.Local.Name == "value")));

        Assert.Equal(LambdaEscapeDisposition.MayEscape, nested.Disposition);
        Assert.Contains(nested.Uses, use => use.Kind == LambdaEscapeUseKind.ReturnValue);
    }

    [Fact]
    public void ReturningLocalAliasRemainsEscaping()
    {
        var analysis = Analyze("module app\nfunc create(seed: int): () -> int { val callback = { seed }; return callback }");
        var escape = Assert.Single(analysis.LambdaEscapes);

        Assert.Equal(LambdaEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.ReturnValue);
    }

    [Fact]
    public void TrackedMutableAliasCanProveNonEscapeWhenItIsInvokedLocally()
    {
        var analysis = Analyze("module app\nfunc f(seed: int) { var callback = { seed }; callback() }");
        var escape = Assert.Single(analysis.LambdaEscapes);

        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.DoesNotContain(escape.Uses, use => use.Kind == LambdaEscapeUseKind.AliasMutation);
    }

    [Fact]
    public void AliasUseCapturedByAnotherCallableDoesNotAcquireStackLifetimeProof()
    {
        var analysis = Analyze("module app\nfunc f(seed: int) { val callback = { seed }; val nested = { callback() } }");
        var escape = Assert.Single(analysis.LambdaEscapes.Where(candidate =>
            candidate.Captures.Any(capture => capture.Local.Name == "seed")));

        Assert.Equal(LambdaEscapeDisposition.Unknown, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.CallableCapture);
    }

    [Fact]
    public void AssignmentToAPropertyIsClassifiedAsHeapStorage()
    {
        var analysis = Analyze("module app\nclass Holder { var callback: (() -> int)? }\n"
            + "func f(holder: Holder, seed: int) { holder.callback = { seed } }");
        var escape = Assert.Single(analysis.LambdaEscapes);

        Assert.Equal(LambdaEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Equal(LambdaEscapeUseKind.HeapStorage, Assert.Single(escape.Uses).Kind);
    }

    [Fact]
    public void LambdaInsideTupleIsEscapeCapableAggregateValue()
    {
        var analysis = Analyze("module app\nfunc f(seed: int) { val pair = ({ seed }, 1) }");
        var escape = Assert.Single(analysis.LambdaEscapes);

        Assert.Equal(LambdaEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Equal(LambdaEscapeUseKind.AggregateElement, Assert.Single(escape.Uses).Kind);
    }

    [Fact]
    public void UnknownExpressionConsumerDoesNotAcquireAStackLifetimeProof()
    {
        var analysis = Analyze("module app\nfunc f() { val value = 1 + { 2 } }");
        var escape = Assert.Single(analysis.LambdaEscapes);

        Assert.Equal(LambdaEscapeDisposition.Unknown, escape.Disposition);
        Assert.Equal(LambdaEscapeUseKind.UnsupportedContext, Assert.Single(escape.Uses).Kind);
    }

    [Fact]
    public void EscapeInventoryIsDeterministicAndRetainsSnapshotOwnership()
    {
        const string source = "module app\nfunc f(seed: int) { val local = { seed }; val immediate = ({ seed })() }";
        var firstSession = AstTestSources.Session(source);
        var secondSession = AstTestSources.Session(source);
        var first = LexicalAnalysis.Create(firstSession, TestContext.Current.CancellationToken);
        var second = LexicalAnalysis.Create(secondSession, TestContext.Current.CancellationToken);
        string[] Signature(LexicalAnalysis analysis) => analysis.LambdaEscapes.Select(escape => string.Join("|",
            escape.Lambda.Origin.Span.Start, escape.Disposition, escape.Uses[0].Kind,
            string.Join(",", escape.Captures.Select(capture => capture.Local.Id)))).ToArray();

        Assert.Equal(Signature(first), Signature(second));
        var foreign = secondSession.Files.SelectMany(file => file.Root.Descendants()).OfType<AstLambda>().First();
        Assert.Throws<System.ArgumentException>(() => first.GetLambdaEscape(foreign));
        Assert.NotNull(first.GetLambdaEscape(first.LambdaEscapes[0].Lambda));
    }
}
