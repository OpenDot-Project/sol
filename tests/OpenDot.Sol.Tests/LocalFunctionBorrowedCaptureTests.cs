// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LocalFunctionBorrowedCaptureTests
{
    [Fact]
    public void DirectLocalInvocationCanBorrowARefParameter()
    {
        var analysis = Analyze("func read(ref value: int): int { "
            + "func get(): int = value; get() }");

        Assert.False(analysis.HasErrors, Describe(analysis));
    }

    [Fact]
    public void LocalFunctionPassedToAKnownNonescapingParameterKeepsItsBorrowedLifetime()
    {
        var session = AstTestSources.Session("module app\nfunc consume(callback: () -> int): int = callback()\n"
            + "func read(ref value: int): int { func get(): int = value; consume(get) }");
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        var function = sources.Lexical.LocalFunctionEscapes.Single(escape =>
            escape.Function.Name == "get");

        Assert.Equal(LocalFunctionEscapeDisposition.ProvenNonEscaping, function.Disposition);
        Assert.Contains(function.Uses, use =>
            use.Kind == LocalFunctionEscapeUseKind.NonescapingCallArgument);
        Assert.Same(function, sources.Lexical.GetLocalFunctionEscape(function.Function));
    }

    [Fact]
    public void LocalFunctionPassedToAnEscapingParameterCannotCarryABorrowedCapture()
    {
        var analysis = Analyze("func retain(@escaping callback: () -> int): () -> int = callback\n"
            + "func read(ref value: int): () -> int { func get(): int = value; retain(get) }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
    }

    [Fact]
    public void LocalFunctionPassedToAnIndirectConsumerRemainsUnknownAndCannotCarryBorrow()
    {
        var analysis = Analyze("func read(ref value: int, consume: (() -> int) -> int): int { "
            + "func get(): int = value; consume(get) }");
        var function = analysis.Sources.Lexical.LocalFunctionEscapes.Single(escape =>
            escape.Function.Name == "get");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
        Assert.Equal(LocalFunctionEscapeDisposition.MayEscape, function.Disposition);
    }

    [Fact]
    public void LocalFunctionEscapeQueryIsBoundToItsLexicalSnapshot()
    {
        var first = SourceAnalysis.Create(AstTestSources.Session("module app\n"
            + "func outer(seed: int) { func nested(): int = seed; nested() }"),
            TestContext.Current.CancellationToken).Lexical;
        var second = SourceAnalysis.Create(AstTestSources.Session("module app\n"
            + "func outer(seed: int) { func nested(): int = seed; nested() }"),
            TestContext.Current.CancellationToken).Lexical;
        var foreign = second.LocalFunctionEscapes.Single().Function;

        Assert.Throws<System.ArgumentException>(() => first.GetLocalFunctionEscape(foreign));
    }

    [Fact]
    public void UnusedLocalFunctionDoesNotEscapeItsBorrowedCapture()
    {
        var analysis = Analyze("func create(ref value: int): int { "
            + "func unused(): int = value; 0 }");
        var function = analysis.Sources.Lexical.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<OpenDot.Sol.Ast.AstFunction>().Single(candidate => candidate.Name == "unused");
        var escape = LocalFunctionEscapeAnalysis.Create(analysis.Sources.Lexical,
            TestContext.Current.CancellationToken).GetEscape(function);

        Assert.False(analysis.HasErrors, Describe(analysis));
        Assert.Equal(LocalFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
    }

    [Fact]
    public void ReturningLocalFunctionWithBorrowedCaptureIsRejected()
    {
        var analysis = Analyze("func create(ref value: int): () -> int { "
            + "func read(): int = value; return read }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
    }

    [Fact]
    public void ReturningLocalFunctionWithRefLikeCaptureIsRejected()
    {
        var analysis = Analyze("ref struct View { val length: int = 1 }\n"
            + "func create(view: View): () -> int { func read(): int = view.length; return read }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
    }

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session("module app\n" + source);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static string Describe(BodyAnalysis analysis) => string.Join('\n', analysis.Diagnostics.Select(diagnostic =>
        diagnostic.Code + " " + diagnostic.Message));
}
