// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionFinalizerFlowTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\nopen class IOException: Exception {}\n";

    [Fact]
    public void CheckedEffectFromFinallyIsInferredAlongTheNormalReturnPath()
    {
        var (analysis, function) = Analyze("func fail(): int throws IOException { 1 }\n"
            + "func use() { val callback = func(): int { try { return 42 } finally { fail() } } }");
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);
        var summary = Assert.IsType<AnonymousFunctionBodySummary>(analysis.GetAnonymousFunctionBody(function));

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Contains(signature.Effects, effect => effect.Declaration?.Name == "IOException");
        Assert.Equal(1, summary.NormalExitCount);
        Assert.False(summary.HasReachableFallthrough);
        Assert.True(summary.IsComplete);
    }

    [Fact]
    public void AnOutParameterStillNeedsAssignmentOnNormalExitFromThrowingFinallyCall()
    {
        var (analysis, function) = Analyze("func fail(): int throws IOException { 1 }\n"
            + "func use() { "
            + "val callback = func(out result: int): int { "
            + "try { return 42 } finally { fail() } } }");
        var summary = Assert.IsType<AnonymousFunctionBodySummary>(analysis.GetAnonymousFunctionBody(function));

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2242");
        Assert.Equal(1, summary.NormalExitCount);
        Assert.False(Assert.Single(summary.OutParameters).AssignedOnEveryNormalExit);
    }

    [Fact]
    public void EffectInFinallyIsRetainedWhenTheTryReturnAlreadyAssignedOutStorage()
    {
        var (analysis, function) = Analyze("func fail(): int throws IOException { 1 }\n"
            + "func use() { "
            + "val callback = func(out result: int): int { "
            + "try { result = 7; return result } finally { fail() } } }");
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);
        var summary = Assert.IsType<AnonymousFunctionBodySummary>(analysis.GetAnonymousFunctionBody(function));

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Contains(signature.Effects, effect => effect.Declaration?.Name == "IOException");
        Assert.Equal(1, summary.NormalExitCount);
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2242");
    }

    [Fact]
    public void AThrowingFinallyDoesNotLeakItsEffectToAnOuterFunctionWhenCaught()
    {
        var (analysis, functions) = AnalyzeMany("func fail(): int throws IOException { 1 }\n"
            + "func use() { try { "
            + "val callback = func(): int { try { return 42 } finally { fail() } }; "
            + "callback() } catch (error: IOException) {} }");
        Assert.Single(functions);
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(functions[0])?.Type);
        var owner = Assert.Single(analysis.Sources.Declarations.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstFunction>(), candidate => candidate.Name == "use");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Contains(signature.Effects, effect => effect.Declaration?.Name == "IOException");
        Assert.Empty(analysis.GetBody(owner)!.EscapingEffects);
        Assert.True(analysis.IsComplete);
    }

    private static (BodyAnalysis Analysis, AstAnonymousFunction Function) Analyze(string body)
    {
        var (analysis, functions) = AnalyzeMany(body);
        return (analysis, Assert.Single(functions));
    }

    private static (BodyAnalysis Analysis, AstAnonymousFunction[] Functions) AnalyzeMany(string body)
    {
        var session = AstTestSources.Session("module app\n" + body, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, Diagnostics(sources.Diagnostics));
        var functions = session.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>().OrderBy(function => function.Origin.Span.Start).ToArray();
        return (BodyAnalysis.Create(sources, TestContext.Current.CancellationToken), functions);
    }

    private static string Diagnostics(BodyAnalysis analysis) => Diagnostics(analysis.Diagnostics);

    private static string Diagnostics(System.Collections.Immutable.ImmutableArray<OpenDot.Sol.Diagnostics.Diagnostic> diagnostics)
        => string.Join('\n', diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message));
}
