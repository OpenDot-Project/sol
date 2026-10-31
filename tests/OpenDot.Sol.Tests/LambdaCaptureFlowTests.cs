// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LambdaCaptureFlowTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String {}\nopen class Throwable {}\n"
        + "open class Exception: Throwable {}\nopen class RuntimeException: Exception {}\n";

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session("module app\n" + source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static void Valid(BodyAnalysis result) => Assert.False(result.HasErrors,
        string.Join('\n', result.Diagnostics.Select(d => d.Code + " " + d.Message + ": " + d.Source.GetText(d.Span))));

    private static void Error(BodyAnalysis result, string code) => Assert.Contains(result.Diagnostics, d => d.Code == code);

    private static AstFunction Function(BodyAnalysis analysis, string name)
        => analysis.Sources.Declarations.Files.SelectMany(file => file.Root.Descendants()).OfType<AstFunction>()
            .Single(function => function.Name == name);

    private static AstLambda Lambda(BodyAnalysis analysis)
        => Assert.Single(analysis.Sources.Declarations.Files.SelectMany(file => file.Root.Descendants()).OfType<AstLambda>());

    [Fact]
    public void CapturedImmutableNullableLocalCanBeNarrowed()
    {
        var analysis = Analyze("class Item { val value: int = 1 }\n"
            + "func read(item: Item?): int { val callback = { item }; if item == null { return 0 }; item.value }");
        Valid(analysis);

        var capture = Assert.Single(analysis.Sources.Lexical.GetLambdaCaptures(Lambda(analysis)));
        Assert.False(capture.Local.IsMutable);
        Assert.Equal(LambdaCaptureStorage.ValueCopy, capture.Storage);
        Assert.Equal(LambdaCaptureAccess.Read, capture.Access);
    }

    [Fact]
    public void CapturedMutableNullableLocalDoesNotReceiveAnUnsoundSmartCast()
    {
        var analysis = Analyze("class Item { val value: int = 1 }\n"
            + "func read(input: Item?): int { var item = input; val callback = { item }; "
            + "if item == null { return 0 }; item.value }");
        Error(analysis, "SOL2243");

        var capture = Assert.Single(analysis.Sources.Lexical.GetLambdaCaptures(Lambda(analysis)));
        Assert.True(capture.Local.IsMutable);
        Assert.Equal(LambdaCaptureStorage.SharedMutableCell, capture.Storage);
    }

    [Fact]
    public void NoncapturedMutableNullableLocalCanBeNarrowed()
    {
        var analysis = Analyze("class Item { val value: int = 1 }\n"
            + "func read(input: Item?): int { var item = input; if item == null { return 0 }; item.value }");
        Valid(analysis);
        Assert.Empty(analysis.Sources.Lexical.LambdaCaptures);
    }

    [Fact]
    public void InvokingCapturedMutableCallbackInvalidatesTheCallerNarrowing()
    {
        var analysis = Analyze("class Item { val value: int = 1 }\n"
            + "func read(input: Item?): int { var item = input; val callback: () -> () = { item = null }; "
            + "if item == null { return 0 }; callback(); item.value }");
        Error(analysis, "SOL2243");

        var capture = Assert.Single(analysis.Sources.Lexical.GetLambdaCaptures(Lambda(analysis)));
        Assert.Equal(LambdaCaptureAccess.Write, capture.Access);
    }

    [Fact]
    public void NestedNonescapingClosureCanCaptureARefLambdaParameter()
    {
        var analysis = Analyze("func create(): () { val callback: (ref int) -> () = "
            + "{ ref value -> val nested = { value }; () } }");

        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
        var captures = analysis.Sources.Lexical.LambdaCaptures;
        var capture = Assert.Single(captures, candidate => candidate.Local.Name == "value");
        Assert.Equal(AstPassingMode.Ref, capture.Local.PassingMode);
        Assert.Equal(LambdaCaptureStorage.BorrowedLocation, capture.Storage);
    }

    [Fact]
    public void NestedClosureDoesNotSatisfyItsOutLambdaParametersAssignmentObligation()
    {
        var analysis = Analyze("func create(): () { val callback: (out int) -> () = "
            + "{ out value -> val nested = { value = 1 }; () } }");

        Error(analysis, "SOL2242");
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
        var capture = Assert.Single(analysis.Sources.Lexical.LambdaCaptures,
            candidate => candidate.Local.Name == "value");
        Assert.Equal(AstPassingMode.Out, capture.Local.PassingMode);
        Assert.Equal(LambdaCaptureStorage.BorrowedLocation, capture.Storage);
        Assert.Equal(LambdaCaptureAccess.Write, capture.Access);
    }

    [Fact]
    public void NestedCaptureInventoryUsesResolvedSymbolsAndDoesNotLeakShadowing()
    {
        var analysis = Analyze("func inspect(seed: int): () { var value = seed; val outer = { "
            + "val inner = { value++ }; val value = 7; val shadowed = { value }; inner; shadowed }; () }");
        var lambdas = analysis.Sources.Declarations.Files.SelectMany(file => file.Root.Descendants()).OfType<AstLambda>()
            .OrderBy(lambda => lambda.Origin.Span.Start).ToArray();
        Assert.Equal(3, lambdas.Length);

        var captures = analysis.Sources.Lexical.LambdaCaptures;
        var mutable = captures.Where(capture => capture.Local.Name == "value" && capture.Local.IsMutable).ToArray();
        Assert.Equal(2, mutable.Length);
        Assert.Same(mutable[0].Local, mutable[1].Local);
        Assert.All(mutable, capture =>
        {
            Assert.Equal(LambdaCaptureStorage.SharedMutableCell, capture.Storage);
            Assert.Equal(LambdaCaptureAccess.ReadWrite, capture.Access);
        });
        Assert.Contains(mutable, capture => ReferenceEquals(capture.Lambda, lambdas[0]));
        Assert.Contains(mutable, capture => ReferenceEquals(capture.Lambda, lambdas[1]));

        var shadow = Assert.Single(captures.Where(capture => capture.Local.Name == "value" && !capture.Local.IsMutable));
        Assert.Equal(LambdaCaptureStorage.ValueCopy, shadow.Storage);
        Assert.Equal(LambdaCaptureAccess.Read, shadow.Access);
        Assert.Same(lambdas[2], shadow.Lambda);
        Valid(analysis);
    }

    [Fact]
    public void LambdaCreationPreservesItsCheckedEffectWithoutExecutingTheBody()
    {
        var analysis = Analyze("func fail(): int throws Exception { for {} }\n"
            + "func create(): () { val callback: () -> int throws Exception = { fail() } }");
        Valid(analysis);

        var lambda = Lambda(analysis);
        var binding = Assert.IsType<ExpressionBinding>(analysis.GetExpression(lambda));
        var callbackType = Assert.IsType<ResolvedType>(binding.Type);
        Assert.Equal(ResolvedTypeKind.Function, callbackType.Kind);
        Assert.Contains(callbackType.Effects, effect => effect.Name.EndsWith("Exception", System.StringComparison.Ordinal));
        Assert.Empty(analysis.GetBody(Function(analysis, "create"))!.EscapingEffects);
    }

    [Fact]
    public void CaptureMetadataDoesNotConvertIntoAnEscapeOrNativeClosureProof()
    {
        const string source = "func capture(seed: int): () -> int { val callback = { seed }; callback }";
        var analysis = Analyze(source);
        Valid(analysis);

        var lambda = Lambda(analysis);
        var capture = Assert.Single(analysis.Sources.Lexical.GetLambdaCaptures(lambda));
        Assert.Equal("seed", capture.Local.Name);
        Assert.Equal(LambdaCaptureStorage.ValueCopy, capture.Storage);

        var compilation = OpenDot.Sol.Fir.FirCompilation.Create([AstTestSources.Parse(source)],
            TestContext.Current.CancellationToken, library: true);
        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
    }
}
