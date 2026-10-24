// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Diagnostics;
using OpenDot.Sol.Text;

namespace OpenDot.Sol.Fuzz;

internal static class FrontendOracle
{
    internal static bool Check(CorpusCase item)
        => Check(item.Bytes, item.Id + ".sol", item.Expectation);

    internal static bool CheckBytes(ReadOnlySpan<byte> bytes)
        => Check(bytes.ToArray(), "coverage.sol", null);

    private static bool Check(byte[] bytes, string path, string? expectation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var source = SourceText.FromBytes(bytes, path);
        var lex = Lexer.Lex(source, errorLimit: 64, cancellationToken: deadline.Token);
        ValidateTokens(lex);
        var first = SyntaxTree.Parse(source, errorLimit: 64, maxDepth: 64, cancellationToken: deadline.Token);
        var second = SyntaxTree.Parse(SourceText.FromBytes(bytes, source.Path), errorLimit: 64, maxDepth: 64,
            cancellationToken: deadline.Token);
        Corpus.Require(Fingerprint(first) == Fingerprint(second), "Frontend output is not deterministic.");
        Corpus.Require(TokenFingerprint(lex.Tokens) == TokenFingerprint(first.Tokens), "Parser lost lexical tokens/trivia.");
        if (expectation == "parse") { Corpus.Require(!first.HasErrors, "Valid grammar seed was rejected."); }
        if (expectation == "reject") { Corpus.Require(first.HasErrors, "Malformed UTF-8 seed was accepted."); }
        return first.HasErrors;
    }

    private static void ValidateTokens(LexResult lex)
    {
        Corpus.Require(lex.Tokens.Length is > 0 && lex.Tokens.Length <= lex.Source.Length + 1 &&
            lex.Tokens[^1].Kind == SyntaxKind.EndOfFileToken, "Invalid lexer termination/token bound.");
        int cursor = 0;
        foreach (var token in lex.Tokens)
        {
            foreach (var trivia in token.LeadingTrivia)
            {
                lex.Source.Validate(trivia.Span);
                Corpus.Require(trivia.Span.Start == cursor, "Lexer trivia does not roundtrip source bytes.");
                cursor = trivia.Span.End;
            }
            lex.Source.Validate(token.Span);
            Corpus.Require(token.Span.Start == cursor, "Lexer tokens do not roundtrip source bytes.");
            cursor = token.Span.End;
        }
        Corpus.Require(cursor == lex.Source.Length, "Lexer dropped source bytes.");
        ValidateDiagnostics(lex.Source, lex.Diagnostics);
    }

    private static string Fingerprint(SyntaxTree tree)
    {
        Corpus.Require(tree.Root.Kind == NodeKind.CompilationUnit && tree.Root.Span.Start == 0 &&
            tree.Root.Span.Length == tree.Source.Length, "Invalid CST root extent.");
        var text = new StringBuilder(TokenFingerprint(tree.Tokens));
        text.Append('|').Append(tree.ModuleName);
        var pending = new Stack<SyntaxNode>();
        pending.Push(tree.Root);
        int count = 0;
        while (pending.TryPop(out var node))
        {
            Corpus.Require(++count <= 16 * (tree.Source.Length + 1) + 1024, "Unbounded CST expansion.");
            tree.Source.Validate(node.Span);
            text.Append('|').Append((int)node.Kind).Append(':').Append(node.Span.Start).Append(':')
                .Append(node.Span.Length).Append(':').Append(node.Children.Length);
            for (int i = node.Children.Length - 1; i >= 0; i--) { pending.Push(node.Children[i]); }
        }
        ValidateDiagnostics(tree.Source, tree.Diagnostics);
        foreach (var diagnostic in tree.Diagnostics)
        {
            text.Append('|').Append(diagnostic.Code).Append(':').Append((int)diagnostic.Severity)
                .Append(':').Append(diagnostic.Span.Start).Append(':').Append(diagnostic.Span.Length)
                .Append(':').Append(diagnostic.Message);
        }
        return Corpus.Hash(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static string TokenFingerprint(IEnumerable<SyntaxToken> tokens)
    {
        var text = new StringBuilder();
        foreach (var token in tokens)
        {
            text.Append('|').Append((int)token.Kind).Append(':').Append(token.Span.Start).Append(':').Append(token.Span.Length);
            foreach (var trivia in token.LeadingTrivia)
            {
                text.Append('/').Append((int)trivia.Kind).Append(':').Append(trivia.Span.Start).Append(':').Append(trivia.Span.Length);
            }
        }
        return text.ToString();
    }

    private static void ValidateDiagnostics(SourceText source, IEnumerable<Diagnostic> diagnostics)
    {
        int count = 0;
        foreach (var diagnostic in diagnostics)
        {
            Corpus.Require(++count <= 128 && ReferenceEquals(diagnostic.Source, source), "Invalid diagnostic budget/snapshot.");
            source.Validate(diagnostic.Span);
        }
    }
}
