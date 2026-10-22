// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;

namespace OpenDot.Sol.Text;

/// <summary>A half-open UTF-8 byte range within one source snapshot.</summary>
public readonly record struct TextSpan
{
    public TextSpan(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _ = checked(start + length);
        Start = start;
        Length = length;
    }

    public int Start { get; }
    public int Length { get; }
    public int End => Start + Length;
    public bool IsEmpty => Length == 0;
    public bool Contains(int offset) => offset >= Start && offset < End;

    public static TextSpan FromBounds(int start, int end) => new(start, checked(end - start));
}
