// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLambdaInferenceMatrixTests
{
    private const string Core = "module std.core\n"
        + "open class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\nopen class IOException: Exception {}\n";

    private static BodyAnalysis Analyze(string source)
    {
        var sources = SourceAnalysis.Create(AstTestSources.Session(["module app\n" + source, Core]),
            TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors,
            string.Join('\n', sources.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static string Diagnostics(BodyAnalysis analysis) => string.Join('\n', analysis.Diagnostics.Select(
        diagnostic => diagnostic.Code + " " + diagnostic.Message));

    private static BodyCallBinding SingleCall(BodyAnalysis analysis, string name) =>
        Assert.Single(analysis.Calls, binding => binding.Target.Name == name);

    [Theory]
    [InlineData("make({ 42 })")]
    [InlineData("make({ -> 42 })")]
    public void HeaderlessZeroParameterLambdaClosesGenericResult(string invocation)
    {
        var analysis = Analyze("func <T> make(callback: () -> T): T = callback()\n"
            + "func use(): int = " + invocation);

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(analysis.IsComplete, string.Join(" | ", analysis.UnverifiedNodes.Select(node => node.Origin.Text)));
        Assert.Equal("int", Assert.Single(SingleCall(analysis, "make").TypeArguments).Name);
    }

    [Fact]
    public void HeaderlessZeroParameterTrailingLambdaClosesGenericResult()
    {
        var analysis = Analyze("func <T> make(callback: () -> T): T = callback()\n"
            + "func use(): int = make { 42 }");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(analysis.IsComplete, string.Join(" | ", analysis.UnverifiedNodes.Select(node => node.Origin.Text)));
        var call = SingleCall(analysis, "make");
        Assert.Equal("int", Assert.Single(call.TypeArguments).Name);
        Assert.Equal(new[] { 0 }, call.Arguments.Select(argument => argument.ParameterIndex));
        Assert.Single(Assert.Single(analysis.LambdaBodies).FunctionType.Arguments);
    }

    [Fact]
    public void HeaderlessZeroParameterNamedLambdaClosesIndependentGenericResult()
    {
        var analysis = Analyze("func <T, R> transform(seed: T, callback: () -> R): R = callback()\n"
            + "func use(): String = transform(callback = { \"result\" }, seed = 7)");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        var call = SingleCall(analysis, "transform");
        Assert.Equal(new[] { "int", "std.core.String" }, call.TypeArguments.Select(type => type.Name));
        Assert.Equal(new[] { 1, 0 }, call.Arguments.Select(argument => argument.ParameterIndex));
        Assert.Equal("std.core.String", call.ReturnType!.Name);
        var lambda = Assert.Single(analysis.LambdaBodies);
        Assert.Single(lambda.FunctionType.Arguments);
        Assert.Equal("std.core.String", lambda.FunctionType.Arguments[^1].Name);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ -> }")]
    public void EmptyHeaderlessLambdaBodyInfersTheUnitResult(string lambda)
    {
        var analysis = Analyze("func <T> invoke(callback: () -> T): T = callback()\n"
            + "func use(): () = invoke(" + lambda + ")");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(analysis.IsComplete, string.Join(" | ", analysis.UnverifiedNodes.Select(node => node.Origin.Text)));
        var call = SingleCall(analysis, "invoke");
        var inferred = Assert.Single(call.TypeArguments);
        Assert.Equal(ResolvedTypeKind.Tuple, inferred.Kind);
        Assert.Empty(inferred.Arguments);
        Assert.Single(Assert.Single(analysis.LambdaBodies).FunctionType.Arguments);
    }

    [Fact]
    public void TwoHeaderlessLambdasContributeIndependentResultsInNamedOrder()
    {
        var analysis = Analyze("func <A, B> pair(first: () -> A, second: () -> B): () = ()\n"
            + "func use(): () { pair(second = { \"second\" }, first = { 1 }) }");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(analysis.IsComplete, string.Join(" | ", analysis.UnverifiedNodes.Select(node => node.Origin.Text)));
        var call = SingleCall(analysis, "pair");
        Assert.Equal(new[] { "int", "std.core.String" }, call.TypeArguments.Select(type => type.Name));
        Assert.Equal(new[] { 1, 0 }, call.Arguments.Select(argument => argument.ParameterIndex));
        var lambdas = analysis.LambdaBodies.OrderBy(summary => summary.Lambda.Origin.Span.Start).ToArray();
        Assert.Equal(2, lambdas.Length);
        Assert.Equal("std.core.String", lambdas[0].FunctionType.Arguments[^1].Name);
        Assert.Equal("int", lambdas[1].FunctionType.Arguments[^1].Name);
        Assert.All(lambdas, lambda => Assert.Single(lambda.FunctionType.Arguments));
    }

    [Fact]
    public void HeaderlessLambdaWithOnlyNullResultDoesNotInventGenericTypeEvidence()
    {
        var analysis = Analyze("func <T> maybe(callback: () -> T?): T? = callback()\n"
            + "func use(): () { val result = maybe({ null }) }");

        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "maybe");
        Assert.Contains(analysis.UnverifiedNodes, node => node.Origin.Text.Contains("maybe", System.StringComparison.Ordinal));
    }

    [Fact]
    public void NestedHeaderlessLambdasRetainSeparateZeroParameterSignatures()
    {
        var analysis = Analyze("func use(): () -> (() -> int) {\n"
            + "val factory: () -> (() -> int) = { { 42 } }; factory }");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(analysis.IsComplete, string.Join(" | ", analysis.UnverifiedNodes.Select(node => node.Origin.Text)));
        var lambdas = analysis.LambdaBodies.OrderBy(summary => summary.Lambda.Origin.Span.Start).ToArray();
        Assert.Equal(2, lambdas.Length);
        Assert.All(lambdas, lambda => Assert.Single(lambda.FunctionType.Arguments));
        Assert.Equal("int", lambdas[1].FunctionType.Arguments[^1].Name);
    }

    [Fact]
    public void HeaderlessTrailingLambdaCannotBindAnImplicitItArgument()
    {
        var analysis = Analyze("func <T> apply(callback: (T) -> T): T = callback(1)\n"
            + "func use(): int = apply { it }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "apply");
        Assert.Empty(analysis.LambdaBodies);
    }

    [Fact]
    public void HeaderlessLambdaDoesNotPretendToHaveAnImplicitParameter()
    {
        var analysis = Analyze("func <T> apply(callback: (T) -> T): () = ()\n"
            + "func use(): () { apply({ 42 }) }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "apply");
    }

    [Fact]
    public void ExplicitLambdaParameterAnnotationClosesItsGenericFormal()
    {
        var analysis = Analyze("func <T> accept(callback: (T) -> T): () = ()\n"
            + "func use(): () { accept({ value: String -> value }) }");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Equal("std.core.String", Assert.Single(SingleCall(analysis, "accept").TypeArguments).Name);
    }

    [Fact]
    public void PositionalLambdaUsesTypeEvidenceFromAnotherArgument()
    {
        var analysis = Analyze("func <T> transform(seed: T, callback: (T) -> T): T = callback(seed)\n"
            + "func use(): String = transform(\"seed\", { $0 })");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        var call = SingleCall(analysis, "transform");
        Assert.Equal("std.core.String", Assert.Single(call.TypeArguments).Name);
        Assert.Equal("std.core.String", call.ReturnType!.Name);
    }

    [Fact]
    public void LambdaResultClosesASeparateGenericReturnParameter()
    {
        var analysis = Analyze("func <T, R> transform(seed: T, callback: (T) -> R): R = callback(seed)\n"
            + "func use(): int = transform(\"seed\", { value -> 17 })");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Equal(new[] { "std.core.String", "int" },
            SingleCall(analysis, "transform").TypeArguments.Select(type => type.Name));
    }

    [Fact]
    public void TwoLambdaFactsCloseOneTypeIndependentOfArgumentOrder()
    {
        var analysis = Analyze("func <T> inspect(first: (T) -> T, second: (T) -> T): () = ()\n"
            + "func use(): () {\n"
            + "inspect({ left: String -> left }, { right -> right })\n"
            + "inspect({ right -> right }, { left: String -> left })\n"
            + "}");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        var calls = analysis.Calls.Where(binding => binding.Target.Name == "inspect").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call => Assert.Equal("std.core.String", Assert.Single(call.TypeArguments).Name));
    }

    [Fact]
    public void CrossLambdaDependencyIsReplayedAfterFirstTypeCloses()
    {
        var analysis = Analyze("func <A, B> connect(seed: () -> A, project: (A) -> B): () = ()\n"
            + "func use(): () { connect({ \"seed\" }, { value -> value }) }");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Equal(new[] { "std.core.String", "std.core.String" },
            SingleCall(analysis, "connect").TypeArguments.Select(type => type.Name));
    }

    [Fact]
    public void CrossLambdaDependencyIsIndependentOfNamedArgumentOrder()
    {
        var analysis = Analyze("func <A, B> connect(seed: () -> A, project: (A) -> B): () = ()\n"
            + "func use(): () { connect(project = { value -> value }, seed = { \"seed\" }) }");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Equal(new[] { "std.core.String", "std.core.String" },
            SingleCall(analysis, "connect").TypeArguments.Select(type => type.Name));
    }

    [Fact]
    public void NamedArgumentsRetainTheirMappedLambdaFormalDuringInference()
    {
        var analysis = Analyze("func <T, R> transform(seed: T, callback: (T) -> R): R = callback(seed)\n"
            + "func use(): String = transform(callback = { item: String -> item }, seed = \"sol\")");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        var call = SingleCall(analysis, "transform");
        Assert.Equal(new[] { "std.core.String", "std.core.String" },
            call.TypeArguments.Select(type => type.Name));
        Assert.Equal(new[] { 1, 0 }, call.Arguments.Select(argument => argument.ParameterIndex));
    }

    [Fact]
    public void TrailingLambdaContributesGenericResultEvidence()
    {
        var analysis = Analyze("func <T, R> transform(seed: T, callback: (T) -> R): R = callback(seed)\n"
            + "func use(): String = transform(\"seed\") { $0 }");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Equal(new[] { "std.core.String", "std.core.String" },
            SingleCall(analysis, "transform").TypeArguments.Select(type => type.Name));
    }

    [Fact]
    public void EmptyArrayReceivesItsElementTypeFromLambdaEvidence()
    {
        var analysis = Analyze("func <T> inspect(values: []T, result: () -> T): () = ()\n"
            + "func use(): () { inspect([], { \"element\" }) }");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Equal("std.core.String", Assert.Single(SingleCall(analysis, "inspect").TypeArguments).Name);
    }

    [Fact]
    public void EmptyArrayAndLambdaEvidenceResolveWhenNamedArgumentsReverseTheirOrder()
    {
        var analysis = Analyze("func <T> inspect(values: []T, result: () -> T): () = ()\n"
            + "func use(): () { inspect(result = { \"element\" }, values = []) }");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Equal("std.core.String", Assert.Single(SingleCall(analysis, "inspect").TypeArguments).Name);
    }

    [Fact]
    public void NumericArrayLiteralIsCheckedAfterLambdaEvidenceClosesItsElementType()
    {
        var analysis = Analyze("func <T> inspect(values: []T, transform: (T) -> T): () = ()\n"
            + "func use(): () { inspect([1], { value: byte -> value }) }");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Equal("byte", Assert.Single(SingleCall(analysis, "inspect").TypeArguments).Name);
    }

    [Fact]
    public void OutOfRangeArrayLiteralRejectsOnlyTheCandidateAfterLambdaInference()
    {
        var analysis = Analyze("func <T> inspect(values: []T, transform: (T) -> T): () = ()\n"
            + "func use(): () { inspect([256], { value: byte -> value }) }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "inspect");
    }

    [Fact]
    public void DefaultsDoNotProvideGenericInferenceEvidence()
    {
        var analysis = Analyze("func <T> maybe(value: T? = null): T? = value\n"
            + "func use(): () { val result = maybe() }");

        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "maybe");
        Assert.Contains(analysis.UnverifiedNodes, node => node.Origin.Text.Contains("maybe", System.StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyArrayAndOpenPositionalLambdaDoNotInventATypeArgument()
    {
        var analysis = Analyze("func <T> inspect(values: []T, transform: (T) -> T): () = ()\n"
            + "func use(): () { inspect([], { $0 }) }");

        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "inspect");
    }

    [Fact]
    public void ConflictingExactLambdaAnnotationsRejectTheGenericCandidate()
    {
        var analysis = Analyze("func <T> inspect(first: (T) -> T, second: (T) -> T): () = ()\n"
            + "func use(): () { inspect({ text: String -> text }, { number: int -> number }) }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "inspect");
    }

    [Fact]
    public void GenericCallbackEffectsCloseTypeParameterBeforeDependentLambdaReplay()
    {
        var analysis = Analyze("class ReadFailure: Exception {}\n"
            + "func read(): () throws ReadFailure = ()\n"
            + "func <E: Throwable> recover(action: () -> () throws E, handler: (E) -> ()): () throws E { action() }\n"
            + "func use(): () throws ReadFailure { recover({ read() }, { failure -> () }) }");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        var call = SingleCall(analysis, "recover");
        Assert.Equal("app.ReadFailure", Assert.Single(call.TypeArguments).Name);
        Assert.Equal("app.ReadFailure", call.ParameterTypes[1].Arguments[0].Name);
    }

    [Fact]
    public void UncheckedThrowDoesNotCloseGenericCheckedEffectParameter()
    {
        var analysis = Analyze("func fail(): () throws RuntimeException = ()\n"
            + "func <E: Throwable> recover(action: () -> () throws E): () throws E { action() }\n"
            + "func use(): () { recover({ fail() }) }");

        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "recover");
    }

    [Fact]
    public void OnlyTheSelectedLambdaReplayIsPublishedToBodyAnalysis()
    {
        var analysis = Analyze("func <T> apply(seed: T, callback: (T) -> T): T = callback(seed)\n"
            + "func use(): String = apply(\"seed\", { $0 })");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        var summary = Assert.Single(analysis.LambdaBodies);
        Assert.Equal("std.core.String", summary.FunctionType.Arguments[0].Name);
        Assert.True(summary.IsComplete);
    }

    [Fact]
    public void RejectedLambdaCandidateDoesNotLeakItsNestedCallDiagnostic()
    {
        var analysis = Analyze("func text(value: String): int = 1\n"
            + "func choose(callback: (int) -> int): int = 0\n"
            + "func choose(callback: (String) -> int): int = 1\n"
            + "func use(): int = choose({ value -> text(value) })");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        var choose = SingleCall(analysis, "choose");
        Assert.Equal("std.core.String", choose.ParameterTypes[0].Arguments[0].Name);
        Assert.Single(analysis.Calls, binding => binding.Target.Name == "text");
    }

    [Fact]
    public void AmbiguousLambdaCandidatesPublishNeitherNestedCallNorLambdaSummary()
    {
        var analysis = Analyze("func convert(value: Object): int = 1\n"
            + "func convert(value: String): int = 2\n"
            + "func choose(callback: (Object) -> int): int = 1\n"
            + "func choose(callback: (String) -> int): int = 2\n"
            + "func use(): int = choose({ value -> convert(value) })");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2222");
        Assert.Empty(analysis.Calls);
        Assert.Empty(analysis.LambdaBodies);
    }

    [Fact]
    public void SelectedCandidateIsStableWhenOverloadDeclarationsAreReordered()
    {
        const string use = "func use(): int = choose({ value: String -> 1 })";
        var first = Analyze("func choose(callback: (Object) -> int): int = 1\n"
            + "func choose(callback: (String) -> int): int = 2\n" + use);
        var reversed = Analyze("func choose(callback: (String) -> int): int = 2\n"
            + "func choose(callback: (Object) -> int): int = 1\n" + use);

        Assert.False(first.HasErrors, Diagnostics(first));
        Assert.False(reversed.HasErrors, Diagnostics(reversed));
        Assert.Equal("std.core.String", SingleCall(first, "choose").ParameterTypes[0].Arguments[0].Name);
        Assert.Equal("std.core.String", SingleCall(reversed, "choose").ParameterTypes[0].Arguments[0].Name);
    }

    [Fact]
    public void GenericCallDoesNotGiveAHeaderlessLambdaAnImplicitItParameter()
    {
        var analysis = Analyze("func <T> apply(callback: (T) -> T): () = ()\n"
            + "func use(): () { apply({ it }) }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "apply");
    }

    [Theory]
    [InlineData("{ it }")]
    [InlineData("{ -> it }")]
    public void NeitherParameterlessHeaderFormIntroducesImplicitIt(string lambda)
    {
        var analysis = Analyze("func <T> make(callback: () -> T): T = callback()\n"
            + "func use(): int = make(" + lambda + ")");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "make");
        Assert.Empty(analysis.LambdaBodies);
    }

    [Fact]
    public void HeaderlessLambdaReturnsItsFinalExpressionAfterLocalDeclarations()
    {
        var analysis = Analyze("func use(): int {\n"
            + "val compute: () -> int = { val first = 20; val second = 22; first + second };\n"
            + "compute() }");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(analysis.IsComplete, string.Join(" | ", analysis.UnverifiedNodes.Select(node => node.Origin.Text)));
        var callback = Assert.Single(analysis.LambdaBodies).FunctionType;
        Assert.Single(callback.Arguments);
        Assert.Equal("int", callback.Arguments[^1].Name);
    }
}
