// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionBodySummaryFlowTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\nopen class IOException: Exception {}\n";

    [Fact]
    public void NormalExitCountIncludesEachExplicitReturnAndTheJoinedFallthroughState()
    {
        var (analysis, functions) = AnalyzeMany("func use() { val callback = func(first: bool, second: bool): int { "
            + "if first { return 1 }; if second { return 2 }; 3 } }");
        var function = Assert.Single(functions);
        var summary = Assert.IsType<AnonymousFunctionBodySummary>(analysis.GetAnonymousFunctionBody(function));

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Equal(3, summary.NormalExitCount);
        Assert.True(summary.HasReachableFallthrough);
        Assert.True(summary.IsComplete);
    }

    [Fact]
    public void ThrowOnlyBodyHasNoNormalExitOrFallthrough()
    {
        var (analysis, functions) = AnalyzeMany("func use() { val callback = func(error: IOException): int throws IOException { "
            + "throw error } }");
        var summary = Assert.IsType<AnonymousFunctionBodySummary>(analysis.GetAnonymousFunctionBody(Assert.Single(functions)));

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Equal(0, summary.NormalExitCount);
        Assert.False(summary.HasReachableFallthrough);
        Assert.True(summary.IsComplete);
    }

    [Fact]
    public void OutAssignmentSummaryUsesTheIntersectionOfBranchFacts()
    {
        var (analysis, functions) = AnalyzeMany("func use() { val callback = func(flag: bool, out result: int): int { "
            + "if flag { result = 1 } else { result = 2 }; 3 } }");
        var summary = Assert.IsType<AnonymousFunctionBodySummary>(analysis.GetAnonymousFunctionBody(Assert.Single(functions)));

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(Assert.Single(summary.OutParameters).AssignedOnEveryNormalExit);
        Assert.True(summary.IsComplete);
    }

    [Fact]
    public void OutAssignmentSummaryReportsABranchThatLeavesTheStorageUninitialized()
    {
        var (analysis, functions) = AnalyzeMany("func use() { val callback = func(flag: bool, out result: int): int { "
            + "if flag { result = 1 } else { 2 }; 3 } }");
        var summary = Assert.IsType<AnonymousFunctionBodySummary>(analysis.GetAnonymousFunctionBody(Assert.Single(functions)));

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2242");
        Assert.False(Assert.Single(summary.OutParameters).AssignedOnEveryNormalExit);
        Assert.False(summary.IsComplete);
    }

    [Fact]
    public void NestedAnonymousDefaultParameterErrorsInvalidateSummaryCompleteness()
    {
        var (analysis, functions) = AnalyzeMany("func use() { val outer = func(): int { "
            + "val inner = func(value: int = 1): int { value }; 2 } }");
        var summaries = functions.Select(function => analysis.GetAnonymousFunctionBody(function)).ToArray();

        Assert.Equal(2, functions.Length);
        Assert.All(summaries, Assert.NotNull);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2226");
        Assert.All(summaries, summary => Assert.False(summary!.IsComplete));
        Assert.False(analysis.IsComplete);
    }

    [Fact]
    public void MissingRecoveredBodyHasNoBodySummaryEvenWhenItsWrittenSignatureIsKnown()
    {
        var malformed = SyntaxTree.Parse(SourceText.FromString(
            "module app\nfunc use() { val callback = func(value: int): int\n}", "missing-anon-body.sol"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(malformed.HasErrors);
        var core = SyntaxTree.Parse(SourceText.FromString(Core, "core.sol"),
            cancellationToken: TestContext.Current.CancellationToken);
        var session = AnalysisSession.Create([malformed, core], new() { RequireCore = false },
            cancellationToken: TestContext.Current.CancellationToken);
        var function = Assert.Single(session.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>());

        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);

        Assert.Equal(AstBodyKind.Missing, function.Body.Kind);
        Assert.Null(analysis.GetAnonymousFunctionBody(function));
        Assert.False(analysis.IsComplete);
    }

    private static (BodyAnalysis Analysis, AstAnonymousFunction[] Functions) AnalyzeMany(string source)
    {
        var session = AstTestSources.Session("module app\n" + source, Core);
        var declarations = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(declarations.HasErrors, string.Join('\n', declarations.Diagnostics.Select(item => item.Code + " " + item.Message)));
        var functions = session.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>().OrderBy(function => function.Origin.Span.Start).ToArray();
        return (BodyAnalysis.Create(declarations, TestContext.Current.CancellationToken), functions);
    }

    private static string Diagnostics(BodyAnalysis analysis) => string.Join('\n',
        analysis.Diagnostics.Select(item => item.Code + " " + item.Message));
}
