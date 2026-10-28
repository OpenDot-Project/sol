// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class CallableAliasEscapingParameterOverwriteTests
{
    [Theory]
    [InlineData(false, "{ seed }")]
    [InlineData(true, "func(): int { seed }")]
    public void EscapingParameterOverwriteStopsPublishingThePreviousCallable(bool anonymous, string initializer)
    {
        const string prefix = "module app\nfunc use(seed: int, @escaping replacement: () -> int): () -> int { ";
        var sources = SourceAnalysis.Create(AstTestSources.Session(prefix + "var callback: () -> int = "
            + initializer + "; callback = replacement; return callback }"), TestContext.Current.CancellationToken);
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);

        Assert.False(analysis.HasErrors, Describe(analysis));
        var declaration = Assert.Single(sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstFunction>(), function => function.Name == "use");
        Assert.True(Assert.Single(declaration.Parameters, parameter => parameter.Name == "replacement").IsEscaping);

        if (anonymous)
        {
            var escape = Assert.Single(sources.Lexical.AnonymousFunctionEscapes);
            Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
            Assert.DoesNotContain(escape.Uses, use => use.Kind is AnonymousFunctionEscapeUseKind.ReturnValue
                or AnonymousFunctionEscapeUseKind.CallArgument or AnonymousFunctionEscapeUseKind.HeapStorage);
        }
        else
        {
            var escape = Assert.Single(sources.Lexical.LambdaEscapes);
            Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
            Assert.DoesNotContain(escape.Uses, use => use.Kind is LambdaEscapeUseKind.ReturnValue
                or LambdaEscapeUseKind.CallArgument or LambdaEscapeUseKind.HeapStorage);
        }
    }

    [Theory]
    [InlineData(false, "{ seed }")]
    [InlineData(true, "func(): int { seed }")]
    public void AliasCreatedFromTheReplacementDoesNotPublishTheOverwrittenCallable(bool anonymous, string initializer)
    {
        const string prefix = "module app\nfunc use(seed: int, @escaping replacement: () -> int): () -> int { ";
        var sources = SourceAnalysis.Create(AstTestSources.Session(prefix + "var callback: () -> int = "
            + initializer + "; callback = replacement; val alias = callback; return alias }"),
            TestContext.Current.CancellationToken);
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);

        Assert.False(analysis.HasErrors, Describe(analysis));
        if (anonymous)
        { Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping,
            Assert.Single(sources.Lexical.AnonymousFunctionEscapes).Disposition); }
        else
        { Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping,
            Assert.Single(sources.Lexical.LambdaEscapes).Disposition); }
    }

    [Theory]
    [InlineData(false, "{ seed }")]
    [InlineData(true, "func(): int { seed }")]
    public void UnrelatedStatementsDoNotKeepTheOverwrittenCallableAlive(bool anonymous, string initializer)
    {
        const string prefix = "module app\nfunc use(seed: int, @escaping replacement: () -> int): () -> int { ";
        var sources = SourceAnalysis.Create(AstTestSources.Session(prefix + "var callback: () -> int = "
            + initializer + "; callback = replacement; val first = seed + 1; val second = first * 2; "
            + "return callback }"), TestContext.Current.CancellationToken);
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);

        Assert.False(analysis.HasErrors, Describe(analysis));
        if (anonymous)
        { Assert.Equal(AnonymousFunctionEscapeDisposition.ProvenNonEscaping,
            Assert.Single(sources.Lexical.AnonymousFunctionEscapes).Disposition); }
        else
        { Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping,
            Assert.Single(sources.Lexical.LambdaEscapes).Disposition); }
    }

    [Theory]
    [InlineData(false, "{ seed }")]
    [InlineData(true, "func(): int { seed }")]
    public void AliasCopiedBeforeOverwriteKeepsTheOriginalCallableLive(bool anonymous, string initializer)
    {
        const string prefix = "module app\nfunc use(seed: int, @escaping replacement: () -> int): () -> int { ";
        var sources = SourceAnalysis.Create(AstTestSources.Session(prefix + "var callback: () -> int = "
            + initializer + "; val original = callback; callback = replacement; return original }"),
            TestContext.Current.CancellationToken);

        if (anonymous)
        { Assert.Equal(AnonymousFunctionEscapeDisposition.MayEscape,
            Assert.Single(sources.Lexical.AnonymousFunctionEscapes).Disposition); }
        else
        { Assert.Equal(LambdaEscapeDisposition.MayEscape,
            Assert.Single(sources.Lexical.LambdaEscapes).Disposition); }
    }

    [Theory]
    [InlineData(false, "{ seed }")]
    [InlineData(true, "func(): int { seed }")]
    public void ConditionalParameterOverwriteDoesNotProveThatTheOriginalCallableWasRemoved(
        bool anonymous, string initializer)
    {
        const string prefix = "module app\nfunc use(flag: bool, seed: int, @escaping replacement: () -> int): () -> int { ";
        var sources = SourceAnalysis.Create(AstTestSources.Session(prefix + "var callback: () -> int = "
            + initializer + "; if flag { callback = replacement }; return callback }"),
            TestContext.Current.CancellationToken);

        if (anonymous)
        {
            var escape = Assert.Single(sources.Lexical.AnonymousFunctionEscapes);
            Assert.NotEqual(AnonymousFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        }
        else
        {
            var escape = Assert.Single(sources.Lexical.LambdaEscapes);
            Assert.NotEqual(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        }
    }

    [Theory]
    [InlineData(false, "{ seed }")]
    [InlineData(true, "func(): int { seed }")]
    public void UnannotatedReplacementDoesNotAcquireAnEscapingParameterContract(bool anonymous, string initializer)
    {
        const string prefix = "module app\nfunc use(seed: int, replacement: () -> int): () -> int { ";
        var sources = SourceAnalysis.Create(AstTestSources.Session(prefix + "var callback: () -> int = "
            + initializer + "; callback = replacement; return callback }"), TestContext.Current.CancellationToken);
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2264");
        Assert.False(Assert.Single(sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstFunction>(), function => function.Name == "use").Parameters
            .Single(parameter => parameter.Name == "replacement").IsEscaping);
        if (anonymous)
        { Assert.NotEqual(AnonymousFunctionEscapeDisposition.ProvenNonEscaping,
            Assert.Single(sources.Lexical.AnonymousFunctionEscapes).Disposition); }
        else
        { Assert.NotEqual(LambdaEscapeDisposition.ProvenNonEscaping,
            Assert.Single(sources.Lexical.LambdaEscapes).Disposition); }
    }

    private static string Describe(BodyAnalysis analysis) => string.Join('\n', analysis.Diagnostics.Select(diagnostic =>
        diagnostic.Code + " " + diagnostic.Message));
}
