// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionNullableExpectedTypeTests
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
    public void NullableExpectedFunctionTypeSuppliesAnonymousParameterAndResultContext()
    {
        var (analysis, function) = Analyze("func use() { val callback: ((int) -> int)? = "
            + "func(value) { value + 1 } }");
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        Assert.True(analysis.IsComplete);
        Assert.Equal(ResolvedTypeKind.Function, signature.Kind);
        Assert.Equal("int", Assert.Single(signature.Arguments.Take(signature.ParameterModes.Length)).Name);
        Assert.Equal("int", signature.Arguments[^1].Name);
    }

    [Fact]
    public void NullableTargetContextDoesNotMakeTheAnonymousExpressionNullable()
    {
        var (analysis, function) = Analyze("func use() { val callback: (() -> int)? = func() { 42 } }");
        var expressionType = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);

        Assert.False(expressionType.IsNullable);
        Assert.Equal(ResolvedTypeKind.Function, expressionType.Kind);
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
    }

    [Fact]
    public void NullableFunctionContextPreservesRefOutModesExactly()
    {
        var (analysis, function) = Analyze("func use() { val callback: ((ref int, out int) -> ())? = "
            + "func(ref input, out output: int) { output = input }; "
            + "var value = 1; var result: int; callback!!(ref value, out result) }");
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        Assert.Equal(new[] { AstPassingMode.Ref, AstPassingMode.Out }, signature.ParameterModes);
        Assert.True(analysis.IsComplete);
    }

    [Theory]
    [InlineData("((ref int) -> ())?", "func(value: int) { () }")]
    [InlineData("(int?)?", "func(value) { value }")]
    public void NullableContextStillRejectsWrongModeOrNonFunctionExpectedType(string targetType, string expression)
    {
        var session = AstTestSources.Session("module app\nfunc use() { val callback: " + targetType
            + " = " + expression + " }", Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);

        Assert.True(analysis.HasErrors);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2220");
    }

    [Fact]
    public void NullableExpectedTypeDoesNotEraseCheckedEffects()
    {
        var (analysis, function) = Analyze("open class IOException: Exception {}\n"
            + "func fail(): int throws IOException { return 1 }\n"
            + "func use() { val callback: (() -> int throws IOException)? = "
            + "func(): int { fail() } }");
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        Assert.Contains(signature.Effects, effect => effect.Declaration?.Name == "IOException");
        Assert.True(analysis.IsComplete);
    }
}
