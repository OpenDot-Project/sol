// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class CallableAssignmentEscapeAliasTests
{
    private static LexicalAnalysis Analyze(string source) =>
        LexicalAnalysis.Create(AstTestSources.Session(source), TestContext.Current.CancellationToken);

    [Fact]
    public void BranchAlternativesAcrossLambdaAndAnonymousFunctionWritesStayLocal()
    {
        var analysis = Analyze("module app\nfunc use(flag: bool, lambdaValue: int, functionValue: int): int {\n"
            + "var callback: () -> int = func(): int { functionValue }\n"
            + "if flag { callback = { lambdaValue } }\n"
            + "callback()\n}");
        var lambdaEscape = Assert.Single(analysis.LambdaEscapes);
        var functionEscape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, lambdaEscape.Disposition);
        Assert.Contains(lambdaEscape.Uses, use => use.Kind == LambdaEscapeUseKind.LocalInvocation);
        Assert.DoesNotContain(lambdaEscape.Uses, use => use.Kind == LambdaEscapeUseKind.AliasMutation);
        Assert.Equal("lambdaValue", Assert.Single(lambdaEscape.Captures).Local.Name);

        Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, functionEscape.Disposition);
        Assert.Contains(functionEscape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalInvocation);
        Assert.DoesNotContain(functionEscape.Uses,
            use => use.Kind == AnonymousFunctionEscapeUseKind.AliasMutation);
        Assert.Equal("functionValue", Assert.Single(functionEscape.Captures).Local.Name);
    }

    [Fact]
    public void LocalCallableAliasWritesPropagateAllKnownIdentities()
    {
        var analysis = Analyze("module app\nfunc use(replace: bool, firstValue: int, secondValue: int): int {\n"
            + "var first = { firstValue }\n"
            + "val second = func(): int { secondValue }\n"
            + "if replace { first = second }\n"
            + "first()\n}");
        Assert.Equal(2, analysis.LambdaEscapes.Length + analysis.AnonymousFunctionEscapes.Length);
        Assert.All(analysis.LambdaEscapes,
            escape => Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition));
        Assert.All(analysis.AnonymousFunctionEscapes,
            escape => Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition));
        Assert.Contains(analysis.LambdaEscapes, escape => escape.Captures.Any(capture =>
            capture.Local.Name == "firstValue"));
        Assert.Contains(analysis.AnonymousFunctionEscapes, escape => escape.Captures.Any(capture =>
            capture.Local.Name == "secondValue"));
    }

    [Fact]
    public void NullWriteKeepsTheCallableEscapeClassificationUnknown()
    {
        var analysis = Analyze("module app\nfunc use(seed: int): () {\n"
            + "var callback: (() -> int)? = { seed }\n"
            + "callback = null\n}");
        var escape = Assert.Single(analysis.LambdaEscapes);

        Assert.Equal(LambdaEscapeDisposition.Unknown, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.AliasMutation);
    }

    [Fact]
    public void TrackedWritesDoNotHideALaterPublication()
    {
        var analysis = Analyze("module app\nfunc publish(@escaping callback: () -> int): () {}\n"
            + "func use(flag: bool, first: int, second: int): () {\n"
            + "var callback: () -> int = { first }\n"
            + "if flag { callback = func(): int { second } }\n"
            + "callback()\n"
            + "publish(callback)\n}");

        Assert.Equal(2, analysis.LambdaEscapes.Length + analysis.AnonymousFunctionEscapes.Length);
        Assert.All(analysis.LambdaEscapes,
            escape => Assert.Equal(LambdaEscapeDisposition.MayEscape, escape.Disposition));
        Assert.All(analysis.AnonymousFunctionEscapes,
            escape => Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape, escape.Disposition));
        Assert.All(analysis.LambdaEscapes, escape => Assert.Contains(escape.Uses,
            use => use.Kind == LambdaEscapeUseKind.CallArgument));
        Assert.All(analysis.AnonymousFunctionEscapes, escape => Assert.Contains(escape.Uses,
            use => use.Kind == AnonymousFunctionEscapeUseKind.CallArgument));
    }

    [Fact]
    public void RefAndOutPublicationThroughLocalAliasesRemainsUnknown()
    {
        var analysis = Analyze("module app\nfunc sendLambda(out destination: () -> int, seed: int): () {\n"
            + "val callback = { seed }\n"
            + "destination = callback\n}\n"
            + "func sendFunction(ref destination: () -> int, seed: int): () {\n"
            + "val callback = func(): int { seed }\n"
            + "destination = callback\n}");

        var lambda = Assert.Single(analysis.LambdaEscapes);
        var function = Assert.Single(analysis.AnonymousFunctionEscapes);
        Assert.Equal(LambdaEscapeDisposition.Unknown, lambda.Disposition);
        Assert.Contains(lambda.Uses, use => use.Kind == LambdaEscapeUseKind.AliasMutation);
        Assert.Equal(AnonymousFunctionEscapeDisposition.Unknown, function.Disposition);
        Assert.Contains(function.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.AliasMutation);
    }

}
