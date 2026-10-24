// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Fuzz.h"
#include <Windows.h>
#include <bcrypt.h>
#include <cstdio>
// memcmp is part of this translation unit's direct contract with the bounded trace format.
// NOLINTNEXTLINE(CppUnusedIncludeDirective)
#include <cstring>
#include <cstdlib>
#include <cwchar>
#include <fcntl.h>
#include <iostream>
#include <io.h>
#include <memory>
#include <tuple>

// These implementation-defined identifiers are the supported MSVC AddressSanitizer test interface.
// NOLINTNEXTLINE(bugprone-reserved-identifier, clang-diagnostic-reserved-identifier)
extern "C" void __asan_poison_memory_region(const volatile void *, std::size_t);
// NOLINTNEXTLINE(bugprone-reserved-identifier, clang-diagnostic-reserved-identifier)
extern "C" int __asan_address_is_poisoned(const volatile void *);

namespace OpenDot::Sol::Fuzz
{
__declspec(noinline) int ReadProbe(const unsigned char *value) noexcept
{
    return *value;
}

std::array<unsigned char, 32> Hash(const Input &input)
{
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    Require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) == 0,
            "Cannot initialize trace digest.");
    std::array<unsigned char, 32> digest{};
    const auto status = BCryptHash(algorithm, nullptr, 0, const_cast<PUCHAR>(input.data()),
                                   static_cast<ULONG>(input.size()), digest.data(), static_cast<ULONG>(digest.size()));
    const auto closed = BCryptCloseAlgorithmProvider(algorithm, 0);
    Require(status == 0 && closed == 0, "Cannot hash native trace.");
    return digest;
}

unsigned ParseTarget(const wchar_t *value)
{
    if (std::wcscmp(value, L"runtime") == 0)
    {
        return 1;
    }
    if (std::wcscmp(value, L"backend") == 0)
    {
        return 2;
    }
    if (std::wcscmp(value, L"gc") == 0)
    {
        return 3;
    }
    throw std::invalid_argument("Unknown native fuzz target.");
}

unsigned ParseShard(const wchar_t *value)
{
    wchar_t *end = nullptr;
    const auto shard = std::wcstoul(value, &end, 10);
    Require(end != value && *end == L'\0' && shard < 40, "Native fuzz shard must be between 0 and 39.");
    return static_cast<unsigned>(shard);
}

void Run(std::istream &source, std::ostream &output, unsigned expectedTarget, unsigned expectedShard)
{
    std::array<char, 8> magic{};
    std::uint32_t count = 0, first = 0;
    source.read(magic.data(), static_cast<std::streamsize>(magic.size()));
    source.read(reinterpret_cast<char *>(&count), sizeof(count));
    source.read(reinterpret_cast<char *>(&first), sizeof(first));
    const auto shardFirst = expectedShard * 500U;
    Require(source.good() && std::memcmp(magic.data(), "SFZP0001", 8) == 0 && count > 0 && count <= 500 &&
                first >= shardFirst && first <= shardFirst + 500U && count <= shardFirst + 500U - first,
            "Invalid bounded native trace pack.");
    std::unique_ptr<Roots> roots;
    unsigned target = 0;
    for (std::uint32_t index = 0; index < count; ++index)
    {
        Input input{};
        std::array<unsigned char, 32> expected{};
        source.read(reinterpret_cast<char *>(input.data()), static_cast<std::streamsize>(input.size()));
        source.read(reinterpret_cast<char *>(expected.data()), static_cast<std::streamsize>(expected.size()));
        Require(source.good() && std::memcmp(input.data(), "SFZ1", 4) == 0 && input[4] == expectedTarget &&
                    input[5] < 20 && input[6] == 1 && input[7] == 0 && Hash(input) == expected,
                "Corrupt native trace record.");
        if (index == 0)
        {
            target = input[4];
            if (target == 2)
            {
                LoadBackend();
            }
            else
            {
                roots = std::make_unique<Roots>();
            }
        }
        Require(input[4] == target && input[5] == expectedShard / 2,
                "Native trace target/family does not match its shard.");
        output << "case-";
        output.width(5);
        output.fill('0');
        output << first + index << ' ';
        constexpr char Hex[] = "0123456789abcdef";
        for (auto byte : expected)
        {
            output << Hex[byte >> 4] << Hex[byte & 15];
        }
        output << '\n';
        output.flush();
        Require(output.good(), "Native fuzz progress stream failed.");
        if (target == 1)
        {
            RuntimeCase(input, *roots);
        }
        else if (target == 2)
        {
            BackendCase(input);
        }
        else
        {
            GcCase(input, *roots);
        }
    }
    Require(source.peek() == std::char_traits<char>::eof(), "Trailing native trace bytes.");
    roots.reset();
    if (target != 2)
    {
        sol_rt_shutdown();
    }
    UnloadBackend();
    output << "{\"schema_version\":1,\"status\":\"passed\",\"count\":" << count << ",\"first\":" << first
           << ",\"target\":" << target << "}\n";
    output.flush();
    Require(output.good(), "Cannot write native fuzz completion report.");
}
}

int wmain(int argc, wchar_t **argv)
{
    std::ignore = SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    _set_error_mode(_OUT_TO_STDERR);
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
    try
    {
        if (argc == 2 && std::wcscmp(argv[1], L"--asan-probe") == 0)
        {
            const auto probe = std::make_unique<unsigned char[]>(16);
            __asan_poison_memory_region(probe.get(), 16);
            OpenDot::Sol::Fuzz::Require(__asan_address_is_poisoned(probe.get()) != 0,
                                        "ASan probe storage was not poisoned.");
            return OpenDot::Sol::Fuzz::ReadProbe(probe.get());
        }
        if (argc != 4 || std::wcscmp(argv[1], L"--replay") != 0)
        {
            throw std::invalid_argument("Usage: NativeFuzz --replay <runtime|backend|gc> <shard-00..39>");
        }
        if (_setmode(_fileno(stdin), _O_BINARY) == -1)
        {
            throw std::runtime_error("Cannot set binary mode for the native trace pipe.");
        }
        const auto target = OpenDot::Sol::Fuzz::ParseTarget(argv[2]);
        const auto shard = OpenDot::Sol::Fuzz::ParseShard(argv[3]);
        OpenDot::Sol::Fuzz::Run(std::cin, std::cout, target, shard);
        return 0;
    }
    catch (const std::exception &error)
    {
        if (std::fprintf(stderr, "Native fuzz failed: %s\n", error.what()) < 0)
        {
            return 2;
        }
        return 1;
    }
}
