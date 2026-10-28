// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionCaptureInventoryTests
{
    private static LexicalAnalysis Analyze(params string[] sources)
        => LexicalAnalysis.Create(AstTestSources.Session(sources), TestContext.Current.CancellationToken);

    [Fact]
    public void CaptureInventoryUsesResolvedLocalsAndCombinesReadWriteAccess()
    {
        var analysis = Analyze("module app\nfunc create(seed: int) { var state = seed; "
            + "val callback = func(input: int): int { state = state + input; seed + state } }");
        var function = Assert.Single(analysis.Declarations.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>());
        var captures = analysis.GetAnonymousFunctionCaptures(function).OrderBy(capture => capture.Local.Name).ToArray();

        Assert.Equal(new[] { "seed", "state" }, captures.Select(capture => capture.Local.Name));
        Assert.All(captures, capture => Assert.Same(function, capture.Function));
        Assert.Equal(AnonymousFunctionCaptureAccess.Read, captures[0].Access);
        Assert.Equal(AnonymousFunctionCaptureStorage.ValueCopy, captures[0].Storage);
        Assert.Equal(AnonymousFunctionCaptureAccess.ReadWrite, captures[1].Access);
        Assert.Equal(AnonymousFunctionCaptureStorage.SharedMutableCell, captures[1].Storage);
        Assert.Equal(captures[1].References.Select(reference => reference.Origin.Span.Start).OrderBy(offset => offset),
            captures[1].References.Select(reference => reference.Origin.Span.Start));
        Assert.Empty(analysis.LambdaCaptures);
    }

    [Fact]
    public void OneOuterLocalPropagatesThroughEachAnonymousFunctionEnvironment()
    {
        var analysis = Analyze("module app\nfunc create(seed: int) { val outer = func(): int { "
            + "val middle = func(): int { val inner = func(): int { seed }; inner() }; middle() }; outer() }");
        var functions = analysis.Declarations.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>().OrderBy(function => function.Origin.Span.Start).ToArray();
        var captures = analysis.AnonymousFunctionCaptures.ToArray();

        Assert.Equal(3, functions.Length);
        Assert.Equal(3, captures.Length);
        Assert.All(captures, capture =>
        {
            Assert.Same(captures[0].Local, capture.Local);
            Assert.Equal("seed", capture.Local.Name);
            Assert.Equal(AnonymousFunctionCaptureAccess.Read, capture.Access);
        });
        Assert.Equal(functions, captures.Select(capture => capture.Function));
    }

    [Fact]
    public void AnonymousParameterShadowingDoesNotCaptureSameNamedOuterLocal()
    {
        var analysis = Analyze("module app\nfunc create(value: int) { val callback = "
            + "func(value: int): int { value } }");
        var function = Assert.Single(analysis.Declarations.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>());

        Assert.Empty(analysis.GetAnonymousFunctionCaptures(function));
        Assert.Empty(analysis.AnonymousFunctionCaptures);
    }

    [Fact]
    public void NestedAnonymousFunctionCapturesParameterOwnedByItsParent()
    {
        var analysis = Analyze("module app\nfunc create() { val outer = func(value: int): int { "
            + "val inner = func(): int { value }; inner() } }");
        var functions = analysis.Declarations.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>().OrderBy(function => function.Origin.Span.Start).ToArray();
        var capture = Assert.Single(analysis.AnonymousFunctionCaptures);

        Assert.Same(functions[1], capture.Function);
        Assert.Equal("value", capture.Local.Name);
        Assert.Equal(LocalSymbolKind.Parameter, capture.Local.Kind);
        Assert.Same(functions[0], analysis.Scopes.Single(scope => scope.Id == capture.Local.ScopeId).Owner);
    }

    [Theory]
    [InlineData("ref", AstPassingMode.Ref, AnonymousFunctionCaptureAccess.Read, AnonymousFunctionCaptureStorage.BorrowedLocation)]
    [InlineData("out", AstPassingMode.Out, AnonymousFunctionCaptureAccess.Write, AnonymousFunctionCaptureStorage.BorrowedLocation)]
    public void NestedAnonymousCaptureRetainsBorrowedParameterMode(
        string mode, AstPassingMode passingMode, AnonymousFunctionCaptureAccess access,
        AnonymousFunctionCaptureStorage storage)
    {
        string body = mode == "ref" ? "value" : "value = 1";
        var analysis = Analyze($"module app\nfunc create() {{ val outer = func({mode} value: int) {{ "
            + $"val inner = func() {{ {body} }}; inner() }} }}");
        var capture = Assert.Single(analysis.AnonymousFunctionCaptures);

        Assert.Equal("value", capture.Local.Name);
        Assert.Equal(passingMode, capture.Local.PassingMode);
        Assert.Equal(access, capture.Access);
        Assert.Equal(storage, capture.Storage);
    }

    [Fact]
    public void ConstructorPropertiesAreReceiverRequirementsRatherThanLocalCaptures()
    {
        var analysis = Analyze("module app\nclass Box(val value: int) { val callback = "
            + "func(): int { value } }");
        var function = Assert.Single(analysis.Declarations.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>());

        Assert.Empty(analysis.GetAnonymousFunctionCaptures(function));
        Assert.Single(analysis.GetAnonymousFunctionReceiverCaptures(function));
    }

    [Fact]
    public void LocalFunctionCaptureRetainsItsOwnSymbolIdentity()
    {
        var analysis = Analyze("module app\nfunc create() { func local(): int = 1; "
            + "val callback = func(): int { local() } }");
        var capture = Assert.Single(analysis.AnonymousFunctionCaptures);

        Assert.Equal("local", capture.Local.Name);
        Assert.Equal(LocalSymbolKind.Function, capture.Local.Kind);
        Assert.Equal(AnonymousFunctionCaptureStorage.NestedCallableEnvironment, capture.Storage);
    }

    [Fact]
    public void AnonymousFunctionCaptureQueriesAreSnapshotOwnedAndEmptyQueriesAreStable()
    {
        var firstSession = AstTestSources.Session("module app\nfunc create() { val empty = func(): int { 1 } }");
        var secondSession = AstTestSources.Session("module app\nfunc create() { val empty = func(): int { 1 } }");
        var first = LexicalAnalysis.Create(firstSession, TestContext.Current.CancellationToken);
        var second = LexicalAnalysis.Create(secondSession, TestContext.Current.CancellationToken);
        var owned = Assert.Single(firstSession.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>());
        var foreign = Assert.Single(secondSession.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>());

        Assert.Empty(first.GetAnonymousFunctionCaptures(owned));
        Assert.Empty(first.GetAnonymousFunctionCaptures(owned));
        Assert.Throws<System.ArgumentException>(() => first.GetAnonymousFunctionCaptures(foreign));
        Assert.Throws<System.ArgumentNullException>(() => first.GetAnonymousFunctionCaptures(null!));
    }
}
