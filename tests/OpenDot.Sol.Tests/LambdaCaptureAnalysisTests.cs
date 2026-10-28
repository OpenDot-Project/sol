// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LambdaCaptureAnalysisTests
{
    private static LexicalAnalysis Bind(params string[] files)
        => LexicalAnalysis.Create(AstTestSources.Session(files), TestContext.Current.CancellationToken);

    private static string[] Shape(LexicalAnalysis analysis)
        => analysis.LambdaCaptures.Select(capture => string.Join("|",
            capture.Lambda.Origin.Source.Path,
            capture.Lambda.Origin.Span.Start,
            capture.Local.Name,
            capture.Local.Id,
            capture.Storage,
            capture.Access,
            string.Join(",", capture.References.Select(reference => reference.Origin.Span.Start))))
            .ToArray();

    [Fact]
    public void RepeatedAnalysisProducesTheSameOrderedCaptureInventory()
    {
        const string source = "module app\nfunc f(a:int,b:int) { val first = { a + b }; val second = { b + a } }";
        var session = AstTestSources.Session(source);
        var first = LexicalAnalysis.Create(session, TestContext.Current.CancellationToken);
        var second = LexicalAnalysis.Create(session, TestContext.Current.CancellationToken);

        Assert.Equal(Shape(first), Shape(second));
        Assert.Equal(first.LambdaCaptures.Select(capture => capture.Lambda.Origin.Span.Start)
            .OrderBy(offset => offset), first.LambdaCaptures.Select(capture => capture.Lambda.Origin.Span.Start));
        Assert.All(first.LambdaCaptures.GroupBy(capture => capture.Lambda), group =>
            Assert.Equal(group.Select(capture => capture.Local.Id).OrderBy(id => id),
                group.Select(capture => capture.Local.Id)));
    }

    [Fact]
    public void OneLambdaKeepsDifferentCapturedLocalsAsDifferentEntries()
    {
        var analysis = Bind("module app\nfunc f(left:int,right:int) { val callback = { left + right } }");
        var captures = analysis.LambdaCaptures.OrderBy(capture => capture.Local.Id).ToArray();

        Assert.Equal(new[] { "left", "right" }, captures.Select(capture => capture.Local.Name));
        Assert.Single(captures.Select(capture => capture.Lambda).Distinct());
        Assert.All(captures, capture => Assert.Equal(LambdaCaptureStorage.ValueCopy, capture.Storage));
        Assert.All(captures, capture => Assert.Equal(LambdaCaptureAccess.Read, capture.Access));
    }

    [Fact]
    public void CaptureEntriesAreOwnedByTheirSourceFilesInACombinedSession()
    {
        var analysis = Bind(
            "module app\nfunc first(seed:int) { val callback = { seed } }",
            "module app\nfunc second(seed:int) { val callback = { seed } }");
        var captures = analysis.LambdaCaptures.ToArray();

        Assert.Equal(new[] { "file0.sol", "file1.sol" },
            captures.Select(capture => capture.Lambda.Origin.Source.Path));
        Assert.Equal(new[] { "seed", "seed" }, captures.Select(capture => capture.Local.Name));
        Assert.NotSame(captures[0].Local, captures[1].Local);
        Assert.NotSame(captures[0].Lambda, captures[1].Lambda);
        Assert.Equal(2, analysis.Declarations.Files.Length);
    }

    [Theory]
    [InlineData("ref", "value", AstPassingMode.Ref, LambdaCaptureAccess.Read)]
    [InlineData("out", "value = 3", AstPassingMode.Out, LambdaCaptureAccess.Write)]
    public void CapturedCallableParametersRetainPassingModeAndBorrowedStorage(
        string mode, string body, AstPassingMode expectedMode, LambdaCaptureAccess expectedAccess)
    {
        var analysis = Bind($"module app\nfunc f({mode} value:int) {{ val callback = {{ {body} }} }}");
        var capture = Assert.Single(analysis.LambdaCaptures);

        Assert.Equal(expectedMode, capture.Local.PassingMode);
        Assert.Equal(LambdaCaptureStorage.BorrowedLocation, capture.Storage);
        Assert.Equal(expectedAccess, capture.Access);
    }

    [Fact]
    public void CapturedOverloadedLocalFunctionsRemainSeparateSymbols()
    {
        var analysis = Bind("module app\nfunc f() { func visit(value:int) {}; func visit(value:byte) {}; val callback = { visit(1) } }");
        var captures = analysis.LambdaCaptures.ToArray();

        Assert.Equal(2, captures.Length);
        Assert.All(captures, capture =>
        {
            Assert.Equal("visit", capture.Local.Name);
            Assert.Equal(LocalSymbolKind.Function, capture.Local.Kind);
            Assert.Equal(LambdaCaptureStorage.NestedCallableEnvironment, capture.Storage);
        });
        Assert.NotEqual(captures[0].Local.Id, captures[1].Local.Id);
    }

    [Fact]
    public void CaptureReferencesAreSourceOrderedAndAccessIsUnioned()
    {
        var analysis = Bind("module app\nfunc f() { var count = 0; val callback = { count = count + count }; use(callback) }");
        var capture = Assert.Single(analysis.LambdaCaptures);

        Assert.Equal(LambdaCaptureAccess.ReadWrite, capture.Access);
        Assert.Equal(capture.References.Select(reference => reference.Origin.Span.Start).OrderBy(offset => offset),
            capture.References.Select(reference => reference.Origin.Span.Start));
        Assert.Equal(3, capture.References.Length);
    }

    [Fact]
    public void ConstructorPropertyNameDoesNotBecomeALexicalCapture()
    {
        var analysis = Bind("module app\nclass Box(val value:int) { func callback(): () -> int = { value } }");
        Assert.Empty(analysis.LambdaCaptures);
    }

    [Fact]
    public void CaptureQueryAndCreationRejectCrossSnapshotOwnership()
    {
        var firstSession = AstTestSources.Session("module app\nfunc f(seed:int) { val callback = { seed } }");
        var secondSession = AstTestSources.Session("module app\nfunc f(seed:int) { val callback = { seed } }");
        var first = LexicalAnalysis.Create(firstSession, TestContext.Current.CancellationToken);
        var second = LexicalAnalysis.Create(secondSession, TestContext.Current.CancellationToken);
        var foreign = Assert.Single(secondSession.Files.SelectMany(file => file.Root.Descendants()).OfType<AstLambda>());

        Assert.Throws<ArgumentException>(() => first.GetLambdaCaptures(foreign));
        Assert.Throws<ArgumentNullException>(() => first.GetLambdaCaptures(null!));
        var owned = Assert.Single(firstSession.Files.SelectMany(file => file.Root.Descendants()).OfType<AstLambda>());
        Assert.Single(first.GetLambdaCaptures(owned));
    }

    [Fact]
    public void CancelledCaptureAnalysisStopsBeforeBuildingTheInventory()
    {
        var session = AstTestSources.Session("module app\nfunc f(seed:int) { val callback = { seed } }");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => LexicalAnalysis.Create(session, cancellation.Token));
    }
}
