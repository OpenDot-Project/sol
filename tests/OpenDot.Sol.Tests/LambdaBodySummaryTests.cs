// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LambdaBodySummaryTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\nopen class IOException: Exception {}\n";

    [Fact]
    public void TailBodyPublishesResolvedSignatureAndOneFallthroughExit()
    {
        var analysis = Analyze("func use() { val callback = { value: int -> value + 1 } }");
        var lambda = Assert.Single(Lambdas(analysis));
        var summary = Assert.IsType<LambdaBodySummary>(analysis.GetLambdaBody(lambda));

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(summary.IsComplete, string.Join('\n', analysis.UnverifiedNodes.Select(node =>
            node.GetType().Name + ": " + node.Origin.Text)));
        Assert.Equal(1, summary.NormalExitCount);
        Assert.True(summary.HasReachableFallthrough);
        Assert.Equal("int", summary.FunctionType.Arguments[^1].Name);
        Assert.Empty(summary.OutParameters);
        Assert.Same(summary, Assert.Single(analysis.LambdaBodies));
    }

    [Fact]
    public void TargetedEarlyReturnJoinsWithTailAndRecordsOutAssignmentPerExit()
    {
        var analysis = Analyze("func use() { val callback = done@ { flag: bool, out value: int -> "
            + "if flag { value = 1; return@done 1 }; value = 2; 2 } }");
        var lambda = Assert.Single(Lambdas(analysis));
        var summary = Assert.IsType<LambdaBodySummary>(analysis.GetLambdaBody(lambda));
        var output = Assert.Single(summary.OutParameters);

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(summary.IsComplete, string.Join('\n', analysis.UnverifiedNodes.Select(node =>
            node.GetType().Name + ": " + node.Origin.Text)));
        Assert.Equal(2, summary.NormalExitCount);
        Assert.True(summary.HasReachableFallthrough);
        Assert.Equal("int", summary.FunctionType.Arguments[^1].Name);
        Assert.Same(lambda.Parameters.Last(), output.Parameter);
        Assert.True(output.AssignedOnEveryNormalExit);
    }

    [Fact]
    public void BareTargetedReturnContributesUnitAndHasNoFallthrough()
    {
        var analysis = Analyze("func use() { val callback = done@ { out value: int -> "
            + "value = 5; return@done } }");
        var lambda = Assert.Single(Lambdas(analysis));
        var summary = Assert.IsType<LambdaBodySummary>(analysis.GetLambdaBody(lambda));

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Equal(ResolvedTypeKind.Tuple, summary.FunctionType.Arguments[^1].Kind);
        Assert.Empty(summary.FunctionType.Arguments[^1].Arguments);
        Assert.Equal(1, summary.NormalExitCount);
        Assert.False(summary.HasReachableFallthrough);
        Assert.True(Assert.Single(summary.OutParameters).AssignedOnEveryNormalExit);
    }

    [Fact]
    public void InferredFunctionTypeCarriesCheckedEffectsFromItsBody()
    {
        var analysis = Analyze("func read(): int throws IOException = 4\n"
            + "func use() { val callback = { -> read() } }");
        var lambda = Assert.Single(Lambdas(analysis));
        var summary = Assert.IsType<LambdaBodySummary>(analysis.GetLambdaBody(lambda));

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(summary.IsComplete);
        Assert.Equal("int", summary.FunctionType.Arguments[^1].Name);
        Assert.Equal("IOException", Assert.Single(summary.FunctionType.Effects).Declaration!.Name);
    }

    [Fact]
    public void DeferredBodyProofProducesAnIncompleteSummaryWhenSignatureStillResolves()
    {
        var analysis = Analyze("func use() { val callback = { -> if true { missingValue }; 1 } }");
        var lambda = Assert.Single(Lambdas(analysis));
        var summary = Assert.IsType<LambdaBodySummary>(analysis.GetLambdaBody(lambda));

        Assert.False(summary.IsComplete);
        Assert.Equal("int", summary.FunctionType.Arguments[^1].Name);
        Assert.Contains(analysis.UnverifiedNodes, node => node.Origin.Text == "missingValue");
    }

    [Fact]
    public void UnresolvedParameterSignatureHasNoBodySummary()
    {
        var analysis = Analyze("func use() { val callback = { value -> value } }");
        var lambda = Assert.Single(Lambdas(analysis));

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2220");
        Assert.Null(analysis.GetLambdaBody(lambda));
        Assert.Empty(analysis.LambdaBodies);
    }

    [Fact]
    public void NullableFunctionContextProvidesLambdaParametersAndKeepsValueNonNull()
    {
        var analysis = Analyze("func use() {\n"
            + "val callback: (() -> int)? = { 42 }\n"
            + "val callbacks: []((() -> int)?) = [{ 1 }, null]\n"
            + "}");
        var lambdas = Lambdas(analysis);

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Equal(2, lambdas.Length);
        Assert.All(lambdas, lambda =>
        {
            Assert.Equal(ResolvedTypeKind.Function, analysis.GetExpression(lambda)!.Type!.Kind);
            Assert.True(Assert.IsType<LambdaBodySummary>(analysis.GetLambdaBody(lambda)).IsComplete);
        });
    }

    [Fact]
    public void NullableLambdaSourceTypingDoesNotBypassTheCurrentFirLiftBoundary()
    {
        var source = AstTestSources.Parse("object Main { func main(args: []String) {\n"
            + "val callback: (() -> int)? = { 42 }\n"
            + "val callbacks: []((() -> int)?) = [{ 1 }]\n"
            + "} }");
        var lambdas = source.Root.Descendants().OfType<AstLambda>().ToArray();
        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.Equal(2, lambdas.Length);
        Assert.All(lambdas, lambda => Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Code == "SOL2400" && diagnostic.Span == lambda.Origin.Span));
    }

    [Fact]
    public void InferredResultJoinsNullableNullWithNominalReturn()
    {
        var analysis = Analyze("open class Animal: Object {}\nclass Dog: Animal {}\n"
            + "func use(flag: bool, dog: Dog) { val callback = choose@ { -> "
            + "if flag { return@choose dog }; null } }");
        var summary = Assert.IsType<LambdaBodySummary>(analysis.GetLambdaBody(Assert.Single(Lambdas(analysis))));
        var result = summary.FunctionType.Arguments[^1];

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Equal(ResolvedTypeKind.Nullable, result.Kind);
        Assert.Equal("app.Dog", result.Arguments[0].Name);
        Assert.Equal(2, summary.NormalExitCount);
        Assert.True(summary.HasReachableFallthrough);
    }

    [Fact]
    public void IncompatibleTargetedReturnAndTailAreRejected()
    {
        var analysis = Analyze("func use(flag: bool) { val callback = choose@ { -> "
            + "if flag { return@choose 1 }; return@choose { value: int -> value } } }");
        var lambdas = Lambdas(analysis);
        Assert.Equal(2, lambdas.Length);
        var outer = lambdas[0];

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2200");
        Assert.Null(analysis.GetLambdaBody(outer));
        Assert.Contains(lambdas, lambda => !ReferenceEquals(lambda, outer)
            && analysis.GetLambdaBody(lambda) is { IsComplete: true });
    }

    [Fact]
    public void NestedLambdaReturnsDoNotContributeToTheOuterResultJoin()
    {
        var analysis = Analyze("func use(flag: bool) { val outer = outer@ { -> "
            + "val inner = inner@ { -> return@inner \"inner\" }; "
            + "if flag { return@outer 1 }; 2 } }");
        var lambdas = Lambdas(analysis);
        var summaries = lambdas.Select(lambda => analysis.GetLambdaBody(lambda)).ToArray();

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Equal(2, lambdas.Length);
        Assert.Equal("int", Assert.IsType<LambdaBodySummary>(summaries[0]).FunctionType.Arguments[^1].Name);
        Assert.Equal("std.core.String", Assert.IsType<LambdaBodySummary>(summaries[1]).FunctionType.Arguments[^1].Name);
    }

    [Fact]
    public void ThrowOnlyLambdaHasBottomResultAndNoNormalExit()
    {
        var analysis = Analyze("func use(failure: IOException) { val callback = { -> throw failure } }");
        var summary = Assert.IsType<LambdaBodySummary>(analysis.GetLambdaBody(Assert.Single(Lambdas(analysis))));

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Equal(ResolvedTypeKind.Bottom, summary.FunctionType.Arguments[^1].Kind);
        Assert.Equal(0, summary.NormalExitCount);
        Assert.False(summary.HasReachableFallthrough);
        Assert.Contains(summary.FunctionType.Effects, effect => effect.Declaration?.Name == "IOException");
    }

    [Fact]
    public void ForeignLambdaAndNullQueriesAreRejected()
    {
        var first = Analyze("func use() { val callback = { -> 1 } }");
        var second = Analyze("func use() { val callback = { -> 2 } }");
        var foreign = Assert.Single(Lambdas(second));

        Assert.Throws<ArgumentException>(() => first.GetLambdaBody(foreign));
        Assert.Throws<ArgumentNullException>(() => first.GetLambdaBody(null!));
    }

    [Fact]
    public void AmbiguousOverloadCandidateDoesNotPublishSpeculativeLambdaSummaries()
    {
        var analysis = Analyze("func choose(callbacks: [](() -> String?)): int = 1\n"
            + "func choose(callbacks: [](() -> Throwable?)): int = 2\n"
            + "func use() { val result = choose([{ -> null }]) }");
        var lambda = Assert.Single(Lambdas(analysis));

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2222");
        Assert.Null(analysis.GetLambdaBody(lambda));
        Assert.Empty(analysis.LambdaBodies);
    }

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session("module app\n" + source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, Diagnostics(sources.Diagnostics));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static AstLambda[] Lambdas(BodyAnalysis analysis) => analysis.Sources.Declarations.Files
        .SelectMany(file => file.Nodes).OfType<AstLambda>()
        .OrderBy(lambda => lambda.Origin.Source.Path, StringComparer.Ordinal)
        .ThenBy(lambda => lambda.Origin.Span.Start).ToArray();

    private static string Diagnostics(BodyAnalysis analysis) => Diagnostics(analysis.Diagnostics);

    private static string Diagnostics(System.Collections.Immutable.ImmutableArray<OpenDot.Sol.Diagnostics.Diagnostic> diagnostics)
        => string.Join('\n', diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message));
}
