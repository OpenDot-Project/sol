// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Text;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionBodyAnalysisTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\n";

    private static (BodyAnalysis Analysis, AstAnonymousFunction Function) Analyze(string source)
    {
        var session = AstTestSources.Session("module app\n" + source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        var function = Assert.Single(session.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>());
        return (BodyAnalysis.Create(sources, TestContext.Current.CancellationToken), function);
    }

    private static (BodyAnalysis Analysis, AstAnonymousFunction[] Functions) AnalyzeMany(string source)
    {
        var session = AstTestSources.Session("module app\n" + source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        var functions = session.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>().OrderBy(function => function.Origin.Span.Start).ToArray();
        return (BodyAnalysis.Create(sources, TestContext.Current.CancellationToken), functions);
    }

    [Fact]
    public void ExpressionBodyInfersTheFunctionResultFromTypedLexicalParameters()
    {
        var (analysis, function) = Analyze("func use() { val callback = func(value: int) { value + 1 }; "
            + "val result = callback(2) }");
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        Assert.True(analysis.IsComplete);
        Assert.Equal(ResolvedTypeKind.Function, signature.Kind);
        Assert.Equal("int", signature.Arguments[0].Name);
        Assert.Equal("int", signature.Arguments[^1].Name);
    }

    [Fact]
    public void ContextualFunctionTypeSuppliesAnOmittedResultType()
    {
        var (analysis, function) = Analyze("func use() { val callback: (int) -> int = func(value: int) { value }; "
            + "val result = callback(3) }");
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        Assert.True(analysis.IsComplete);
        Assert.Equal("int", signature.Arguments[0].Name);
        Assert.Equal("int", signature.Arguments[^1].Name);
    }

    [Fact]
    public void ExplicitReturnsAcrossEveryBranchInferOneCommonResult()
    {
        var (analysis, function) = Analyze("func use() { val callback = func(flag: bool) { "
            + "if flag { return 1 } else { return 2 } }; val result = callback(true) }");
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        Assert.Equal("int", signature.Arguments[^1].Name);
        Assert.True(analysis.IsComplete);
    }

    [Fact]
    public void RefAndOutParametersPreserveModesAndOutAssignmentFacts()
    {
        var (analysis, function) = Analyze("func use() { val callback = func(ref value: int, out result: int): int { "
            + "result = value + 1; return result }; var input = 4; var output: int; "
            + "val answer = callback(ref input, out output) }");
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        Assert.Equal(new[] { AstPassingMode.Ref, AstPassingMode.Out }, signature.ParameterModes);
        Assert.Equal("int", signature.Arguments[0].Name);
        Assert.Equal("int", signature.Arguments[1].Name);
        Assert.True(analysis.IsComplete);
    }

    [Fact]
    public void OutParameterMustBeAssignedOnEveryNormalExit()
    {
        var (analysis, _) = Analyze("func use() { val callback = func(out result: int): () { return } }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2242");
    }

    [Fact]
    public void OutAssignmentsCoverAnExplicitReturnAndTheTailExpressionPath()
    {
        var (analysis, function) = Analyze("func use() { val callback = func(flag: bool, out result: int) { "
            + "if flag { result = 1; return 1 }; result = 2; 2 } }");
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        Assert.Equal("int", signature.Arguments[^1].Name);
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code is "SOL2242" or "SOL2247");
        Assert.True(analysis.IsComplete);
    }

    [Fact]
    public void OutAssignmentIsCheckedOnEveryExplicitReturnPath()
    {
        var (analysis, _) = Analyze("func use() { val callback = func(flag: bool, out result: int): int { "
            + "if flag { result = 1; return 1 } else { return 2 } } }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2242");
    }

    [Fact]
    public void BlockTailExpressionMustMatchWrittenFunctionResult()
    {
        var (analysis, _) = Analyze("func use() { val callback = func(): String { 1 } }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
    }

    [Fact]
    public void NonCompletingBlockPathCannotOmitARequiredResult()
    {
        var (analysis, _) = Analyze("func use() { val callback = func(flag: bool): int { "
            + "if flag { return 1 } } }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2247");
    }

    [Fact]
    public void ExplicitCheckedThrowsAreValidatedAndAppearInTheFunctionType()
    {
        var (analysis, function) = Analyze("open class IOException: Exception {}\n"
            + "func fail(): int throws IOException { return 1 }\n"
            + "func use() { val callback = func(): int throws IOException { return fail() } }");
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        Assert.Contains(signature.Effects, effect => effect.Declaration?.Name == "IOException");
        Assert.True(analysis.IsComplete);
    }

    [Fact]
    public void InferredCheckedEffectsAreAddedToTheFunctionValueType()
    {
        var (analysis, function) = Analyze("open class IOException: Exception {}\n"
            + "func fail(): int throws IOException { return 1 }\n"
            + "func use() { val callback = func(): int { fail() } }");
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        Assert.Contains(signature.Effects, effect => effect.Declaration?.Name == "IOException");
        Assert.True(analysis.IsComplete);
    }

    [Fact]
    public void DeclaredThrowsMustCoverTheCheckedEffectsProducedByTheBody()
    {
        var (analysis, _) = Analyze("open class IOException: Exception {}\n"
            + "open class OtherException: Exception {}\n"
            + "func fail(): int throws IOException { return 1 }\n"
            + "func use() { val callback = func(): int throws OtherException { fail() } }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2250");
    }

    [Fact]
    public void ExpectedEffectContractRejectsAnUnlistedCheckedEffect()
    {
        var (analysis, _) = Analyze("open class IOException: Exception {}\n"
            + "func fail(): int throws IOException { return 1 }\n"
            + "func use() { val callback: () -> int = func(): int { fail() } }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2253");
    }

    [Fact]
    public void CheckedEffectsStayWithAnInnerFunctionCreatedInsideATryRegion()
    {
        var (analysis, functions) = AnalyzeMany("open class IOException: Exception {}\n"
            + "func fail(): int throws IOException { return 1 }\n"
            + "func use() { val enclosing = func() { try { val deferred = func(): int { fail() }; fail() } "
            + "catch (error: IOException) {} } }");
        Assert.Equal(2, functions.Length);
        var enclosing = Assert.IsType<ResolvedType>(analysis.GetExpression(functions[0])?.Type);
        var deferred = Assert.IsType<ResolvedType>(analysis.GetExpression(functions[1])?.Type);

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        Assert.Empty(enclosing.Effects);
        Assert.Contains(deferred.Effects, effect => effect.Declaration?.Name == "IOException");
        Assert.True(analysis.IsComplete);
    }

    [Fact]
    public void CapturingMutableLocalsCanBeSemanticallyCheckedWithoutClaimingEmissionSupport()
    {
        var (analysis, function) = Analyze("func use(seed: int) { var current = seed; "
            + "val callback = func(): int { current }; current = 2; val result = callback() }");
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        Assert.Equal(ResolvedTypeKind.Function, signature.Kind);
        Assert.True(analysis.IsComplete);
    }

    [Fact]
    public void RefOutAndRefLikeCapturesRequireAProvenNonescapingAnonymousFunction()
    {
        var (byRef, _) = Analyze("func capture(ref value: int): () -> int { "
            + "val callback = func(): int { value }; return callback }");
        Assert.Contains(byRef.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");

        var (byLike, _) = Analyze("ref struct View {}\nfunc capture(value: View): () -> View { "
            + "val callback = func(): View { value }; return callback }");
        Assert.Contains(byLike.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
    }

    [Fact]
    public void EmptyBlockInfersTheEmptyTupleResult()
    {
        var (analysis, function) = Analyze("func use() { val callback = func() {} }");
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        Assert.Equal(ResolvedTypeKind.Tuple, signature.Arguments[^1].Kind);
        Assert.Empty(signature.Arguments[^1].Arguments);
    }

    [Fact]
    public void AnonymousDefaultsAreRejectedWhileVarargUsesTheFixedArrayParameterType()
    {
        var (defaults, defaultFunction) = Analyze("func use() { val callback = func(value: int = 1): int { value } }");
        Assert.IsType<ResolvedType>(defaults.GetExpression(defaultFunction)?.Type);
        var defaultDiagnostic = Assert.Single(defaults.Diagnostics.Where(diagnostic => diagnostic.Code == "SOL2226"));
        Assert.Equal("1", defaultDiagnostic.Source.GetText(defaultDiagnostic.Span));
        Assert.True(defaults.HasErrors);

        var (varargs, varargFunction) = Analyze("func use() { val callback = func(vararg values: int): int { values[0] } }");
        var signature = Assert.IsType<ResolvedType>(varargs.GetExpression(varargFunction)?.Type);
        Assert.Equal(ResolvedTypeKind.Array, signature.Arguments[0].Kind);
        Assert.Equal("int", signature.Arguments[0].Arguments[0].Name);
        Assert.False(varargs.HasErrors);
        Assert.True(varargs.IsComplete);
        Assert.Equal(AstPassingMode.Value, signature.ParameterModes[0]);
    }

    [Fact]
    public void ContextualVarargParameterInfersItsElementTypeFromAnArrayFunctionType()
    {
        var (analysis, function) = Analyze("func use() { val callback: ([]int) -> int = "
            + "func(vararg values) { values.length } }");
        var signature = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        Assert.True(analysis.IsComplete);
        Assert.Equal(ResolvedTypeKind.Array, signature.Arguments[0].Kind);
        Assert.Equal("int", signature.Arguments[0].Arguments[0].Name);
    }

    [Theory]
    [InlineData("func(left: int, left: int) { () }", "SOL2005")]
    [InlineData("func(vararg left: int, vararg right: int) { () }", "SOL2005")]
    [InlineData("func(vararg left, right: int) { () }", "SOL2005")]
    public void AnonymousParameterNamesAndVarargGroupsFollowDeclarationRules(
        string function, string expectedDiagnostic)
    {
        var session = AstTestSources.Session("module app\nfunc use() { val callback = " + function + " }", Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);

        Assert.True(analysis.HasErrors);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == expectedDiagnostic);
    }

    [Fact]
    public void ResultInferenceStopsAtItsReturnPathBudgetWithoutThrowing()
    {
        var source = new StringBuilder("module app\nfunc use(flag: bool) { val callback = func() { ");
        for (int index = 0; index < 4097; index++) { source.Append("if flag { return 1 }\n"); }
        source.Append("1 } }");
        var session = AstTestSources.Session(source.ToString(), Core);
        var declarations = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(declarations.HasErrors);

        var analysis = BodyAnalysis.Create(declarations, TestContext.Current.CancellationToken);

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2290");
        Assert.False(analysis.IsComplete);
    }

    [Fact]
    public void ResultInferenceStopsAtItsBodyNodeBudgetWithoutThrowing()
    {
        var source = new StringBuilder("module app\nfunc use() { val callback = func() { ");
        for (int index = 0; index < 40000; index++) { source.Append("0;"); }
        source.Append("} }");
        var session = AstTestSources.Session(source.ToString(), Core);
        var declarations = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(declarations.HasErrors, string.Join('\n', declarations.Diagnostics.Select(d => d.Code + " " + d.Message)));

        var analysis = BodyAnalysis.Create(declarations, TestContext.Current.CancellationToken);

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2290"
            && diagnostic.Message.Contains("65536-node traversal budget", StringComparison.Ordinal));
        Assert.False(analysis.IsComplete);
    }

    [Fact]
    public void BodyAnalysisHonorsCancellationBeforeAnonymousFunctionTraversal()
    {
        var session = AstTestSources.Session("module app\nfunc use() { val callback = func(value: int) { value } }", Core);
        var declarations = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => BodyAnalysis.Create(declarations, cancellation.Token));
    }
}
