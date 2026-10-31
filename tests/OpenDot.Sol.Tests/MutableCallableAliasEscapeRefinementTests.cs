// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class MutableCallableAliasEscapeRefinementTests
{
    private static LexicalAnalysis Analyze(string source)
        => LexicalAnalysis.Create(AstTestSources.Session(source), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(false, "{ seed }")]
    [InlineData(true, "func(): int { seed }")]
    public void MutableAliasWithoutAnObservedWriteCanProveNonEscape(bool anonymous, string function)
    {
        var analysis = Analyze("module app\nfunc use(seed: int) { var callback = " + function
            + "; val alias = callback; alias() }");

        if (anonymous)
        {
            var escape = Assert.Single(analysis.AnonymousFunctionEscapes);
            Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
            Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalAlias);
            Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalInvocation);
            Assert.DoesNotContain(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.AliasMutation);
            Assert.Equal("seed", Assert.Single(escape.Captures).Local.Name);
        }
        else
        {
            var escape = Assert.Single(analysis.LambdaEscapes);
            Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
            Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.LocalAlias);
            Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.LocalInvocation);
            Assert.DoesNotContain(escape.Uses, use => use.Kind == LambdaEscapeUseKind.AliasMutation);
            Assert.Equal("seed", Assert.Single(escape.Captures).Local.Name);
        }
    }

    [Theory]
    [InlineData(false, "{ first }", "{ second }")]
    [InlineData(true, "func(): int { first }", "func(): int { second }")]
    public void ReassignmentOfMutableAliasUnionsValuesAndCanProveNonEscape(bool anonymous, string initial, string replacement)
    {
        var analysis = Analyze("module app\nfunc use(first: int, second: int) { var callback = " + initial
            + "; callback = " + replacement + "; callback() }");

        if (anonymous)
        {
            Assert.Equal(2, analysis.AnonymousFunctionEscapes.Length);
            Assert.All(analysis.AnonymousFunctionEscapes, escape =>
            {
                Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
                Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalAlias);
                Assert.DoesNotContain(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.AliasMutation);
                Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalInvocation);
            });
        }
        else
        {
            Assert.Equal(2, analysis.LambdaEscapes.Length);
            Assert.All(analysis.LambdaEscapes, escape =>
            {
                Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
                Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.LocalAlias);
                Assert.DoesNotContain(escape.Uses, use => use.Kind == LambdaEscapeUseKind.AliasMutation);
                Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.LocalInvocation);
            });
        }
    }

    [Theory]
    [InlineData(false, "ref")]
    [InlineData(false, "out")]
    [InlineData(true, "ref")]
    [InlineData(true, "out")]
    public void RefOrOutPassingDoesNotUseTheNoWriteProof(bool anonymous, string mode)
    {
        string function = anonymous ? "func(): int { seed }" : "{ seed }";
        string replacement = anonymous ? "func(): int { 0 }" : "{ 0 }";
        string helper = mode == "out"
            ? "func touch(out callback: () -> int) { callback = " + replacement + " }"
            : "func touch(ref callback: () -> int) {}";
        var analysis = Analyze("module app\n" + helper + "\nfunc use(seed: int) { var callback = " + function
            + "; touch(" + mode + " callback); callback() }");

        if (anonymous)
        {
            var escape = Assert.Single(analysis.AnonymousFunctionEscapes,
                candidate => candidate.Captures.Any(capture => capture.Local.Name == "seed"));
            Assert.NotEqual(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
            Assert.Contains(escape.Uses, use => use.Kind is AnonymousFunctionEscapeUseKind.CallArgument
                or AnonymousFunctionEscapeUseKind.UnsupportedContext);
            Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalInvocation);
        }
        else
        {
            var escape = Assert.Single(analysis.LambdaEscapes,
                candidate => candidate.Captures.Any(capture => capture.Local.Name == "seed"));
            Assert.NotEqual(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
            Assert.Contains(escape.Uses, use => use.Kind is LambdaEscapeUseKind.CallArgument
                or LambdaEscapeUseKind.UnsupportedContext);
            Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.LocalInvocation);
        }
    }

    [Theory]
    [InlineData(false, "{ callback() }")]
    [InlineData(true, "func(): int { callback() }")]
    public void NestedCallableCaptureRemainsUnknown(bool anonymous, string nestedFunction)
    {
        string function = anonymous ? "func(): int { seed }" : "{ seed }";
        var analysis = Analyze("module app\nfunc use(seed: int) { var callback = " + function
            + "; val nested = " + nestedFunction + " }");

        if (anonymous)
        {
            var escape = Assert.Single(analysis.AnonymousFunctionEscapes,
                candidate => candidate.Captures.Any(capture => capture.Local.Name == "seed"));
            Assert.Equal(AnonymousFunctionEscapeDisposition.Unknown, escape.Disposition);
            Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.CallableCapture);
        }
        else
        {
            var escape = Assert.Single(analysis.LambdaEscapes,
                candidate => candidate.Captures.Any(capture => capture.Local.Name == "seed"));
            Assert.Equal(LambdaEscapeDisposition.Unknown, escape.Disposition);
            Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.CallableCapture);
        }
    }
}
