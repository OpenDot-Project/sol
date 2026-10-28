// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LocalFunctionEscapeAnalysisTests
{
    private static LexicalAnalysis Analyze(string source) => LexicalAnalysis.Create(
        AstTestSources.Session(source), TestContext.Current.CancellationToken);

    private static AstFunction Function(LexicalAnalysis analysis, string name) => Assert.Single(
        analysis.Declarations.Files.SelectMany(file => file.Nodes).OfType<AstFunction>(),
        function => function.Name == name);

    private static LocalFunctionEscape Escape(LexicalAnalysis analysis, string name) =>
        LocalFunctionEscapeAnalysis.Create(analysis, TestContext.Current.CancellationToken)
            .GetEscape(Function(analysis, name));

    [Fact]
    public void DirectCallsIncludingSelfRecursionRemainNonEscaping()
    {
        var analysis = Analyze("module app\nfunc outer(seed: int): int { "
            + "func helper(): int = seed; func descend(value: int): int { "
            + "if value == 0 { return 0 }; descend(value - 1) }; helper() + descend(seed) }");
        var helper = Escape(analysis, "helper");
        var descend = Escape(analysis, "descend");

        Assert.Equal(LocalFunctionEscapeDisposition.ProvenNonEscaping, helper.Disposition);
        Assert.Equal(LocalFunctionEscapeUseKind.DirectInvocation, Assert.Single(helper.Uses).Kind);
        Assert.Equal(LocalFunctionEscapeDisposition.ProvenNonEscaping, descend.Disposition);
        Assert.Equal(2, descend.Uses.Count(use => use.Kind == LocalFunctionEscapeUseKind.DirectInvocation));
        Assert.Equal(descend.Uses.OrderBy(use => use.Site.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(use => use.Site.Origin.Span.Start).ThenBy(use => use.Kind), descend.Uses);
    }

    [Fact]
    public void ReturningPassingAndAggregatingFunctionValuesArePublications()
    {
        var returned = Analyze("module app\nfunc create(seed: int): () -> int { "
            + "func read(): int = seed; return read }");
        var returnEscape = Escape(returned, "read");
        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, returnEscape.Disposition);
        Assert.Contains(returnEscape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.ReturnValue);

        var forwarded = Analyze("module app\nfunc publish(@escaping callback: () -> int) {}\n"
            + "func create(seed: int) { func read(): int = seed; publish(read); val callbacks = [read] }");
        var forwardEscape = Escape(forwarded, "read");

        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, forwardEscape.Disposition);
        Assert.Contains(forwardEscape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.CallArgument);
        Assert.Contains(forwardEscape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.AggregateElement);
    }

    [Fact]
    public void LocalAliasChainRemainsNonEscapingWhenOnlyInvokedLocally()
    {
        var analysis = Analyze("module app\nfunc outer(seed: int) { func read(): int = seed; "
            + "val first = read; val alias = ((first)); alias() }");
        var escape = Escape(analysis, "read");

        Assert.Equal(LocalFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.LocalStorage);
        Assert.Single(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.LocalAlias);
        Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.DirectInvocation);
    }

    [Fact]
    public void ReturningAnAliasIsStillAnEscape()
    {
        var analysis = Analyze("module app\nfunc create(seed: int): () -> int { func read(): int = seed; "
            + "val alias = read; return alias }");
        var escape = Escape(analysis, "read");

        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.ReturnValue);
        Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.LocalStorage);
    }

    [Fact]
    public void ReplacingTheOnlyLocalFunctionAliasBeforeReturnDoesNotPublishTheLocalFunction()
    {
        var analysis = Analyze("module app\nfunc outer(seed: int, @escaping replacement: () -> int): () -> int { "
            + "func read(): int = seed; var callback: () -> int = read; callback = replacement; return callback }");
        var escape = Escape(analysis, "read");

        Assert.True(escape.Disposition == LocalFunctionEscapeDisposition.ProvenNonEscaping,
            string.Join("; ", escape.Uses.Select(use => $"{use.Kind}@{use.Site.GetType().Name}:{use.Site.Origin.Span.Start}")));
        Assert.DoesNotContain(escape.Uses, use => use.Kind is LocalFunctionEscapeUseKind.ReturnValue
            or LocalFunctionEscapeUseKind.CallArgument or LocalFunctionEscapeUseKind.HeapStorage);
    }

    [Fact]
    public void ConditionalOpaqueOverwriteKeepsTheUntakenLocalFunctionValueLive()
    {
        var analysis = Analyze("module app\nfunc outer(flag: bool, seed: int, "
            + "@escaping replacement: () -> int): () -> int { func read(): int = seed; "
            + "var callback: () -> int = read; if flag { callback = replacement }; return callback }");
        var escape = Escape(analysis, "read");

        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.ReturnValue);
    }

    [Fact]
    public void LoopOpaqueOverwriteDoesNotProveThePostLoopValue()
    {
        var analysis = Analyze("module app\nfunc outer(flag: bool, seed: int, "
            + "@escaping replacement: () -> int): () -> int { func read(): int = seed; "
            + "var callback: () -> int = read; for flag { callback = replacement; break }; return callback }");
        var escape = Escape(analysis, "read");

        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.ReturnValue);
    }

    [Fact]
    public void AliasDependentOverwriteDoesNotKillTheLocalFunctionValue()
    {
        var analysis = Analyze("module app\nfunc outer(seed: int): () -> int { func read(): int = seed; "
            + "var callback: () -> int = read; val alias = read; callback = alias; return callback }");
        var escape = Escape(analysis, "read");

        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.ReturnValue);
    }

    [Fact]
    public void IncompleteOverwriteKeepsTheOldValueConservative()
    {
        var analysis = Analyze("module app\nfunc outer(seed: int, @escaping replacement: () -> int): () -> int { "
            + "func read(): int = seed; func produce(): () -> int = replacement; "
            + "var callback: () -> int = read; callback = produce(); return callback }");
        var escape = Escape(analysis, "read");

        Assert.NotEqual(LocalFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.AliasMutation);
    }

    [Fact]
    public void AliasesUseTheResolvedNonescapingOrEscapingParameterContract()
    {
        var nonescaping = Analyze("module app\nfunc consume(callback: () -> int): int = callback()\n"
            + "func outer(seed: int): int { func read(): int = seed; val alias = read; consume(alias) }");
        var safe = Escape(nonescaping, "read");
        Assert.Equal(LocalFunctionEscapeDisposition.ProvenNonEscaping, safe.Disposition);
        Assert.Contains(safe.Uses, use => use.Kind == LocalFunctionEscapeUseKind.NonescapingCallArgument);

        var escaping = Analyze("module app\nfunc retain(@escaping callback: () -> int) {}\n"
            + "func outer(seed: int) { func read(): int = seed; val alias = read; retain(alias) }");
        var published = Escape(escaping, "read");
        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, published.Disposition);
        Assert.Contains(published.Uses, use => use.Kind == LocalFunctionEscapeUseKind.CallArgument);
    }

    [Fact]
    public void BranchAssignedAliasesTrackEveryFunctionAndJoinUse()
    {
        var analysis = Analyze("module app\nfunc outer(flag: bool, first: int, second: int): int { "
            + "func left(): int = first; func right(): int = second; var callback: () -> int; "
            + "if flag { callback = left } else { callback = right }; callback() }");
        var escapes = LocalFunctionEscapeAnalysis.Create(analysis, TestContext.Current.CancellationToken)
            .Escapes.Where(escape => escape.Function.Name is "left" or "right").ToArray();

        Assert.Equal(2, escapes.Length);
        Assert.All(escapes, escape =>
        {
            Assert.Equal(LocalFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
            Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.LocalAlias);
            Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.DirectInvocation);
        });
    }

    [Fact]
    public void CyclicAliasesTerminateAndPreserveTheirPublicationWitnesses()
    {
        var analysis = Analyze("module app\nfunc outer(seed: int) { func read(): int = seed; "
            + "var first: () -> int; var second = read; first = second; second = first; "
            + "first() }");
        var escape = Escape(analysis, "read");

        Assert.Equal(LocalFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Equal(2, escape.Uses.Count(use => use.Kind == LocalFunctionEscapeUseKind.LocalAlias));
        Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.DirectInvocation);
        Assert.Equal(escape.Uses.OrderBy(use => use.Site.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(use => use.Site.Origin.Span.Start).ThenBy(use => use.Kind), escape.Uses);
    }

    [Fact]
    public void UnsupportedAliasMutationDoesNotCreateALifetimeProof()
    {
        var analysis = Analyze("module app\nfunc outer(seed: int, replacement: () -> int) { "
            + "func read(): int = seed; var callback: () -> int = read; callback = replacement; callback() }");
        var escape = Escape(analysis, "read");

        Assert.Equal(LocalFunctionEscapeDisposition.Unknown, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.AliasMutation);
    }

    [Fact]
    public void AliasAggregateAndHeapStoresRemainEscapeCapable()
    {
        var aggregate = Analyze("module app\nfunc outer(seed: int) { func read(): int = seed; "
            + "val alias = read; val callbacks = [alias] }");
        var aggregateEscape = Escape(aggregate, "read");
        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, aggregateEscape.Disposition);
        Assert.Contains(aggregateEscape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.AggregateElement);

        var field = Analyze("module app\nclass Holder { var callback: () -> int }\n"
            + "func outer(holder: Holder, seed: int) { func read(): int = seed; "
            + "val alias = read; holder.callback = alias }");
        var fieldEscape = Escape(field, "read");
        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, fieldEscape.Disposition);
        Assert.Contains(fieldEscape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.HeapStorage);
    }

    [Fact]
    public void BorrowedCaptureSurvivesOnlyLocallyInvokedAliases()
    {
        var session = AstTestSources.Session("module app\nfunc outer(ref value: int): int { "
            + "func read(): int = value; val alias = read; alias() }");
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        Assert.Equal(LocalFunctionEscapeDisposition.ProvenNonEscaping,
            sources.Lexical.LocalFunctionEscapes.Single(escape => escape.Function.Name == "read").Disposition);
    }

    [Fact]
    public void BorrowedCaptureCannotEscapeThroughAnAlias()
    {
        var session = AstTestSources.Session("module app\nfunc outer(ref value: int): () -> int { "
            + "func read(): int = value; val alias = read; return alias }");
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
    }

    [Fact]
    public void AssigningFunctionValueToAnObjectFieldIsHeapPublication()
    {
        var analysis = Analyze("module app\nclass Holder { var callback: () -> int }\n"
            + "func outer(holder: Holder, seed: int) { func read(): int = seed; holder.callback = read }");
        var escape = Escape(analysis, "read");

        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.HeapStorage);
    }

    [Fact]
    public void CapturingLocalFunctionInNestedCallableKeepsEscapeUnknown()
    {
        var analysis = Analyze("module app\nfunc outer(seed: int) { func read(): int = seed; "
            + "val callback = { read() }; callback() }");
        var escape = Escape(analysis, "read");

        Assert.Equal(LocalFunctionEscapeDisposition.Unknown, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.CallableCapture);
        Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.DirectInvocation);
    }

    [Fact]
    public void SameSpelledDeclarationsKeepIndependentResolvedFunctionIdentities()
    {
        var analysis = Analyze("module app\nfunc outer(flag: bool) { "
            + "if flag { func local(): int = 1; local() }; "
            + "if !flag { func local(): int = 2; local() } }");
        var escapes = LocalFunctionEscapeAnalysis.Create(analysis, TestContext.Current.CancellationToken).Escapes;

        Assert.Equal(2, escapes.Length);
        Assert.All(escapes, escape =>
        {
            Assert.Equal("local", escape.Local.Name);
            Assert.Equal(LocalFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
            Assert.Equal(LocalFunctionEscapeUseKind.DirectInvocation, Assert.Single(escape.Uses).Kind);
        });
        Assert.NotSame(escapes[0].Local, escapes[1].Local);
        Assert.NotSame(escapes[0].Function, escapes[1].Function);
    }

    [Fact]
    public void EscapeQueryRejectsForeignAndNonLocalFunctions()
    {
        var first = Analyze("module app\nfunc top() {}\nfunc outer() { func inner() {} }");
        var second = Analyze("module app\nfunc outer() { func inner() {} }");
        var escapeAnalysis = LocalFunctionEscapeAnalysis.Create(first, TestContext.Current.CancellationToken);
        var top = Function(first, "top");
        var foreign = Function(second, "inner");

        Assert.Throws<ArgumentException>(() => escapeAnalysis.GetEscape(top));
        Assert.Throws<ArgumentException>(() => escapeAnalysis.GetEscape(foreign));
        Assert.Throws<ArgumentNullException>(() => escapeAnalysis.GetEscape(null!));
    }
}
