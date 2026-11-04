// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Buffers.Binary;
using System.IO;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class OrcEnvelopeTests
{
    private static byte[] Bitcode => [0x42, 0x43, 0xC0, 0xDE, 1, 2, 3, 4];

    [Fact]
    public void EncodingMatchesTheFixedLittleEndianWireContract()
    {
        byte[] expected = Convert.FromHexString(
            "534F4C4A495430310100000058000000080000000000000002000000004C4F53" +
            "0800000017000000010000000300000001000000010000009440C8223EF613F8" +
            "018755DCB7514A1C2581AD533DD146B606F9039C7163AC424243C0DE01020304");
        Assert.Equal(expected, OrcEnvelope.Create(Bitcode, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PayloadIsDeterministicAndPinsTheRuntimeLinkage(bool staticRuntime)
    {
        byte[] envelope = OrcEnvelope.Create(Bitcode, staticRuntime);
        Assert.Equal(envelope, OrcEnvelope.Create(Bitcode, staticRuntime));
        Assert.Equal(Bitcode, OrcEnvelope.Read(envelope, staticRuntime).ToArray());
        Assert.Throws<InvalidDataException>(() => OrcEnvelope.Read(envelope, !staticRuntime));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(36)]
    [InlineData(40)]
    [InlineData(44)]
    [InlineData(48)]
    [InlineData(52)]
    [InlineData(56)]
    [InlineData(92)]
    public void CorruptEnvelopeAndPayloadFieldsAreRejected(int offset)
    {
        byte[] envelope = OrcEnvelope.Create(Bitcode, false); envelope[offset] ^= 0x80;
        Assert.Throws<InvalidDataException>(() => OrcEnvelope.Read(envelope, false));
    }

    [Fact]
    public void TruncationTrailingDataAndForgedWideLengthsAreRejected()
    {
        byte[] envelope = OrcEnvelope.Create(Bitcode, false);
        for (int length = 0; length < envelope.Length; length++)
        {
            Assert.Throws<InvalidDataException>(() => OrcEnvelope.Read(envelope.AsSpan(0, length), false));
        }
        Assert.Throws<InvalidDataException>(() => OrcEnvelope.Read([.. envelope, 0], false));
        BinaryPrimitives.WriteUInt64LittleEndian(envelope.AsSpan(16), ulong.MaxValue);
        Assert.Throws<InvalidDataException>(() => OrcEnvelope.Read(envelope, false));
        Assert.Throws<InvalidDataException>(() => OrcEnvelope.Create([0x42, 0x43], false));
        Assert.Throws<InvalidDataException>(() => OrcEnvelope.Create("define i32 @main() { ret i32 0 }"u8, false));
    }
}
