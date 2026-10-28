// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionVarargCallContractTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\n";

    private static (BodyAnalysis Analysis, AstAnonymousFunction Function) Analyze(string body)
    {
        var session = AstTestSources.Session("module app\n" + body, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        var function = Assert.Single(session.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>());
        return (BodyAnalysis.Create(sources, TestContext.Current.CancellationToken), function);
    }

    [Fact]
    public void ContextualVarargElementTypeStaysAnArrayForAnIndirectArrayCall()
    {
        var (analysis, function) = Analyze("func use() { "
            + "val callback: ([]int) -> int = func(vararg values) { values.length }; "
            + "val count = callback([1, 2]) }");
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);
        var parameter = Assert.Single(signature.Arguments.Take(signature.ParameterModes.Length));

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        Assert.True(analysis.IsComplete);
        Assert.Equal(ResolvedTypeKind.Array, parameter.Kind);
        Assert.Equal("int", Assert.Single(parameter.Arguments).Name);
        Assert.Equal(AstPassingMode.Value, Assert.Single(signature.ParameterModes));
    }

    [Fact]
    public void IndirectFunctionValueDoesNotExpandAnArrayParameterIntoVarargArguments()
    {
        var (analysis, _) = Analyze("func use() { "
            + "val callback: ([]int) -> int = func(vararg values) { values.length }; "
            + "val count = callback(1, 2) }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
    }

    [Fact]
    public void VarargParameterWithoutWrittenOrContextualElementTypeIsRejected()
    {
        var (analysis, _) = Analyze("func use() { "
            + "val callback = func(vararg values) { values.length } }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2220");
    }
}
