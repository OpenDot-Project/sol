// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class NullableFunctionValueControlFlowJoinTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\n";

    [Theory]
    [InlineData("func target(): int = 42\n"
        + "func invoke(enabled: bool): int {\n"
        + "var callback: (() -> int)? = null\n"
        + "for enabled { callback = target; break }\n"
        + "callback()\n}")]
    [InlineData("open class IOException: Exception {}\n"
        + "func afterCatch(callback: (() -> int)?, failure: IOException): int {\n"
        + "try { if callback == null { throw failure } } catch (error: IOException) {}\n"
        + "callback()\n}")]
    [InlineData("func afterFinally(callback: (() -> int)?): int {\n"
        + "var current = callback\n"
        + "try { if current == null { return 0 } } finally { current = null }\n"
        + "current()\n}")]
    public void JoinOrCleanupCannotInventANonNullFunctionProof(string declaration)
    {
        var session = AstTestSources.Session("module app\n" + declaration, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));

        var bodies = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);

        Assert.Contains(bodies.Diagnostics, diagnostic => diagnostic.Code is "SOL2221" or "SOL2243");
    }

    [Fact]
    public void FunctionValueRefCallInvalidatesTheWrittenLocalBeforeTheNextRead()
    {
        var session = AstTestSources.Session("module app\n"
            + "func clear(ref callback: (() -> int)?): () { callback = null }\n"
            + "func invoke(callback: (() -> int)?): int {\n"
            + "var current = callback\n"
            + "if current == null { return 0 }\n"
            + "val reset: (ref (() -> int)?) -> () = clear\n"
            + "reset(ref current)\n"
            + "current()\n}", Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));

        var bodies = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);

        Assert.Contains(bodies.Diagnostics, diagnostic => diagnostic.Code == "SOL2243");
    }

    [Fact]
    public void FunctionValueCallDropsMutableRefinementsBeforeNormalAndExceptionalEdges()
    {
        var session = AstTestSources.Session("module app\nopen class IOException: Exception {}\n"
            + "func invoke(action: () -> () throws IOException, callback: (() -> int)?): int throws IOException {\n"
            + "var current = callback\n"
            + "if current == null { return 0 }\n"
            + "try { action() } catch (error: IOException) {}\n"
            + "current()\n}", Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));

        var bodies = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);

        Assert.Contains(bodies.Diagnostics, diagnostic => diagnostic.Code == "SOL2243");
    }

    [Fact]
    public void DirectCallThatReceivesACapturingLambdaCannotKeepTheCapturedMutableProof()
    {
        var session = AstTestSources.Session("module app\n"
            + "func run(action: () -> ()): () { action() }\n"
            + "func invoke(callback: (() -> int)?): int {\n"
            + "var current = callback\n"
            + "if current == null { return 0 }\n"
            + "run({ current = null })\n"
            + "current()\n}", Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));

        var bodies = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);

        Assert.Contains(bodies.Diagnostics, diagnostic => diagnostic.Code == "SOL2243");
    }

    [Fact]
    public void ExceptionalFunctionValueCallInvalidatesDeferredCleanupProofBeforeSnapshot()
    {
        var session = AstTestSources.Session("module app\nopen class IOException: Exception {}\n"
            + "func invoke(callback: (() -> int)?): int {\n"
            + "var current = callback\n"
            + "if current == null { return 0 }\n"
            + "defer { current() }\n"
            + "val clear: () -> () throws IOException = func(): () throws IOException {\n"
            + "current = null\n"
            + "throw IOException()\n}\n"
            + "try { clear() } catch (error: IOException) {}\n"
            + "0\n}", Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));

        var bodies = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);

        Assert.Contains(bodies.Diagnostics, diagnostic => diagnostic.Code == "SOL2243");
    }
}
