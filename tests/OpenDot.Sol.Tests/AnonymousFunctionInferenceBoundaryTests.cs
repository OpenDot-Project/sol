// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionInferenceBoundaryTests
{
    private const string Core = "module std.core\n"
        + "open class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\nopen class IOException: Exception {}\n";

    [Fact]
    public void ThrowOnlyBodyInfersBottomAndRetainsItsCheckedEffect()
    {
        var analysis = Analyze("func use() { val callback = func(error: IOException) { throw error } }");
        var function = SingleFunction(analysis);
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(analysis.IsComplete);
        Assert.Equal(ResolvedTypeKind.Bottom, signature.Arguments[^1].Kind);
        Assert.Contains(signature.Effects, effect => effect.Declaration?.Name == "IOException");
        var summary = Assert.IsType<AnonymousFunctionBodySummary>(analysis.GetAnonymousFunctionBody(function));
        Assert.Equal(0, summary.NormalExitCount);
        Assert.False(summary.HasReachableFallthrough);
    }

    [Fact]
    public void ResultJoinSelectsTheMostSpecificSharedNominalBase()
    {
        var analysis = Analyze("open class Animal: Object {}\nclass Dog: Animal {}\nclass Cat: Animal {}\n"
            + "func use(flag: bool, dog: Dog, cat: Cat) { val callback = func() { "
            + "if flag { return dog }; cat } }");
        var function = SingleFunction(analysis);
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(analysis.IsComplete);
        Assert.Equal("app.Animal", signature.Arguments[^1].Name);
    }

    [Fact]
    public void ResultInferenceDoesNotCollectReturnsFromNestedCallableBodies()
    {
        var analysis = Analyze("func use(flag: bool) { val outer = func() { if flag { "
            + "val inner = func() { return \"inner\" }; return 1 }; 2 } }");
        var functions = Functions(analysis);

        Assert.Equal(2, functions.Length);
        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(analysis.IsComplete);
        Assert.Equal("int", Assert.IsType<ResolvedType>(analysis.GetExpression(functions[0])?.Type).Arguments[^1].Name);
        Assert.Equal("std.core.String",
            Assert.IsType<ResolvedType>(analysis.GetExpression(functions[1])?.Type).Arguments[^1].Name);
    }

    [Fact]
    public void ExpectedFunctionTypeProvidesAnonymousParameterTypes()
    {
        var analysis = Analyze("func use() { val callback: (int) -> int = func(value) { value }; "
            + "val result = callback(42) }");
        var function = SingleFunction(analysis);
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(analysis.IsComplete);
        Assert.Equal("int", signature.Arguments[0].Name);
        Assert.Equal("int", signature.Arguments[^1].Name);
    }

    [Fact]
    public void MissingExpectedParameterTypeLeavesTheFunctionUnverified()
    {
        var analysis = Analyze("func use() { val callback = func(value) { value } }");
        var function = SingleFunction(analysis);

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2220");
        Assert.Null(analysis.GetExpression(function)?.Type);
        Assert.Null(analysis.GetAnonymousFunctionBody(function));
        Assert.False(analysis.IsComplete);
    }

    [Fact]
    public void CatchInsideFunctionBodyConsumesTheCheckedEffectBeforeContextualTyping()
    {
        var analysis = Analyze("func read(): int throws IOException = 1\n"
            + "func use() { val callback: () -> int = func(): int { "
            + "try { return read() } catch (error: IOException) { return 0 } } }");
        var function = Functions(analysis).Single();
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(analysis.IsComplete);
        Assert.Empty(signature.Effects);
    }

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session("module app\n" + source, Core);
        var declarations = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(declarations.HasErrors, string.Join('\n', declarations.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(declarations, TestContext.Current.CancellationToken);
    }

    private static AstAnonymousFunction SingleFunction(BodyAnalysis analysis) =>
        Assert.Single(Functions(analysis));

    private static AstAnonymousFunction[] Functions(BodyAnalysis analysis) =>
        analysis.Sources.Declarations.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>().OrderBy(function => function.Origin.Span.Start).ToArray();

    private static string Diagnostics(BodyAnalysis analysis) => string.Join('\n', analysis.Diagnostics.Select(
        diagnostic => diagnostic.Code + " " + diagnostic.Message));
}
