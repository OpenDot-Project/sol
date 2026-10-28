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

public sealed class AnonymousFunctionOutFlowBoundaryTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\nopen class IOException: Exception {}\n";

    [Fact]
    public void NestedIfAndCatchBranchesAssignOutStorageOnEveryNormalExit()
    {
        var (analysis, functions) = Analyze("func use(flag: bool, failure: IOException) { "
            + "val callback = func(out result: int): int throws IOException { "
            + "try { if flag { throw failure }; "
            + "if flag { if flag { result = 1 } else { result = 2 } } else { result = 3 } } "
            + "catch (error: IOException) { if flag { result = 4 } else { result = 5 } }; result } }");
        var summary = Assert.IsType<AnonymousFunctionBodySummary>(
            analysis.GetAnonymousFunctionBody(Assert.Single(functions)));

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(Assert.Single(summary.OutParameters).AssignedOnEveryNormalExit);
        Assert.True(summary.IsComplete);
    }

    [Fact]
    public void FinallyAssignmentCoversEarlyReturnAndCaughtThrowExits()
    {
        var (analysis, functions) = Analyze("func use(flag: bool, failure: IOException) { "
            + "val callback = func(out result: int): int throws IOException { "
            + "try { if flag { return 1 }; throw failure } "
            + "catch (error: IOException) { return 2 } finally { result = 3 } } }");
        var summary = Assert.IsType<AnonymousFunctionBodySummary>(
            analysis.GetAnonymousFunctionBody(Assert.Single(functions)));

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Equal(2, summary.NormalExitCount);
        Assert.False(summary.HasReachableFallthrough);
        Assert.True(Assert.Single(summary.OutParameters).AssignedOnEveryNormalExit);
    }

    [Fact]
    public void UnassignedExplicitReturnFailsWhileThrowOnlyBodyHasNoNormalOutExit()
    {
        var (analysis, functions) = Analyze("func use(failure: IOException, flag: bool) { "
            + "val returnsEarly = func(out result: int, flag: bool): int { "
            + "if flag { return 1 }; result = 2; 2 }; "
            + "val throwsOnly = func(out result: int, failure: IOException): ! throws IOException { "
            + "throw failure } }");
        var summaries = functions.Select(function =>
            Assert.IsType<AnonymousFunctionBodySummary>(analysis.GetAnonymousFunctionBody(function))).ToArray();

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2242");
        Assert.False(Assert.Single(summaries[0].OutParameters).AssignedOnEveryNormalExit);
        Assert.Equal(2, summaries[0].NormalExitCount);
        Assert.True(Assert.Single(summaries[1].OutParameters).AssignedOnEveryNormalExit);
        Assert.Equal(0, summaries[1].NormalExitCount);
    }

    [Fact]
    public void NestedFunctionBodyDoesNotCreateAnOuterTryExceptionEdge()
    {
        var (analysis, functions) = Analyze("func use(failure: RuntimeException) { "
            + "val callback = func(out result: int): int { "
            + "try { func nested(): int { 10 / 2 }; result = 1; throw failure } "
            + "catch (error: RuntimeException) { result = 2 }; result } }");
        var summary = Assert.IsType<AnonymousFunctionBodySummary>(
            analysis.GetAnonymousFunctionBody(Assert.Single(functions)));

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(Assert.Single(summary.OutParameters).AssignedOnEveryNormalExit);
    }

    [Fact]
    public void NestedAnonymousOutParameterWithSameNameDoesNotAssignOuterStorage()
    {
        var (analysis, functions) = Analyze("func use() { val outer = func(out result: int): int { "
            + "val inner = func(out result: int): () { result = 1 }; 2 } }");
        var summaries = functions.Select(function =>
            Assert.IsType<AnonymousFunctionBodySummary>(analysis.GetAnonymousFunctionBody(function))).ToArray();

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2242");
        Assert.False(Assert.Single(summaries[0].OutParameters).AssignedOnEveryNormalExit);
        Assert.True(Assert.Single(summaries[1].OutParameters).AssignedOnEveryNormalExit);
    }

    private static (BodyAnalysis Analysis, AstAnonymousFunction[] Functions) Analyze(string source)
    {
        var session = AstTestSources.Session("module app\n" + source, Core);
        var declarations = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(declarations.HasErrors,
            string.Join('\n', declarations.Diagnostics.Select(item => item.Code + " " + item.Message)));
        var functions = session.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>().OrderBy(function => function.Origin.Span.Start).ToArray();
        return (BodyAnalysis.Create(declarations, TestContext.Current.CancellationToken), functions);
    }

    private static string Diagnostics(BodyAnalysis analysis) => string.Join('\n',
        analysis.Diagnostics.Select(item => item.Code + " " + item.Message));
}
