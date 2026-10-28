// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LambdaEscapeFlowBoundaryTests
{
    private static LexicalAnalysis Analyze(string source)
        => LexicalAnalysis.Create(AstTestSources.Session(source), TestContext.Current.CancellationToken);

    private static LambdaEscape Capturing(LexicalAnalysis analysis, string local)
        => Assert.Single(analysis.LambdaEscapes.Where(escape =>
            escape.Captures.Any(capture => capture.Local.Name == local)));

    [Fact]
    public void ArrayLiteralIsAnEscapeCapableAggregateBoundary()
    {
        var analysis = Analyze("module app\nfunc f(seed: int) { val callbacks = [{ seed }] }");
        var escape = Capturing(analysis, "seed");

        Assert.Equal(LambdaEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Equal(LambdaEscapeUseKind.AggregateElement, Assert.Single(escape.Uses).Kind);
    }

    [Fact]
    public void GroupedImmutableAliasPropagatesToItsInvocation()
    {
        var analysis = Analyze("module app\nfunc f(seed: int) { val first = { seed }; "
            + "val second = ((first)); second() }");
        var escape = Capturing(analysis, "seed");

        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.LocalAlias);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.LocalInvocation);
        Assert.Equal(2, escape.Uses.Count(use => use.Kind == LambdaEscapeUseKind.LocalStorage));
    }

    [Fact]
    public void LocalInvocationDoesNotEraseLaterPublicationThroughAnAlias()
    {
        var analysis = Analyze("module app\nfunc publish(@escaping callback: () -> int): () {}\n"
            + "func f(seed: int) { val first = { seed }; val second = first; second(); publish(first) }");
        var escape = Capturing(analysis, "seed");

        Assert.Equal(LambdaEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.LocalInvocation);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.CallArgument);
        Assert.Equal(escape.Uses.OrderBy(use => use.Site.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(use => use.Site.Origin.Span.Start).ThenBy(use => use.Kind), escape.Uses);
    }

    [Fact]
    public void MutableBranchJoinKeepsEachAssignedLambdaAsAMayAlias()
    {
        var analysis = Analyze("module app\nfunc f(flag: bool, first: int, second: int) { "
            + "var callback = { first }; if flag { callback = { second } }; callback() }");
        var captures = analysis.LambdaEscapes.Where(escape => escape.Captures.Any(capture =>
            capture.Local.Name is "first" or "second")).ToArray();

        Assert.Equal(2, captures.Length);
        Assert.All(captures, escape =>
        {
            Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
            Assert.DoesNotContain(escape.Uses, use => use.Kind == LambdaEscapeUseKind.AliasMutation);
            Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.LocalInvocation);
        });
        Assert.Equal("first", Assert.Single(captures.Single(escape => escape.Captures.Any(capture =>
            capture.Local.Name == "first")).Captures).Local.Name);
        Assert.Equal("second", Assert.Single(captures.Single(escape => escape.Captures.Any(capture =>
            capture.Local.Name == "second")).Captures).Local.Name);
    }

    [Fact]
    public void ReassignmentFromLambdaToLambdaRetainsBothPossibleCaptureIdentities()
    {
        var analysis = Analyze("module app\nfunc f(first: int, second: int) { "
            + "var callback = { first }; callback = { second }; callback() }");
        var captures = analysis.LambdaEscapes.Where(escape => escape.Captures.Any(capture =>
            capture.Local.Name is "first" or "second")).ToArray();

        Assert.Equal(2, captures.Length);
        Assert.All(captures, escape => Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition));
        Assert.Contains(captures, escape => escape.Captures.Any(capture => capture.Local.Name == "first"));
        Assert.Contains(captures, escape => escape.Captures.Any(capture => capture.Local.Name == "second"));
    }

    [Fact]
    public void AliasOfMutableAliasUsesTheConservativeIdentityUnion()
    {
        var analysis = Analyze("module app\nfunc f(seed: int) { var callback = { seed }; "
            + "val stableName = callback; stableName() }");
        var escape = Capturing(analysis, "seed");

        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.DoesNotContain(escape.Uses, use => use.Kind == LambdaEscapeUseKind.AliasMutation);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.LocalInvocation);
    }

    [Fact]
    public void AliasCapturedByNestedLambdaRemainsUnknownDespiteOuterLocalCall()
    {
        var analysis = Analyze("module app\nfunc f(seed: int) { val callback = { seed }; "
            + "val invokeLater = { callback() }; invokeLater() }");
        var escape = Capturing(analysis, "seed");

        Assert.Equal(LambdaEscapeDisposition.Unknown, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.CallableCapture);
    }

    [Fact]
    public void ReturningAliasFromNestedCallableRemainsEscapeCapable()
    {
        var analysis = Analyze("module app\nfunc create(seed: int): () -> int { "
            + "val callback = { seed }; return callback }");
        var escape = Capturing(analysis, "seed");

        Assert.Equal(LambdaEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.ReturnValue);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.LocalStorage);
    }

    [Fact]
    public void LocalFunctionReturningAliasRemainsAnEscapeBoundary()
    {
        var analysis = Analyze("module app\nfunc create(seed: int): () -> int { "
            + "val callback = { seed }; func relay(): () -> int = callback; return relay() }");
        var escape = Capturing(analysis, "seed");

        Assert.Equal(LambdaEscapeDisposition.Unknown, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.CallableCapture);
    }

    [Fact]
    public void ArrayAndTuplePublicationWitnessesAreOrderedAndDistinct()
    {
        var analysis = Analyze("module app\nfunc f(seed: int) { val callback = { seed }; "
            + "val array = [callback]; val tuple = (callback, 1) }");
        var escape = Capturing(analysis, "seed");

        Assert.Equal(LambdaEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.AggregateElement);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.LocalStorage);
        Assert.Equal(escape.Uses.OrderBy(use => use.Site.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(use => use.Site.Origin.Span.Start).ThenBy(use => use.Kind), escape.Uses);
        Assert.Equal(escape.Uses.Select(use => (use.Site.Origin.Span.Start, use.Kind)).Distinct().Count(),
            escape.Uses.Length);
    }

    [Fact]
    public void NoncapturingLambdaInLocalArrayIsStillTrackedAsAValue()
    {
        var analysis = Analyze("module app\nfunc f() { val callbacks = [{ 1 }]; callbacks }");
        var escape = Assert.Single(analysis.LambdaEscapes);

        Assert.Empty(escape.Captures);
        Assert.Equal(LambdaEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Equal(LambdaEscapeUseKind.AggregateElement, Assert.Single(escape.Uses).Kind);
    }

    [Fact]
    public void ForeignLambdaCannotBeQueriedThroughAnotherEscapeSnapshot()
    {
        const string source = "module app\nfunc f(seed: int) { val callback = { seed }; callback() }";
        var firstSession = AstTestSources.Session(source);
        var secondSession = AstTestSources.Session(source);
        var first = LexicalAnalysis.Create(firstSession, TestContext.Current.CancellationToken);
        var second = LexicalAnalysis.Create(secondSession, TestContext.Current.CancellationToken);
        var foreign = second.LambdaEscapes.Single().Lambda;

        Assert.Throws<ArgumentException>(() => first.GetLambdaEscape(foreign));
        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping,
            first.GetLambdaEscape(first.LambdaEscapes.Single().Lambda).Disposition);
    }
}
