// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionCaptureAccessMatrixTests
{
    private static (LexicalAnalysis Analysis, AstAnonymousFunction[] Functions) Analyze(string source)
    {
        var analysis = LexicalAnalysis.Create(AstTestSources.Session(source), TestContext.Current.CancellationToken);
        var functions = analysis.Declarations.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>().OrderBy(function => function.Origin.Span.Start).ToArray();
        return (analysis, functions);
    }

    [Fact]
    public void GroupedRefAndOutArgumentsUseTheCapturedStorageAccessModes()
    {
        var (analysis, functions) = Analyze("module app\nfunc create(ref source: int, out destination: int) { "
            + "val callback = func(): () { mutate(ref ((source)), out ((destination))) } }");
        var captures = analysis.GetAnonymousFunctionCaptures(Assert.Single(functions))
            .OrderBy(capture => capture.Local.Name).ToArray();

        Assert.Equal(new[] { "destination", "source" }, captures.Select(capture => capture.Local.Name));
        Assert.Equal(AnonymousFunctionCaptureAccess.Write, captures[0].Access);
        Assert.Equal(AnonymousFunctionCaptureAccess.ReadWrite, captures[1].Access);
        Assert.All(captures, capture => Assert.Equal(AnonymousFunctionCaptureStorage.BorrowedLocation, capture.Storage));
        Assert.All(captures, capture => Assert.Single(capture.References));
    }

    [Theory]
    [InlineData("state += 1")]
    [InlineData("++state")]
    [InlineData("state++")]
    [InlineData("--state")]
    [InlineData("state--")]
    public void CompoundAssignmentAndPrefixPostfixMutationAreReadWrite(string mutation)
    {
        var (analysis, functions) = Analyze("module app\nfunc create() { var state = 0; "
            + "val callback = func(): () { " + mutation + " } }");
        var capture = Assert.Single(analysis.GetAnonymousFunctionCaptures(Assert.Single(functions)));

        Assert.Equal("state", capture.Local.Name);
        Assert.Equal(AnonymousFunctionCaptureAccess.ReadWrite, capture.Access);
        Assert.Equal(AnonymousFunctionCaptureStorage.SharedMutableCell, capture.Storage);
        Assert.Single(capture.References);
    }

    [Fact]
    public void NestedPropagationKeepsTheShadowingLocalSymbolAcrossEveryEnvironment()
    {
        const string source = "module app\nfunc create(seed: int) { val outer = func(): int { "
            + "val seed = 10; val middle = func(): int { val inner = func(): int { seed }; inner() }; "
            + "middle() + seed } }";
        var (analysis, functions) = Analyze(source);

        Assert.Equal(3, functions.Length);
        Assert.Empty(analysis.GetAnonymousFunctionCaptures(functions[0]));
        var middleCapture = Assert.Single(analysis.GetAnonymousFunctionCaptures(functions[1]));
        var innerCapture = Assert.Single(analysis.GetAnonymousFunctionCaptures(functions[2]));
        var shadowingLocal = Assert.Single(analysis.Scopes.SelectMany(scope => scope.Symbols),
            local => local.Name == "seed" && local.Kind == LocalSymbolKind.Variable);
        var outerParameter = Assert.Single(analysis.Scopes.SelectMany(scope => scope.Symbols),
            local => local.Name == "seed" && local.Kind == LocalSymbolKind.Parameter);

        Assert.Same(shadowingLocal, middleCapture.Local);
        Assert.Same(shadowingLocal, innerCapture.Local);
        Assert.NotEqual(outerParameter.Id, shadowingLocal.Id);
        Assert.Equal(AnonymousFunctionCaptureAccess.Read, middleCapture.Access);
        Assert.Equal(AnonymousFunctionCaptureAccess.Read, innerCapture.Access);
        Assert.False(shadowingLocal.IsMutable);
        Assert.Equal(AnonymousFunctionCaptureStorage.ValueCopy, middleCapture.Storage);
        Assert.Equal(AnonymousFunctionCaptureStorage.ValueCopy, innerCapture.Storage);
    }
}
