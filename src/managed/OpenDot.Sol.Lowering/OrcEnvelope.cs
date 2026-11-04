// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace OpenDot.Sol.Lowering;

/// <summary>Bounded executable payload container. LLVM module verification remains mandatory after decoding.</summary>
internal static class OrcEnvelope
{
    internal const int HeaderSize = 88;
    internal const int MaximumPayloadSize = 64 * 1024 * 1024;
    private const uint WindowsX64 = 1;
    private const uint StaticRuntime = 1;

    internal static byte[] Create(ReadOnlySpan<byte> bitcode, bool staticRuntime)
    {
        ValidateBitcodePrefix(bitcode);
        byte[] output = new byte[checked(HeaderSize + bitcode.Length)];
        "SOLJIT01"u8.CopyTo(output);
        Write32(output, 8, 1); Write32(output, 12, HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(16), (ulong)bitcode.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(24), RuntimeContract.Id);
        Write32(output, 32, 8);
        Write32(output, 36, 23); Write32(output, 40, 1); Write32(output, 44, 3);
        Write32(output, 48, WindowsX64); Write32(output, 52, staticRuntime ? StaticRuntime : 0);
        SHA256.HashData(bitcode, output.AsSpan(56, 32));
        bitcode.CopyTo(output.AsSpan(HeaderSize));
        return output;
    }

    internal static ReadOnlySpan<byte> Read(ReadOnlySpan<byte> envelope, bool staticRuntime)
    {
        if (envelope.Length < HeaderSize || envelope.Length > HeaderSize + MaximumPayloadSize
            || !envelope[..8].SequenceEqual("SOLJIT01"u8))
        { throw new InvalidDataException("Invalid or oversized ORC executable payload."); }
        if (Read32(envelope, 8) != 1 || Read32(envelope, 12) != HeaderSize
            || BinaryPrimitives.ReadUInt64LittleEndian(envelope[24..]) != RuntimeContract.Id
            || Read32(envelope, 32) != 8 || Read32(envelope, 36) != 23
            || Read32(envelope, 40) != 1 || Read32(envelope, 44) != 3
            || Read32(envelope, 48) != WindowsX64
            || Read32(envelope, 52) != (staticRuntime ? StaticRuntime : 0))
        { throw new InvalidDataException("Incompatible ORC envelope schema, target, LLVM, runtime, FIR, or flags."); }
        ulong length = BinaryPrimitives.ReadUInt64LittleEndian(envelope[16..]);
        if (length != (ulong)(envelope.Length - HeaderSize))
        { throw new InvalidDataException("ORC payload length does not match its envelope."); }
        var bitcode = envelope[HeaderSize..];
        ValidateBitcodePrefix(bitcode);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bitcode, digest);
        if (!CryptographicOperations.FixedTimeEquals(digest, envelope.Slice(56, 32)))
        { throw new InvalidDataException("ORC payload SHA-256 mismatch."); }
        return bitcode;
    }

    private static void ValidateBitcodePrefix(ReadOnlySpan<byte> bitcode)
    {
        ReadOnlySpan<byte> magic = [0x42, 0x43, 0xC0, 0xDE];
        if (bitcode.Length < 4 || bitcode.Length > MaximumPayloadSize || !bitcode[..4].SequenceEqual(magic))
        { throw new InvalidDataException("ORC payload must contain bounded binary LLVM bitcode."); }
    }

    private static uint Read32(ReadOnlySpan<byte> bytes, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    private static void Write32(Span<byte> bytes, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);
}
