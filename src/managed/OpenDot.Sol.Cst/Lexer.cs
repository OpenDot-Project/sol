// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using OpenDot.Sol.Diagnostics;
using OpenDot.Sol.Text;

namespace OpenDot.Sol.Cst;

/// <summary>A byte-preserving lexer. Newline insertion is a parser-context operation.</summary>
public sealed class Lexer
{
    private static readonly Dictionary<string, SyntaxKind> Keywords = new(StringComparer.Ordinal)
    {
        ["abstract"] = SyntaxKind.AbstractKeyword,
        ["annotation"] = SyntaxKind.AnnotationKeyword,
        ["as"] = SyntaxKind.AsKeyword,
        ["break"] = SyntaxKind.BreakKeyword,
        ["case"] = SyntaxKind.CaseKeyword,
        ["catch"] = SyntaxKind.CatchKeyword,
        ["class"] = SyntaxKind.ClassKeyword,
        ["companion"] = SyntaxKind.CompanionKeyword,
        ["const"] = SyntaxKind.ConstKeyword,
        ["constructor"] = SyntaxKind.ConstructorKeyword,
        ["continue"] = SyntaxKind.ContinueKeyword,
        ["data"] = SyntaxKind.DataKeyword,
        ["default"] = SyntaxKind.DefaultKeyword,
        ["defer"] = SyntaxKind.DeferKeyword,
        ["else"] = SyntaxKind.ElseKeyword,
        ["enum"] = SyntaxKind.EnumKeyword,
        ["false"] = SyntaxKind.FalseKeyword,
        ["final"] = SyntaxKind.FinalKeyword,
        ["finally"] = SyntaxKind.FinallyKeyword,
        ["for"] = SyntaxKind.ForKeyword,
        ["func"] = SyntaxKind.FuncKeyword,
        ["if"] = SyntaxKind.IfKeyword,
        ["import"] = SyntaxKind.ImportKeyword,
        ["in"] = SyntaxKind.InKeyword,
        ["init"] = SyntaxKind.InitKeyword,
        ["interface"] = SyntaxKind.InterfaceKeyword,
        ["internal"] = SyntaxKind.InternalKeyword,
        ["is"] = SyntaxKind.IsKeyword,
        ["module"] = SyntaxKind.ModuleKeyword,
        ["null"] = SyntaxKind.NullKeyword,
        ["object"] = SyntaxKind.ObjectKeyword,
        ["open"] = SyntaxKind.OpenKeyword,
        ["operator"] = SyntaxKind.OperatorKeyword,
        ["out"] = SyntaxKind.OutKeyword,
        ["override"] = SyntaxKind.OverrideKeyword,
        ["partial"] = SyntaxKind.PartialKeyword,
        ["private"] = SyntaxKind.PrivateKeyword,
        ["protected"] = SyntaxKind.ProtectedKeyword,
        ["public"] = SyntaxKind.PublicKeyword,
        ["ref"] = SyntaxKind.RefKeyword,
        ["return"] = SyntaxKind.ReturnKeyword,
        ["sealed"] = SyntaxKind.SealedKeyword,
        ["struct"] = SyntaxKind.StructKeyword,
        ["super"] = SyntaxKind.SuperKeyword,
        ["switch"] = SyntaxKind.SwitchKeyword,
        ["this"] = SyntaxKind.ThisKeyword,
        ["throw"] = SyntaxKind.ThrowKeyword,
        ["true"] = SyntaxKind.TrueKeyword,
        ["try"] = SyntaxKind.TryKeyword,
        ["type"] = SyntaxKind.TypeKeyword,
        ["unsafe"] = SyntaxKind.UnsafeKeyword,
        ["val"] = SyntaxKind.ValKeyword,
        ["var"] = SyntaxKind.VarKeyword,
        ["vararg"] = SyntaxKind.VarargKeyword,
        ["where"] = SyntaxKind.WhereKeyword,
    };

    // Longest spellings must precede their prefixes.
    private static readonly (string Text, SyntaxKind Kind)[] Operators =
    [
        ("===", SyntaxKind.EqualsEqualsEqualsToken), ("!==", SyntaxKind.BangEqualsEqualsToken),
        ("<<=", SyntaxKind.LessLessEqualsToken), (">>=", SyntaxKind.GreaterGreaterEqualsToken),
        ("++", SyntaxKind.PlusPlusToken), ("--", SyntaxKind.MinusMinusToken),
        ("+=", SyntaxKind.PlusEqualsToken), ("-=", SyntaxKind.MinusEqualsToken),
        ("*=", SyntaxKind.StarEqualsToken), ("/=", SyntaxKind.SlashEqualsToken),
        ("%=", SyntaxKind.PercentEqualsToken), ("&=", SyntaxKind.AmpersandEqualsToken),
        ("|=", SyntaxKind.PipeEqualsToken), ("^=", SyntaxKind.CaretEqualsToken),
        ("==", SyntaxKind.EqualsEqualsToken), ("!=", SyntaxKind.BangEqualsToken),
        ("<=", SyntaxKind.LessEqualsToken), (">=", SyntaxKind.GreaterEqualsToken),
        ("<<", SyntaxKind.LessLessToken), (">>", SyntaxKind.GreaterGreaterToken),
        ("&&", SyntaxKind.AmpersandAmpersandToken), ("||", SyntaxKind.PipePipeToken),
        ("->", SyntaxKind.ArrowToken), ("?.", SyntaxKind.QuestionDotToken),
        ("?:", SyntaxKind.QuestionColonToken), ("!!", SyntaxKind.BangBangToken),
        ("(", SyntaxKind.OpenParenToken), (")", SyntaxKind.CloseParenToken),
        ("[", SyntaxKind.OpenBracketToken), ("]", SyntaxKind.CloseBracketToken),
        ("{", SyntaxKind.OpenBraceToken), ("}", SyntaxKind.CloseBraceToken),
        (",", SyntaxKind.CommaToken), (".", SyntaxKind.DotToken),
        (":", SyntaxKind.ColonToken), (";", SyntaxKind.SemicolonToken), ("@", SyntaxKind.AtToken),
        ("+", SyntaxKind.PlusToken), ("-", SyntaxKind.MinusToken), ("*", SyntaxKind.StarToken),
        ("/", SyntaxKind.SlashToken), ("%", SyntaxKind.PercentToken), ("!", SyntaxKind.BangToken),
        ("~", SyntaxKind.TildeToken), ("&", SyntaxKind.AmpersandToken), ("|", SyntaxKind.PipeToken),
        ("^", SyntaxKind.CaretToken), ("=", SyntaxKind.EqualsToken), ("<", SyntaxKind.LessToken),
        (">", SyntaxKind.GreaterToken), ("?", SyntaxKind.QuestionToken),
    ];

    private readonly SourceText _source;
    private readonly CancellationToken _cancellation;
    private readonly int _errorLimit;
    private readonly List<Diagnostic> _diagnostics = [];
    private int _position;

    private Lexer(SourceText source, int errorLimit, CancellationToken cancellation)
    {
        _source = source;
        _errorLimit = errorLimit;
        _cancellation = cancellation;
    }

    public static LexResult Lex(SourceText source, int errorLimit = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(errorLimit);
        return new Lexer(source, errorLimit, cancellationToken).Run();
    }

    private LexResult Run()
    {
        ValidateEncoding();
        var tokens = ImmutableArray.CreateBuilder<SyntaxToken>();
        do
        {
            _cancellation.ThrowIfCancellationRequested();
            var trivia = ReadTrivia();
            int start = _position;
            SyntaxKind kind = AtEnd ? SyntaxKind.EndOfFileToken : ReadToken();
            tokens.Add(new(kind, TextSpan.FromBounds(start, _position), trivia));
            if (kind == SyntaxKind.EndOfFileToken)
            {
                break;
            }
        } while (true);
        _diagnostics.Sort((a, b) => a.Span.Start != b.Span.Start
            ? a.Span.Start.CompareTo(b.Span.Start) : string.CompareOrdinal(a.Code, b.Code));
        return new(_source, tokens.ToImmutable(), _diagnostics.ToImmutableArray());
    }

    private void ValidateEncoding()
    {
        for (int offset = 0; offset < _source.Length;)
        {
            _cancellation.ThrowIfCancellationRequested();
            var status = Rune.DecodeFromUtf8(_source.Bytes[offset..], out _, out int consumed);
            if (status != OperationStatus.Done)
            {
                Error("SOL1001", "Invalid UTF-8 byte sequence.", offset, 1);
                consumed = 1;
            }
            offset += consumed;
        }
    }

    private ImmutableArray<SyntaxTrivia> ReadTrivia()
    {
        var trivia = ImmutableArray.CreateBuilder<SyntaxTrivia>();
        while (!AtEnd)
        {
            _cancellation.ThrowIfCancellationRequested();
            int start = _position;
            TriviaKind kind;
            if (_position == 0 && Remaining.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                _position += 3;
                kind = TriviaKind.ByteOrderMark;
            }
            else if (Current is (byte)' ' or (byte)'\t' or (byte)'\v' or (byte)'\f')
            {
                do { _position++; } while (!AtEnd && Current is (byte)' ' or (byte)'\t' or (byte)'\v' or (byte)'\f');
                kind = TriviaKind.Whitespace;
            }
            else if (Current is (byte)'\n' or (byte)'\r')
            {
                if (Current == '\r' && Peek(1) == '\n')
                {
                    _position += 2;
                }
                else
                {
                    _position++;
                }
                kind = TriviaKind.Newline;
            }
            else if (Current == '/' && Peek(1) == '/')
            {
                kind = Peek(2) is (byte)'/' or (byte)'!' ? TriviaKind.DocumentationComment : TriviaKind.LineComment;
                _position += 2;
                while (!AtEnd && Current is not ((byte)'\r' or (byte)'\n'))
                {
                    ConsumeScalar(inComment: true);
                }
            }
            else if (Current == '/' && Peek(1) == '*')
            {
                kind = TriviaKind.BlockComment;
                _position += 2;
                while (!AtEnd && !(Current == '*' && Peek(1) == '/'))
                {
                    ConsumeScalar(inComment: true);
                }
                if (AtEnd)
                {
                    Error("SOL1005", "Unterminated block comment.", start, _position - start);
                }
                else
                {
                    _position += 2;
                }
            }
            else
            {
                break;
            }
            trivia.Add(new(kind, TextSpan.FromBounds(start, _position)));
        }
        return trivia.ToImmutable();
    }

    private SyntaxKind ReadToken()
    {
        if (IsIdentifierStart(Current))
        {
            int start = _position++;
            while (!AtEnd && IsIdentifierPart(Current)) { _position++; }
            string text = _source.GetText(TextSpan.FromBounds(start, _position));
            return Keywords.GetValueOrDefault(text, SyntaxKind.IdentifierToken);
        }
        if (IsDigit(Current)) { return ReadNumber(); }
        if (Current is (byte)'"' or (byte)'\'') { return ReadQuoted(); }
        if (Current == '$')
        {
            int start = _position++;
            while (!AtEnd && IsDigit(Current)) { _position++; }
            if (_position == start + 1 || (_position > start + 2 && _source.Bytes[start + 1] == '0'))
            {
                Error("SOL1006", "Expected a canonical positional parameter index such as $0.", start, _position - start);
            }
            return SyntaxKind.PositionalParameterToken;
        }
        foreach (var (text, kind) in Operators)
        {
            if (Matches(text))
            {
                _position += text.Length;
                return kind;
            }
        }
        int bad = _position;
        byte value = Current;
        var status = ConsumeScalar();
        if (value == 0)
        {
            Error("SOL1004", "NUL is only permitted inside a string or rune literal.", bad, 1);
        }
        else if (status == OperationStatus.Done)
        {
            Error("SOL1002", "Unexpected character; identifiers must use ASCII letters, digits, or underscore.", bad, _position - bad);
        }
        return SyntaxKind.BadToken;
    }

    private SyntaxKind ReadNumber()
    {
        int start = _position;
        if (Current == '0' && Peek(1) is (byte)'x' or (byte)'b' or (byte)'o')
        {
            int radix = Peek(1) == 'x' ? 16 : Peek(1) == 'b' ? 2 : 8;
            _position += 2;
            int digits = _position;
            while (!AtEnd && IsIdentifierPart(Current)) { _position++; }
            ValidateDigits(digits, _position, radix, start);
            return SyntaxKind.IntegerLiteralToken;
        }
        ReadDecimalDigits();
        bool floating = false;
        if (!AtEnd && Current == '.' && IsDigit(Peek(1)))
        {
            floating = true;
            _position++;
            ReadDecimalDigits();
        }
        if (!AtEnd && Current is (byte)'e' or (byte)'E')
        {
            floating = true;
            _position++;
            if (!AtEnd && Current is (byte)'+' or (byte)'-') { _position++; }
            ReadDecimalDigits();
        }
        if (!AtEnd && IsIdentifierStart(Current))
        {
            while (!AtEnd && IsIdentifierPart(Current)) { _position++; }
            Error("SOL1007", "Numeric literals do not accept suffixes.", start, _position - start);
        }
        return floating ? SyntaxKind.FloatLiteralToken : SyntaxKind.IntegerLiteralToken;
    }

    private void ReadDecimalDigits()
    {
        int start = _position;
        while (!AtEnd && (IsDigit(Current) || Current == '_')) { _position++; }
        ValidateDigits(start, _position, 10, start);
    }

    private void ValidateDigits(int start, int end, int radix, int literalStart)
    {
        bool valid = end > start;
        for (int i = start; i < end; i++)
        {
            int digit = DigitValue(_source.Bytes[i]);
            if (_source.Bytes[i] == '_')
            {
                valid &= i > start && i + 1 < end && DigitValue(_source.Bytes[i - 1]) is var before
                    && before >= 0 && before < radix && DigitValue(_source.Bytes[i + 1]) is var after
                    && after >= 0 && after < radix;
            }
            else { valid &= digit >= 0 && digit < radix; }
        }
        if (!valid)
        {
            Error("SOL1007", "Invalid digits or underscore placement in numeric literal.", literalStart, Math.Max(0, end - literalStart));
        }
    }

    private SyntaxKind ReadQuoted()
    {
        int start = _position;
        byte quote = Current;
        _position++;
        int scalars = 0;
        while (!AtEnd && Current != quote && Current is not ((byte)'\r' or (byte)'\n'))
        {
            if (Current == '\\') { ReadEscape(); }
            else { ConsumeScalar(); }
            scalars++;
        }
        if (AtEnd || Current != quote)
        {
            Error("SOL1008", "Unterminated literal.", start, _position - start);
        }
        else { _position++; }
        if (quote == '\'' && scalars != 1)
        {
            Error("SOL1009", "A rune literal must contain exactly one Unicode scalar.", start, _position - start);
        }
        return quote == '"' ? SyntaxKind.StringLiteralToken : SyntaxKind.RuneLiteralToken;
    }

    private void ReadEscape()
    {
        int start = _position++;
        if (AtEnd || Current is (byte)'\r' or (byte)'\n') { return; }
        byte kind = Current;
        _position++;
        if (kind is (byte)'0' or (byte)'n' or (byte)'r' or (byte)'t' or (byte)'\\' or (byte)'"' or (byte)'\'')
        {
            return;
        }
        if (kind == 'u' && !AtEnd && Current == '{')
        {
            _position++;
            int digits = 0;
            int value = 0;
            while (!AtEnd && DigitValue(Current) is var digit && digit >= 0)
            {
                if (digits < 6) { value = value * 16 + digit; }
                digits++;
                _position++;
            }
            if (!AtEnd && Current == '}')
            {
                _position++;
                if (digits is >= 1 and <= 6 && Rune.IsValid(value)) { return; }
            }
        }
        Error("SOL1010", "Invalid escape or Unicode scalar escape.", start, _position - start);
    }

    private OperationStatus ConsumeScalar(bool inComment = false)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (inComment && Current == 0)
        {
            Error("SOL1004", "NUL is not permitted in a comment.", _position, 1);
        }
        var status = Rune.DecodeFromUtf8(Remaining, out _, out int consumed);
        _position += status == OperationStatus.Done ? consumed : 1;
        return status;
    }

    private bool Matches(string text)
    {
        if (text.Length > Remaining.Length) { return false; }
        for (int i = 0; i < text.Length; i++)
        {
            if (Remaining[i] != text[i]) { return false; }
        }
        return true;
    }

    private void Error(string code, string message, int start, int length)
    {
        if (_diagnostics.Count < _errorLimit)
        {
            _diagnostics.Add(new(code, message, _source, new(start, length)));
        }
    }

    private bool AtEnd => _position >= _source.Length;
    private byte Current => _source.Bytes[_position];
    private ReadOnlySpan<byte> Remaining => _source.Bytes[_position..];
    private byte Peek(int distance) => _position + distance < _source.Length ? _source.Bytes[_position + distance] : (byte)0;
    private static bool IsIdentifierStart(byte value) => value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or (byte)'_';
    private static bool IsIdentifierPart(byte value) => IsIdentifierStart(value) || IsDigit(value);
    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';
    private static int DigitValue(byte value) => value is >= (byte)'0' and <= (byte)'9' ? value - '0'
        : value is >= (byte)'a' and <= (byte)'f' ? value - 'a' + 10
        : value is >= (byte)'A' and <= (byte)'F' ? value - 'A' + 10 : -1;
}
