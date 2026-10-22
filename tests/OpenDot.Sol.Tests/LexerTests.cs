// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Text;
using System.Threading;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LexerTests
{
    [Fact]
    public void CommentsUnicodeAndBomRoundtripWithoutLoss()
    {
        var source = SourceText.FromString("\uFEFFmodule unrelated.name\r\n//! Module 😀\nobject Main {\n// note\nfunc main(args: []String) { println(\"é\") }\n}\n/* tail */");
        var result = Lex(source);
        Assert.False(result.HasErrors);
        AssertRoundtrip(result);
        Assert.Equal(TriviaKind.ByteOrderMark, result.Tokens[0].LeadingTrivia[0].Kind);
        Assert.Equal(SyntaxKind.ModuleKeyword, result.Tokens[0].Kind);
        Assert.Equal(TriviaKind.BlockComment, result.Tokens[^1].LeadingTrivia[^1].Kind);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("10_000")]
    [InlineData("0xDE_AD")]
    [InlineData("0b10_01")]
    [InlineData("0o7_1")]
    [InlineData("1.0")]
    [InlineData("1e6")]
    [InlineData("2.5e-3")]
    [InlineData("\"\\u{1F600}\\0\\n\"")]
    [InlineData("'😀'")]
    [InlineData("'\\u{10FFFF}'")]
    [InlineData("$0")]
    [InlineData("$42")]
    public void ValidLiterals(string text)
    {
        var result = Lex(SourceText.FromString(text));
        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Tokens.Length);
        AssertRoundtrip(result);
    }

    [Theory]
    [InlineData("0x", "SOL1007")]
    [InlineData("0b102", "SOL1007")]
    [InlineData("0o8", "SOL1007")]
    [InlineData("1__0", "SOL1007")]
    [InlineData("0x_FF", "SOL1007")]
    [InlineData("123_", "SOL1007")]
    [InlineData("1e+", "SOL1007")]
    [InlineData("10u", "SOL1007")]
    [InlineData("\"\\q\"", "SOL1010")]
    [InlineData("'\\u{D800}'", "SOL1010")]
    [InlineData("'\\u{110000}'", "SOL1010")]
    [InlineData("'\\u{}'", "SOL1010")]
    [InlineData("'ab'", "SOL1009")]
    [InlineData("''", "SOL1009")]
    [InlineData("\"unfinished", "SOL1008")]
    [InlineData("/* unfinished", "SOL1005")]
    [InlineData("$00", "SOL1006")]
    [InlineData("$", "SOL1006")]
    [InlineData("é", "SOL1002")]
    [InlineData("\0", "SOL1004")]
    [InlineData("// \0", "SOL1004")]
    public void InvalidLiteralsProduceBoundedDiagnosticsAndRoundtrip(string text, string code)
    {
        var result = Lex(SourceText.FromString(text));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
        AssertRoundtrip(result);
    }

    [Fact]
    public void LongestOperatorAndContextualKeywordsArePreserved()
    {
        var result = Lex(SourceText.FromString("!== === >>= >> ?. ?: !! -> get set by throws bool sbyte byte"));
        Assert.Equal(new[] { SyntaxKind.BangEqualsEqualsToken, SyntaxKind.EqualsEqualsEqualsToken,
            SyntaxKind.GreaterGreaterEqualsToken, SyntaxKind.GreaterGreaterToken,
            SyntaxKind.QuestionDotToken, SyntaxKind.QuestionColonToken, SyntaxKind.BangBangToken,
            SyntaxKind.ArrowToken }, result.Tokens.Take(8).Select(token => token.Kind));
        Assert.All(result.Tokens.Skip(8).SkipLast(1), token => Assert.Equal(SyntaxKind.IdentifierToken, token.Kind));
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void DotWithoutFractionIsNotConsumedByNumber()
    {
        var result = Lex(SourceText.FromString("1.member"));
        Assert.Equal(new[] { SyntaxKind.IntegerLiteralToken, SyntaxKind.DotToken, SyntaxKind.IdentifierToken,
            SyntaxKind.EndOfFileToken }, result.Tokens.Select(token => token.Kind));
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void BlockCommentsDoNotNest()
    {
        var result = Lex(SourceText.FromString("/* outer /* inner */ val"));
        Assert.Equal(SyntaxKind.ValKeyword, result.Tokens[0].Kind);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void InvalidEncodingIsRejectedEvenInsideCommentsAndStrings()
    {
        foreach (byte[] bytes in new byte[][] { [0xFF], [(byte)'/', (byte)'/', 0xFF], [(byte)'"', 0xFF, (byte)'"'] })
        {
            var result = Lex(SourceText.FromBytes(bytes));
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL1001");
            AssertRoundtrip(result);
        }
    }

    [Fact]
    public void DiagnosticLimitDoesNotTruncateSource()
    {
        var result = Lex(SourceText.FromString("` ` ` ` `"), errorLimit: 2);
        Assert.Equal(2, result.Diagnostics.Length);
        Assert.Equal(6, result.Tokens.Length);
        AssertRoundtrip(result);
    }

    [Fact]
    public void CancellationIsObserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Lexer.Lex(SourceText.FromString(""), cancellationToken: cancellation.Token));
    }

    [Fact]
    public void ArbitraryBytesAlwaysProgressAndPreserveEveryByte()
    {
        var random = new Random(2026);
        for (int length = 0; length <= 1024; length += 7)
        {
            byte[] bytes = new byte[length];
            random.NextBytes(bytes);
            var source = SourceText.FromBytes(bytes);
            var first = Lex(source);
            var second = Lex(source);
            Assert.True(first.Tokens.Length <= length + 1);
            Assert.True(first.Diagnostics.Length <= 100);
            AssertRoundtrip(first);
            Assert.Equal(first.Tokens.Select(t => (t.Kind, t.Span)), second.Tokens.Select(t => (t.Kind, t.Span)));
            Assert.Equal(first.Diagnostics.Select(d => (d.Code, d.Span)), second.Diagnostics.Select(d => (d.Code, d.Span)));
        }
    }

    private static LexResult Lex(SourceText source, int errorLimit = 100) =>
        Lexer.Lex(source, errorLimit, TestContext.Current.CancellationToken);

    private static void AssertRoundtrip(LexResult result)
    {
        int cursor = 0;
        var bytes = new System.Collections.Generic.List<byte>();
        foreach (var token in result.Tokens)
        {
            foreach (var trivia in token.LeadingTrivia)
            {
                Assert.Equal(cursor, trivia.Span.Start);
                bytes.AddRange(result.Source.Slice(trivia.Span).ToArray());
                cursor = trivia.Span.End;
            }
            Assert.Equal(cursor, token.Span.Start);
            bytes.AddRange(result.Source.Slice(token.Span).ToArray());
            cursor = token.Span.End;
        }
        Assert.Equal(result.Source.Length, cursor);
        Assert.Equal(result.Source.Bytes.ToArray(), bytes.ToArray());
    }
}
