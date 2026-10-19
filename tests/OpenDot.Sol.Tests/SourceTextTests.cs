// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Text;
using OpenDot.Sol.Diagnostics;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class SourceTextTests
{
    [Fact]
    public void SnapshotCopiesCallerBytes()
    {
        byte[] original = Encoding.UTF8.GetBytes("abc");
        var source = SourceText.FromBytes(original);
        original[0] = (byte)'z';
        Assert.Equal("abc", source.ToString());
    }

    [Fact]
    public void UnicodeCoordinatesDistinguishBytesScalarsAndUtf16()
    {
        var source = SourceText.FromString("a😀é\r\nnext\n");
        Assert.Equal(new SourcePosition(0, 2, 3), source.GetPosition(5));
        Assert.Equal(new SourcePosition(1, 0, 0), source.GetPosition(9));
        Assert.Equal(new SourcePosition(2, 0, 0), source.GetPosition(source.Length));
        Assert.Equal("a😀é", source.GetText(source.GetLineSpan(0)));
        Assert.Equal("next", source.GetText(source.GetLineSpan(1)));
        Assert.True(source.GetLineSpan(2).IsEmpty);
        Assert.Throws<ArgumentException>(() => source.GetPosition(2));
    }

    [Fact]
    public void InvalidUtf8RemainsAvailableForDiagnostics()
    {
        var source = SourceText.FromBytes([0xFF, (byte)'a', 0xC0]);
        Assert.Equal(3, source.Length);
        Assert.Equal(new SourcePosition(0, 2, 2), source.GetPosition(2));
        Assert.Equal(new byte[] { 0xFF, (byte)'a', 0xC0 }, source.Bytes.ToArray());
    }

    [Fact]
    public void EmptyInputHasOneLineAndEofPosition()
    {
        var source = SourceText.FromString("");
        Assert.Equal(1, source.LineCount);
        Assert.Equal(new SourcePosition(0, 0, 0), source.GetPosition(0));
        Assert.Equal(new TextSpan(0, 0), source.GetLineSpan(0));
    }

    [Fact]
    public void LeadingBomDoesNotShiftVisibleColumns()
    {
        var source = SourceText.FromString("\uFEFFa😀");
        Assert.Equal(new SourcePosition(0, 0, 0), source.GetPosition(3));
        Assert.Equal(new SourcePosition(0, 2, 3), source.GetPosition(8));
        Assert.Throws<ArgumentException>(() => source.GetPosition(1));
    }

    [Fact]
    public void RejectsInvalidRangesAndUnpairedUtf16()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextSpan(-1, 0));
        Assert.Throws<OverflowException>(() => new TextSpan(int.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SourceText.FromString("x").Slice(new(0, 2)));
        Assert.Throws<EncoderFallbackException>(() => SourceText.FromString("\uD800"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Diagnostic("SOL1001", "Bad input.",
            SourceText.FromString("x"), new(1, 1)));
    }
}
