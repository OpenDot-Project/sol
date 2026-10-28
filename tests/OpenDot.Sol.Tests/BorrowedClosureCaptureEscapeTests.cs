// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class BorrowedClosureCaptureEscapeTests
{
    [Fact]
    public void LambdaCanReadARefParameterWhenItsOnlyUsesStayInTheActivation()
    {
        var analysis = Analyze("module app\nfunc read(ref value: int): int {\n"
            + "val callback = { -> value }\ncallback()\n}");
        var lambda = SingleLambda(analysis);

        AssertNoErrors(analysis);
        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping,
            analysis.Sources.Lexical.GetLambdaEscape(lambda).Disposition);
        Assert.Equal(LambdaCaptureStorage.BorrowedLocation,
            Assert.Single(analysis.Sources.Lexical.GetLambdaCaptures(lambda)).Storage);
    }

    [Fact]
    public void AnonymousFunctionCanReadARefParameterWhenItsOnlyUsesStayInTheActivation()
    {
        var analysis = Analyze("module app\nfunc read(ref value: int): int {\n"
            + "val callback = func(): int { value }\ncallback()\n}");
        var function = SingleAnonymousFunction(analysis);

        AssertNoErrors(analysis);
        Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping,
            analysis.Sources.Lexical.GetAnonymousFunctionEscape(function).Disposition);
        Assert.Equal(AnonymousFunctionCaptureStorage.BorrowedLocation,
            Assert.Single(analysis.Sources.Lexical.GetAnonymousFunctionCaptures(function)).Storage);
    }

    [Fact]
    public void ReturningARefCaptureIsRejectedForBothCallableForms()
    {
        var lambda = Analyze("module app\nfunc publish(ref value: int): () -> int { return { -> value } }");
        var anonymous = Analyze("module app\nfunc publish(ref value: int): () -> int { return func(): int { value } }");

        AssertCaptureRejected(lambda);
        AssertCaptureRejected(anonymous);
    }

    [Theory]
    [InlineData("val outer = { -> val inner = { -> value }; inner() }", true)]
    [InlineData("val outer = func(): int { val inner = func(): int { value }; inner() }", false)]
    public void EscapingOuterClosureCannotCarryBorrowedCaptureThroughInvokedInnerClosure(
        string declarations, bool outerIsLambda)
    {
        var analysis = Analyze("module app\nfunc publish(ref value: int): () -> int { "
            + declarations + "\nreturn outer\n}");

        AssertCaptureRejected(analysis);
        var nodes = analysis.Sources.Declarations.Files.SelectMany(file => file.Root.Descendants()).ToArray();
        if (outerIsLambda)
        {
            var outer = nodes.OfType<AstLambda>().MinBy(lambda => lambda.Origin.Span.Start)!;
            Assert.Equal(LambdaEscapeDisposition.MayEscape,
                analysis.Sources.Lexical.GetLambdaEscape(outer).Disposition);
            Assert.Contains(nodes.OfType<AstLambda>(), lambda => lambda != outer
                && analysis.Sources.Lexical.GetLambdaEscape(lambda).Disposition
                    == LambdaEscapeDisposition.ProvenNonEscaping);
        }
        else
        {
            var outer = nodes.OfType<AstAnonymousFunction>().MinBy(function => function.Origin.Span.Start)!;
            Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape,
                analysis.Sources.Lexical.GetAnonymousFunctionEscape(outer).Disposition);
            Assert.Contains(nodes.OfType<AstAnonymousFunction>(), function => function != outer
                && analysis.Sources.Lexical.GetAnonymousFunctionEscape(function).Disposition
                    == AnonymousFunctionEscapeDisposition.ProvenNonEscaping);
        }
    }

    [Fact]
    public void PassingARefCaptureToANonescapingConsumerIsAllowed()
    {
        var analysis = Analyze("module app\nfunc consume(callback: () -> int): int = callback()\n"
            + "func call(ref value: int): int { val callback = { -> value }; consume(callback) }");

        AssertNoErrors(analysis);
        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping,
            analysis.Sources.Lexical.GetLambdaEscape(SingleLambda(analysis)).Disposition);
    }

    [Fact]
    public void TrackedLocalAliasesPreserveAProvenBorrowedLifetime()
    {
        var analysis = Analyze("module app\nfunc read(ref value: int): int {\n"
            + "val original = { -> value }\nvar current = original\n"
            + "val alias = current\nalias()\n}");
        var lambda = SingleLambda(analysis);

        AssertNoErrors(analysis);
        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping,
            analysis.Sources.Lexical.GetLambdaEscape(lambda).Disposition);
        Assert.Equal(LambdaCaptureStorage.BorrowedLocation,
            Assert.Single(analysis.Sources.Lexical.GetLambdaCaptures(lambda)).Storage);
    }

    [Fact]
    public void RefLikeLocalCanBeCapturedByAProvenNonescapingLambda()
    {
        var analysis = Analyze("module app\nref struct View { val length: int = 1 }\n"
            + "func read(view: View): int {\nval callback = { -> view.length}\ncallback()\n}");
        var lambda = SingleLambda(analysis);

        AssertNoErrors(analysis);
        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping,
            analysis.Sources.Lexical.GetLambdaEscape(lambda).Disposition);
        Assert.True(Assert.Single(analysis.Sources.Lexical.GetLambdaCaptures(lambda))
            .Local.WrittenType is not null);
    }

    [Fact]
    public void ReturningARefLikeLocalCaptureIsRejected()
    {
        var analysis = Analyze("module app\nref struct View { val length: int = 1 }\n"
            + "func create(view: View): () -> int { return { -> view.length } }");

        AssertCaptureRejected(analysis);
        Assert.Equal(LambdaEscapeDisposition.MayEscape,
            analysis.Sources.Lexical.GetLambdaEscape(SingleLambda(analysis)).Disposition);
    }

    [Fact]
    public void NonescapingRefLikeReceiverCaptureIsAllowedButReturningItIsRejected()
    {
        var local = Analyze("module app\nref struct View { val length: int = 1\n"
            + "func read(): int { val callback = { -> length }; callback() }\n}");
        var escaping = Analyze("module app\nref struct View { val length: int = 1\n"
            + "func create(): () -> int { return { -> length } }\n}");

        AssertNoErrors(local);
        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping,
            local.Sources.Lexical.GetLambdaEscape(SingleLambda(local)).Disposition);
        AssertCaptureRejected(escaping);
        Assert.Equal(LambdaEscapeDisposition.MayEscape,
            escaping.Sources.Lexical.GetLambdaEscape(SingleLambda(escaping)).Disposition);
    }

    [Fact]
    public void ClassReceiverMayBeRetainedByAnEscapingClosure()
    {
        var analysis = Analyze("module app\nclass Box(val value: int) {\n"
            + "func create(): () -> int { return { -> value } }\n}");

        AssertNoErrors(analysis);
        Assert.Equal(LambdaEscapeDisposition.MayEscape,
            analysis.Sources.Lexical.GetLambdaEscape(SingleLambda(analysis)).Disposition);
    }

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session(source);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static AstLambda SingleLambda(BodyAnalysis analysis) =>
        Assert.Single(analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes).OfType<AstLambda>());

    private static AstAnonymousFunction SingleAnonymousFunction(BodyAnalysis analysis) =>
        Assert.Single(analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstAnonymousFunction>());

    private static void AssertNoErrors(BodyAnalysis analysis) => Assert.False(analysis.HasErrors,
        string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));

    private static void AssertCaptureRejected(BodyAnalysis analysis) =>
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
}
