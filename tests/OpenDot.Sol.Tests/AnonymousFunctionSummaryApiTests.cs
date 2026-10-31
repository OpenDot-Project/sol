// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionSummaryApiTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\n";

    [Fact]
    public void SummaryReportsEveryNormalExitAndOutAssignment()
    {
        var session = AstTestSources.Session("module app\nfunc use() { val callback = func(flag: bool, out value: int): int { "
            + "if flag { value = 1; return 1 }; value = 2; 2 } }", Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(item => item.Code + " " + item.Message)));
        var function = Assert.Single(session.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>());

        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var summary = Assert.IsType<AnonymousFunctionBodySummary>(analysis.GetAnonymousFunctionBody(function));

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(item => item.Code + " " + item.Message)));
        Assert.Same(function, summary.Function);
        Assert.Equal(2, summary.NormalExitCount);
        Assert.True(summary.HasReachableFallthrough);
        Assert.True(summary.IsComplete);
        var outParameter = Assert.Single(summary.OutParameters);
        Assert.Equal(AstPassingMode.Out, outParameter.Parameter.PassingMode);
        Assert.True(outParameter.AssignedOnEveryNormalExit);
        Assert.Same(summary, Assert.Single(analysis.AnonymousFunctionBodies));
    }

    [Fact]
    public void SummaryQueryRejectsAnAnonymousFunctionFromAnotherSourceSet()
    {
        var first = Analyze("func use() { val callback = func(): int { 1 } }");
        var second = Analyze("func use() { val callback = func(): int { 2 } }");
        var foreign = Assert.Single(second.Functions);

        Assert.Throws<ArgumentException>(() => first.Analysis.GetAnonymousFunctionBody(foreign));
        Assert.Throws<ArgumentNullException>(() => first.Analysis.GetAnonymousFunctionBody(null!));
    }

    [Fact]
    public void AmbiguousArrayCandidateDoesNotPublishSpeculativeFunctionSummary()
    {
        var session = AstTestSources.Session("module app\n"
            + "func select(callbacks: [](() -> String?)): int = 1\n"
            + "func select(callbacks: [](() -> Throwable?)): int = 2\n"
            + "func use() { val result = select([func() { null }]) }", Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(item => item.Code + " " + item.Message)));
        var function = Assert.Single(session.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>());

        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2222");
        Assert.Null(analysis.GetAnonymousFunctionBody(function));
        Assert.Empty(analysis.AnonymousFunctionBodies);
    }

    private static (BodyAnalysis Analysis, AstAnonymousFunction[] Functions) Analyze(string source)
    {
        var session = AstTestSources.Session("module app\n" + source, Core);
        var declarations = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(declarations.HasErrors, string.Join('\n', declarations.Diagnostics.Select(item => item.Code + " " + item.Message)));
        var functions = session.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>().ToArray();
        return (BodyAnalysis.Create(declarations, TestContext.Current.CancellationToken), functions);
    }
}
