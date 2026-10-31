// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionEscapeTests
{
    private static LexicalAnalysis Analyze(string source)
        => LexicalAnalysis.Create(AstTestSources.Session(source), TestContext.Current.CancellationToken);

    [Fact]
    public void ImmediateInvocationIsTheOnlyDirectNonEscapeProof()
    {
        var analysis = Analyze("module app\nfunc f(seed: int) { val result = (func(): int { seed })() }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Equal(AnonymousFunctionEscapeUseKind.ImmediateInvocation, Assert.Single(escape.Uses).Kind);
        Assert.Equal("seed", Assert.Single(escape.Captures).Local.Name);
    }

    [Fact]
    public void PassingAnonymousFunctionToCallIsPotentialPublication()
    {
        var analysis = Analyze("module app\nfunc consume(@escaping action: () -> int) {}\n"
            + "func f(seed: int) { consume(func(): int { seed }) }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Equal(AnonymousFunctionEscapeUseKind.CallArgument, Assert.Single(escape.Uses).Kind);
    }

    [Fact]
    public void ReturningAnonymousFunctionFromExpressionBodyIsPotentialPublication()
    {
        var analysis = Analyze("module app\nfunc create(seed: int) = func(): int { seed }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Equal(AnonymousFunctionEscapeUseKind.ReturnValue, Assert.Single(escape.Uses).Kind);
    }

    [Fact]
    public void ReturningAnonymousFunctionFromBlockTailIsPotentialPublication()
    {
        var analysis = Analyze("module app\nfunc create(seed: int): () -> int { (func(): int { seed }) }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Equal(AnonymousFunctionEscapeUseKind.ReturnValue, Assert.Single(escape.Uses).Kind);
    }

    [Fact]
    public void LocalAliasChainUsedOnlyForInvocationCanProveNonEscape()
    {
        var analysis = Analyze("module app\nfunc f(seed: int) { val first = func(): int { seed }; "
            + "val second = first; second() }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalAlias);
        Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalInvocation);
        Assert.Single(escape.Captures);
    }

    [Fact]
    public void GroupedAliasInvocationRemainsLocalOnly()
    {
        var analysis = Analyze("module app\nfunc f(seed: int) { val callback = func(): int { seed }; "
            + "val grouped = (((callback))); grouped() }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalAlias);
        Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalInvocation);
        Assert.Single(escape.Captures);
    }

    [Fact]
    public void ImmutableAliasesCreatedInBothConditionalBranchesRemainLocal()
    {
        var analysis = Analyze("module app\nfunc f(flag: bool, seed: int) { val callback = func(): int { seed }; "
            + "if flag { val left = callback; left() } else { val right = callback; right() } }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Equal(2, escape.Uses.Count(use => use.Kind == AnonymousFunctionEscapeUseKind.LocalAlias));
        Assert.Equal(2, escape.Uses.Count(use => use.Kind == AnonymousFunctionEscapeUseKind.LocalInvocation));
        Assert.Equal(escape.Uses.OrderBy(use => use.Site.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(use => use.Site.Origin.Span.Start).ThenBy(use => use.Kind), escape.Uses);
    }

    [Fact]
    public void ShortCircuitImmediateInvocationDoesNotPublishTheFunctionValue()
    {
        var analysis = Analyze("module app\nfunc f(flag: bool) { val result = flag && "
            + "(func(): bool { true })() }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Equal(AnonymousFunctionEscapeUseKind.ImmediateInvocation, Assert.Single(escape.Uses).Kind);
        Assert.Empty(escape.Captures);
    }

    [Fact]
    public void NestedAnonymousReturnsHaveIndependentEscapeAndCaptureFacts()
    {
        var analysis = Analyze("module app\nfunc create(seed: int): () -> () -> int { "
            + "(func(): () -> int { (func(): int { seed }) }) }");
        var escapes = analysis.AnonymousFunctionEscapes.OrderBy(escape => escape.Function.Origin.Span.Start).ToArray();

        Assert.Equal(2, escapes.Length);
        Assert.All(escapes, escape =>
            Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape, escape.Disposition));
        Assert.All(escapes, escape => Assert.Contains(escape.Uses,
            use => use.Kind == AnonymousFunctionEscapeUseKind.ReturnValue));
        Assert.Equal(2, escapes.Count(escape => escape.Captures.Any(capture => capture.Local.Name == "seed")));
        Assert.NotSame(escapes[0].Function, escapes[1].Function);
    }

    [Fact]
    public void UnusedFunctionValueStoredOnlyInAnImmutableLocalDoesNotEscape()
    {
        var analysis = Analyze("module app\nfunc f(seed: int) { val unused = func(): int { seed } }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Equal(AnonymousFunctionEscapeUseKind.LocalStorage, Assert.Single(escape.Uses).Kind);
        Assert.Equal("seed", Assert.Single(escape.Captures).Local.Name);
    }

    [Fact]
    public void CallingAliasDoesNotEraseLaterPublication()
    {
        var analysis = Analyze("module app\nfunc publish(@escaping action: () -> int) {}\n"
            + "func f(seed: int) { val first = func(): int { seed }; first(); publish(first) }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalInvocation);
        Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.CallArgument);
    }

    [Fact]
    public void AliasCallUsesTheResolvedNoEscapeParameterContract()
    {
        var analysis = Analyze("module app\nfunc invoke(action: () -> int): int = action()\n"
            + "func retain(@escaping action: () -> int) {}\n"
            + "func use(seed: int): int { val callback = func(): int { seed }; invoke(callback) }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.NonescapingCallArgument);
    }

    [Fact]
    public void ReturningAFunctionAliasRemainsEscapeCapable()
    {
        var analysis = Analyze("module app\nfunc create(seed: int): () -> int { "
            + "val callback = func(): int { seed }; return callback }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.ReturnValue);
        Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalStorage);
    }

    [Fact]
    public void BranchWritesUnionEveryTrackedFunctionValueAtTheJoin()
    {
        var analysis = Analyze("module app\nfunc f(flag: bool, first: int, second: int) { "
            + "var callback = func(): int { first }; if flag { callback = func(): int { second } }; callback() }");
        var escapes = analysis.AnonymousFunctionEscapes.ToArray();

        Assert.Equal(2, escapes.Length);
        Assert.All(escapes, escape =>
        {
            Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
            Assert.DoesNotContain(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.AliasMutation);
            Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.LocalInvocation);
        });
        Assert.Equal(new[] { "first", "second" }, escapes.Select(escape =>
            Assert.Single(escape.Captures).Local.Name).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void CapturingAnAliasInAnotherCallableRemainsUnknown()
    {
        var analysis = Analyze("module app\nfunc f(seed: int) { val callback = func(): int { seed }; "
            + "val invokeLater = func(): int { callback() } }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes.Where(candidate => candidate.Captures
            .Any(capture => capture.Local.Name == "seed")));

        Assert.Equal(AnonymousFunctionEscapeDisposition.Unknown, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == AnonymousFunctionEscapeUseKind.CallableCapture);
    }

    [Fact]
    public void AggregateAndHeapStorageRemainEscapeCapable()
    {
        var aggregate = Analyze("module app\nfunc f(seed: int) { val callbacks = [func(): int { seed }] }");
        var aggregateEscape = Assert.Single(aggregate.AnonymousFunctionEscapes);
        Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape, aggregateEscape.Disposition);
        Assert.Equal(AnonymousFunctionEscapeUseKind.AggregateElement, Assert.Single(aggregateEscape.Uses).Kind);

        var heap = Analyze("module app\nclass Holder { val callback: () -> int = func(): int { 1 } }");
        var heapEscape = Assert.Single(heap.AnonymousFunctionEscapes);
        Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape, heapEscape.Disposition);
        Assert.Equal(AnonymousFunctionEscapeUseKind.HeapStorage, Assert.Single(heapEscape.Uses).Kind);
    }

    [Fact]
    public void UnknownConsumerNeverAcquiresAStackLifetimeProof()
    {
        var analysis = Analyze("module app\nfunc f() { val result = 1 + func(): int { 2 } }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(AnonymousFunctionEscapeDisposition.Unknown, escape.Disposition);
        Assert.Equal(AnonymousFunctionEscapeUseKind.UnsupportedContext, Assert.Single(escape.Uses).Kind);
    }

    [Fact]
    public void EscapeFactsKeepLocalAndReceiverCapturesSeparate()
    {
        var analysis = Analyze("module app\nclass Box(val receiver: int) { func create(seed: int) { "
            + "val callback = func(): int { seed + receiver } } }");
        var escape = Assert.Single(analysis.AnonymousFunctionEscapes);

        Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Equal(AnonymousFunctionEscapeUseKind.LocalStorage, Assert.Single(escape.Uses).Kind);
        Assert.Equal("seed", Assert.Single(escape.Captures).Local.Name);
        Assert.Equal("receiver", Assert.Single(escape.ReceiverCaptures).References.Single().Spelling);
    }

    [Fact]
    public void EscapeQueryRejectsForeignSnapshotsAndInventoryIsDeterministic()
    {
        const string source = "module app\nfunc f(seed: int) { val callback = func(): int { seed }; callback() }";
        var firstSession = AstTestSources.Session(source);
        var secondSession = AstTestSources.Session(source);
        var first = LexicalAnalysis.Create(firstSession, TestContext.Current.CancellationToken);
        var second = LexicalAnalysis.Create(secondSession, TestContext.Current.CancellationToken);
        string[] Signature(LexicalAnalysis value) => value.AnonymousFunctionEscapes.Select(escape => string.Join("|",
            escape.Function.Origin.Span.Start, escape.Disposition, escape.Uses[0].Kind,
            string.Join(",", escape.Captures.Select(capture => capture.Local.Id)))).ToArray();

        Assert.Equal(Signature(first), Signature(second));
        var foreign = secondSession.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>().Single();
        Assert.Throws<System.ArgumentException>(() => first.GetAnonymousFunctionEscape(foreign));
        var owned = firstSession.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>().Single();
        Assert.Same(owned, first.GetAnonymousFunctionEscape(owned).Function);
        Assert.Throws<System.ArgumentNullException>(() => first.GetAnonymousFunctionEscape(null!));
    }
}
