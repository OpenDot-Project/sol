// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace OpenDot.Sol.Fir;

internal static class FirLiterals
{
    internal static BigInteger ParseInteger(string text)
    {
        text = text.Replace("_", "", StringComparison.Ordinal);
        bool negative = text.StartsWith('-');
        if (negative) { text = text[1..]; }
        int radix = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16
            : text.StartsWith("0o", StringComparison.OrdinalIgnoreCase) ? 8 : text.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ? 2 : 10;
        if (radix != 10) { text = text[2..]; }
        if (text.Length == 0) { throw new FormatException("Integer constant needs digits."); }
        BigInteger value = 0;
        foreach (char digit in text)
        {
            int number = digit is >= '0' and <= '9' ? digit - '0' : char.ToLowerInvariant(digit) - 'a' + 10;
            if (number < 0 || number >= radix) { throw new FormatException("Invalid numeric FIR constant."); }
            value = value * radix + number;
        }
        return negative ? -value : value;
    }

    internal static byte[] DecodeUtf8(string text)
    {
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"') { throw new FormatException("Invalid string FIR constant."); }
        var decoded = new StringBuilder();
        for (int index = 1; index < text.Length - 1; index++)
        {
            char character = text[index];
            if (character != '\\') { decoded.Append(character); continue; }
            char escape = text[++index];
            if (escape == 'u')
            {
                if (text[++index] != '{') { throw new FormatException("Invalid Unicode escape."); }
                int start = ++index;
                while (text[index] != '}') { index++; }
                int scalar = int.Parse(text.AsSpan(start, index - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                decoded.Append(char.ConvertFromUtf32(scalar));
            }
            else { decoded.Append(escape switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', '\\' => '\\', '"' => '"', '\'' => '\'', _ => throw new FormatException("Invalid string escape.") }); }
        }
        return new UTF8Encoding(false, true).GetBytes(decoded.ToString());
    }
}
