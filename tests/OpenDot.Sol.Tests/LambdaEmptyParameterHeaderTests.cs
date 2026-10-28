// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LambdaEmptyParameterHeaderTests
{
    [Fact]
    public void OmittedArrowProducesAHeaderlessZeroParameterLambda()
    {
        var snapshot = AstTestSources.Parse("val callback: () -> int = { 42 }");
        var lambda = Assert.Single(snapshot.Root.Descendants().OfType<AstLambda>());

        Assert.Equal(AstLambdaForm.Headerless, lambda.Form);
        Assert.Empty(lambda.Parameters);
        Assert.Empty(snapshot.Psi.Syntax.Diagnostics);
    }

    [Fact]
    public void OmittedArrowSupportsExpressionAndEmptyBlockBodies()
    {
        var expression = Analyze("module app\nfunc expression(): int { "
            + "val callback: () -> int = { 42 }; callback() }");
        var block = Analyze("module app\nfunc block(): () { val callback: () -> () = {}; callback() }");

        Assert.False(expression.HasErrors, Describe(expression));
        Assert.False(block.HasErrors, Describe(block));
        Assert.All(expression.LambdaBodies, summary => Assert.True(summary.IsComplete));
        Assert.All(block.LambdaBodies, summary => Assert.True(summary.IsComplete));
    }

    [Fact]
    public void ExplicitEmptyArrowRemainsEquivalentToOmittedArrow()
    {
        var analysis = Analyze("module app\nfunc run(): int { "
            + "val withArrow: () -> int = { -> 1 }; val bare: () -> int = { 2 }; withArrow() + bare() }");

        Assert.False(analysis.HasErrors, Describe(analysis));
        var lambdas = analysis.Sources.Declarations.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstLambda>().OrderBy(lambda => lambda.Origin.Span.Start).ToArray();
        Assert.Equal(2, lambdas.Length);
        Assert.All(lambdas, lambda => Assert.Empty(lambda.Parameters));
    }

    [Fact]
    public void OmittedArrowDoesNotIntroduceAnImplicitParameter()
    {
        var analysis = Analyze("module app\nfunc invalid(): int { "
            + "val callback: (int) -> int = { 42 }; callback(1) }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2220");
    }

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session(source);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static string Describe(BodyAnalysis analysis) => string.Join('\n', analysis.Diagnostics.Select(diagnostic =>
        diagnostic.Code + " " + diagnostic.Message));
}
