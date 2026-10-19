// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace OpenDot.Sol.Text;

/// <summary>An immutable byte-preserving UTF-8 snapshot, including malformed input.</summary>
public sealed class SourceText
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[] _bytes;
    private readonly int[] _lineStarts;

    private SourceText(byte[] bytes, string path)
    {
        _bytes = bytes;
        Path = path;
        var lines = new List<int> { 0 };
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == '\r')
            {
                if (i + 1 < bytes.Length && bytes[i + 1] == '\n')
                {
                    i++;
                }
                lines.Add(i + 1);
            }
            else if (bytes[i] == '\n')
            {
                lines.Add(i + 1);
            }
        }
        _lineStarts = lines.ToArray();
    }

    public string Path { get; }
    public int Length => _bytes.Length;
    public int LineCount => _lineStarts.Length;
    public ReadOnlySpan<byte> Bytes => _bytes;

    public static SourceText FromBytes(ReadOnlySpan<byte> bytes, string path = "")
    {
        ArgumentNullException.ThrowIfNull(path);
        return new(bytes.ToArray(), path);
    }

    public static SourceText FromString(string text, string path = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(path);
        return new(StrictUtf8.GetBytes(text), path);
    }

    public ReadOnlySpan<byte> Slice(TextSpan span)
    {
        Validate(span);
        return _bytes.AsSpan(span.Start, span.Length);
    }

    public string GetText(TextSpan span) => Encoding.UTF8.GetString(Slice(span));
    public override string ToString() => Encoding.UTF8.GetString(_bytes);

    public TextSpan GetLineSpan(int line)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(line, LineCount);
        int end = line + 1 < LineCount ? _lineStarts[line + 1] : Length;
        while (end > _lineStarts[line] && _bytes[end - 1] is (byte)'\n' or (byte)'\r')
        {
            end--;
        }
        return TextSpan.FromBounds(_lineStarts[line], end);
    }

    public SourcePosition GetPosition(int byteOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteOffset, Length);
        int index = Array.BinarySearch(_lineStarts, byteOffset);
        int line = index >= 0 ? index : ~index - 1;
        int scalar = 0;
        int utf16 = 0;
        int lineStart = _lineStarts[line];
        if (line == 0 && byteOffset >= 3 && _bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            lineStart = 3;
        }
        for (int current = lineStart; current < byteOffset;)
        {
            var status = Rune.DecodeFromUtf8(_bytes.AsSpan(current), out Rune rune, out int consumed);
            if (status != OperationStatus.Done)
            {
                consumed = 1;
                rune = Rune.ReplacementChar;
            }
            if (current + consumed > byteOffset)
            {
                throw new ArgumentException("The offset splits a UTF-8 scalar.", nameof(byteOffset));
            }
            current += consumed;
            scalar++;
            utf16 += rune.Utf16SequenceLength;
        }
        return new(line, scalar, utf16);
    }

    public void Validate(TextSpan span)
    {
        if (span.End > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(span), "The span exceeds this snapshot.");
        }
    }
}
