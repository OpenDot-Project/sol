// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionAliasGraphTests
{
    private static LexicalAnalysis Analyze(string source)
        => LexicalAnalysis.Create(AstTestSources.Session(source), TestContext.Current.CancellationToken);

    [Fact]
    public void CyclicMutableAliasesTerminateAndKeepEachAliasWitness()
    {
        var analysis = Analyze("module app\nfunc f(seed: int) { "
            + "var first: () -> int; var second = func(): int { seed }; "
            + "first = second; second = first; first() }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalStorage);
        Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalAlias);
        Assert.DoesNotContain(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.AliasMutation);
        Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalInvocation);
        Assert.Equal(escape.Uses.OrderBy(use => use.Site.Origin.Source.Path, System.StringComparer.Ordinal)
            .ThenBy(use => use.Site.Origin.Span.Start).ThenBy(use => use.Kind), escape.Uses);
    }

    [Fact]
    public void AliasesAcrossNestedBlocksPropagateToTheInnermostInvocation()
    {
        var analysis = Analyze("module app\nfunc f(route: bool, seed: int) { "
            + "val first = func(): int { seed }; if route { val second = first; "
            + "if route { val third = ((second)); third() } } }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Equal(2, escape.Uses.Count(use => use.Kind == AnonymousFunctionEscapeUseKind.LocalAlias));
        Assert.Equal(3, escape.Uses.Count(use => use.Kind == AnonymousFunctionEscapeUseKind.LocalStorage));
        Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalInvocation);
        Assert.Equal("seed", Assert.Single(escape.Captures).Local.Name);
    }

    [Fact]
    public void AliasMutationDoesNotMaskCallAndReturnPublication()
    {
        var analysis = Analyze("module app\nfunc publish(@escaping callback: () -> int) {}\n"
            + "func create(seed: int): () -> int { var callback: () -> int = func(): int { seed }; "
            + "val alias = callback; callback = alias; publish(callback); return alias }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape, escape.Disposition);
        Assert.DoesNotContain(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.AliasMutation);
        Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalAlias);
        Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.CallArgument);
        Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.ReturnValue);
    }

    [Fact]
    public void AliasForwardedIntoAggregateAndObjectFieldRemainsEscapeCapable()
    {
        var aggregate = Analyze("module app\nfunc f(seed: int) { val callback = func(): int { seed }; "
            + "val alias = callback; val callbacks = [alias] }");
        var aggregateEscape = Assert.Single(aggregate.AnonymousFunctionEscapes);
        Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape, aggregateEscape.Disposition);
        Assert.Contains(aggregateEscape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalAlias);
        Assert.Contains(aggregateEscape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.AggregateElement);

        var field = Analyze("module app\nclass Holder { var callback: () -> int }\n"
            + "func f(seed: int) { val holder = Holder(); val callback = func(): int { seed }; "
            + "val alias = callback; holder.callback = alias }");
        var fieldEscape = Assert.Single(field.AnonymousFunctionEscapes);
        Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape, fieldEscape.Disposition);
        Assert.Contains(fieldEscape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalAlias);
        Assert.Contains(fieldEscape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.HeapStorage);
    }

    [Fact]
    public void ClassPropertyInitializerIsHeapPublicationNotLocalAliasStorage()
    {
        var analysis = Analyze("module app\nclass Holder { val callback = func(): int { 42 } }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.HeapStorage);
        Assert.DoesNotContain(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalStorage);
    }
}
