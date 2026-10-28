// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FunctionValueDeclarationContractTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\n";

    private static BodyAnalysis Analyze(string body)
    {
        var session = AstTestSources.Session("module app\n" + body, Core);
        var source = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(source.HasErrors, string.Join('\n', source.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return BodyAnalysis.Create(source, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void DirectDeclarationCallKeepsDefaultsWhileItsFunctionValueHasFixedArity()
    {
        var analysis = Analyze("func produce(value: int = 40): int = value\n"
            + "func use(): int { val direct = produce(); val callback: (int) -> int = produce; "
            + "direct + callback(2) }");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        Assert.Equal("produce", Assert.Single(analysis.Expressions,
            expression => expression.FunctionValueTarget?.Name == "produce").FunctionValueTarget!.Name);
    }

    [Theory]
    [InlineData("callback()")]
    [InlineData("callback(value = 1)")]
    public void FunctionValueCallsDoNotRetainDeclarationDefaultsOrParameterNames(string invocation)
    {
        var analysis = Analyze("func produce(value: int = 40): int = value\n"
            + "func use() { val callback: (int) -> int = produce; val result = " + invocation + " }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
    }
}
