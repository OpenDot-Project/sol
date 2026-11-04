// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "JitEnvelope.h"
#include "../Checked.h"
#include <windows.h>
#include <bcrypt.h>
#include <algorithm>
#include <array>
#include <bit>
#include <cstddef>
// NOLINTNEXTLINE(CppUnusedIncludeDirective): Direct standard-library contract, including MSVC intrinsics.
#include <cstring>
#include <stdexcept>

namespace OpenDot::Sol::LlvmNative
{
namespace
{
struct EnvelopeHeader final
{
    std::array<char, 8> Magic;
    std::uint32_t Schema, HeaderSize;
    std::uint64_t PayloadSize, RuntimeContract;
    std::uint32_t FirSchema, LlvmMajor, LlvmMinor, LlvmPatch, Target, Flags;
    std::array<unsigned char, 32> Hash;
};

static_assert(sizeof(EnvelopeHeader) == 88);
static_assert(offsetof(EnvelopeHeader, RuntimeContract) == 24 && offsetof(EnvelopeHeader, Hash) == 56);
static_assert(std::endian::native == std::endian::little);

struct Algorithm final
{
    BCRYPT_ALG_HANDLE Handle = nullptr;

    explicit Algorithm(const wchar_t *name)
    {
        if (BCryptOpenAlgorithmProvider(&Handle, name, nullptr, 0) < 0)
        {
            throw std::runtime_error("Windows SHA-256 provider is unavailable.");
        }
    }

    Algorithm(const Algorithm &) = delete;
    Algorithm &operator=(const Algorithm &) = delete;
    Algorithm(Algorithm &&) = delete;
    Algorithm &operator=(Algorithm &&) = delete;

    ~Algorithm()
    {
        BCryptCloseAlgorithmProvider(Handle, 0);
    }
};

struct Hash final
{
    BCRYPT_HASH_HANDLE Handle = nullptr;

    explicit Hash(BCRYPT_ALG_HANDLE algorithm)
    {
        if (BCryptCreateHash(algorithm, &Handle, nullptr, 0, nullptr, 0, 0) < 0)
        {
            throw std::runtime_error("Windows SHA-256 hash creation failed.");
        }
    }

    Hash(const Hash &) = delete;
    Hash &operator=(const Hash &) = delete;
    Hash(Hash &&) = delete;
    Hash &operator=(Hash &&) = delete;

    ~Hash()
    {
        BCryptDestroyHash(Handle);
    }
};
}

std::span<const unsigned char> ReadJitEnvelope(std::span<const unsigned char> bytes, std::uint32_t flags)
{
    constexpr std::size_t maximum = 64ULL * 1024 * 1024;
    if (bytes.size() < sizeof(EnvelopeHeader) || bytes.size() > sizeof(EnvelopeHeader) + maximum)
    {
        throw std::invalid_argument("Invalid ORC executable payload size.");
    }
    EnvelopeHeader header{};
    std::memcpy(&header, bytes.data(), sizeof(header));
    if (std::memcmp(header.Magic.data(), "SOLJIT01", header.Magic.size()) != 0 || header.Schema != 1
        || header.HeaderSize != sizeof(header) || header.RuntimeContract != 0x534F4C0000000002ULL
        || header.FirSchema != 8 || header.LlvmMajor != 23 || header.LlvmMinor != 1 || header.LlvmPatch != 3
        || header.Target != 1 || flags > 1 || header.Flags != flags)
    {
        throw std::invalid_argument("Incompatible ORC envelope schema, target, LLVM, runtime, FIR, or flags.");
    }
    const auto payload = bytes.subspan(sizeof(header));
    constexpr std::array<unsigned char, 4> magic{0x42, 0x43, 0xC0, 0xDE};
    if (header.PayloadSize != payload.size() || payload.size() < magic.size()
        || std::memcmp(payload.data(), magic.data(), magic.size()) != 0)
    {
        throw std::invalid_argument("Invalid ORC bitcode length or signature.");
    }
    const Algorithm algorithm(BCRYPT_SHA256_ALGORITHM);
    const Hash hash(algorithm.Handle);
    std::array<unsigned char, 32> digest{};
    std::array<unsigned char, 4096> chunk{};
    auto remaining = payload;
    while (!remaining.empty())
    {
        const auto count = std::min(remaining.size(), chunk.size());
        std::memcpy(chunk.data(), remaining.data(), count);
        if (BCryptHashData(hash.Handle, chunk.data(), CheckedInteger<ULONG>(count), 0) < 0)
        {
            throw std::runtime_error("Windows SHA-256 payload hashing failed.");
        }
        remaining = remaining.subspan(count);
    }
    const auto status = BCryptFinishHash(hash.Handle, digest.data(), CheckedInteger<ULONG>(digest.size()), 0);
    if (status < 0 || digest != header.Hash)
    {
        throw std::invalid_argument("ORC payload SHA-256 validation failed.");
    }
    return payload;
}
}
