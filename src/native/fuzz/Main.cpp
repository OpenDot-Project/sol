// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Fuzz.h"
#include <Windows.h>
#include <bcrypt.h>
#include <cstdio>
// memcmp is used directly when validating the bounded native input format.
// NOLINTNEXTLINE(CppUnusedIncludeDirective)
#include <cstring>
#include <cstdlib>
#include <cwchar>
#include <filesystem>
#include <fstream>
#include <memory>
#include <string>
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

void Run(const wchar_t *pack, const wchar_t *report, const wchar_t *journal, const wchar_t *backend)
{
    std::ifstream source{std::filesystem::path(pack), std::ios::binary};
    std::array<char, 8> magic{};
    std::uint32_t count = 0, first = 0;
    source.read(magic.data(), static_cast<std::streamsize>(magic.size()));
    source.read(reinterpret_cast<char *>(&count), sizeof(count));
    source.read(reinterpret_cast<char *>(&first), sizeof(first));
    Require(source.good() && std::memcmp(magic.data(), "SFZP0001", 8) == 0 && count > 0 && count <= 500 &&
            first < 20000 && first + count <= 20000, "Invalid bounded native trace pack.");
    std::ofstream progress{std::filesystem::path(journal)};
    Require(progress.good(), "Cannot create native fuzz journal.");
    std::unique_ptr<Roots> roots;
    unsigned target = 0;
    for (std::uint32_t index = 0; index < count; ++index)
    {
        Input input{};
        std::array<unsigned char, 32> expected{};
        source.read(reinterpret_cast<char *>(input.data()), static_cast<std::streamsize>(input.size()));
        source.read(reinterpret_cast<char *>(expected.data()), static_cast<std::streamsize>(expected.size()));
        Require(source.good() && std::memcmp(input.data(), "SFZ1", 4) == 0 && input[4] >= 1 && input[4] <= 3 &&
                input[5] < 20 && input[6] == 1 && input[7] == 0 && Hash(input) == expected,
                "Corrupt native trace record.");
        if (index == 0)
        {
            target = input[4];
            if (target == 2)
            {
                Require(backend != nullptr, "LLVM backend path is required.");
                LoadBackend(backend);
            }
            else
            {
                roots = std::make_unique<Roots>();
            }
        }
        Require(input[4] == target, "Mixed native target pack.");
        progress << "case-";
        progress.width(5);
        progress.fill('0');
        progress << first + index << ' ';
        constexpr char Hex[] = "0123456789abcdef";
        for (auto byte : expected)
        {
            progress << Hex[byte >> 4] << Hex[byte & 15];
        }
        progress << '\n';
        progress.flush();
        Require(progress.good(), "Native fuzz progress journal failed.");
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
    std::ofstream result{std::filesystem::path(report)};
    result << "{\"schema_version\":1,\"status\":\"passed\",\"count\":" << count
        << ",\"first\":" << first << ",\"target\":" << target << "}\n";
    result.flush();
    Require(result.good(), "Cannot write native fuzz completion report.");
    std::printf("Native fuzz target %u: %u cases passed.\n", target, count);
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
        if (argc != 4 && argc != 5)
        {
            throw std::invalid_argument("Usage: NativeFuzz pack report journal [LLVM-native.dll]");
        }
        OpenDot::Sol::Fuzz::Run(argv[1], argv[2], argv[3], argc == 5 ? argv[4] : nullptr);
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
