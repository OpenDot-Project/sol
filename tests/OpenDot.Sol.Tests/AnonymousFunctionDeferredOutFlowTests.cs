// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionDeferredOutFlowTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\n";

    [Fact]
    public void UnconditionalDeferredAssignmentCompletesAnonymousOutParameter()
    {
        var (analysis, function) = Analyze("func use() { val callback = func(out result: int): () { "
            + "defer { result = 7 } } }");
        var summary = Assert.IsType<AnonymousFunctionBodySummary>(analysis.GetAnonymousFunctionBody(function));

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(analysis.IsComplete, Diagnostics(analysis));
        Assert.Equal(1, summary.NormalExitCount);
        Assert.True(Assert.Single(summary.OutParameters).AssignedOnEveryNormalExit);
    }

    [Fact]
    public void ConditionalDeferredAssignmentDoesNotCompleteAnonymousOutParameterOnEveryPath()
    {
        var (analysis, function) = Analyze("func use(flag: bool) { val callback = func(flag: bool, out result: int): () { "
            + "if flag { defer { result = 7 } } } }");
        var summary = Assert.IsType<AnonymousFunctionBodySummary>(analysis.GetAnonymousFunctionBody(function));

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2242");
        Assert.False(Assert.Single(summary.OutParameters).AssignedOnEveryNormalExit);
        Assert.False(summary.IsComplete);
    }

    private static (BodyAnalysis Analysis, AstAnonymousFunction Function) Analyze(string source)
    {
        var session = AstTestSources.Session("module app\n" + source, Core);
        var declarations = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(declarations.HasErrors, Diagnostics(declarations.Diagnostics));
        var function = Assert.Single(session.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>());
        return (BodyAnalysis.Create(declarations, TestContext.Current.CancellationToken), function);
    }

    private static string Diagnostics(BodyAnalysis analysis) => Diagnostics(analysis.Diagnostics);

    private static string Diagnostics(System.Collections.Immutable.ImmutableArray<OpenDot.Sol.Diagnostics.Diagnostic> diagnostics)
        => string.Join('\n', diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message));
}
