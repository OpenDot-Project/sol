// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "SolRT.h"
#include "CheckedAccess.h"
#include <tuple>
#include <bit>
#include "Gc.h"
#include "GcProfile.h"
#include "../Checked.h"
#include <limits>
#include <string>
#include <cstring>
#include <cwchar>
#include <array>

using namespace OpenDot::Sol::Runtime;
int RunGcTests() noexcept(false);
int RunAbiLayoutProbe() noexcept;
int RunGcModelTests();
int RunSpanTests() noexcept(false);
int RunSpanPoisonTest(bool freed) noexcept(false);
int RunRegionTests();
int RunHandleTests() noexcept(false);
int RunThreadAllocationTests() noexcept(false);
int RunThreadAllocationPoisonTest();
int RunMarkBitmapTests() noexcept(false);
int RunReadyWorkModel();
int RunBarrierPhaseModel();
int RunSpanTraceTests() noexcept(false);
int RunDiagnosticsTests() noexcept(false);
int RunPacingTests() noexcept;
int RunBarrierTests() noexcept(false);
int RunDescriptorFailure(const wchar_t *mode);
int RunTraceLayoutTests() noexcept(false);
int RunProfileOptionsTests() noexcept(false);
int RunReferenceArrayTests();
int RunFunctionArrayTests() noexcept(false);
int RunArrayAccessFailure(const wchar_t *mode) noexcept(false);

int wmain(int argc, wchar_t **argv)
{
    if (argc < 1 || !argv)
    {
        return 7;
    }
    const std::span commandArguments(argv, argc);
    if (argc == 2 && std::wcscmp(Detail::CheckedAt(commandArguments, 1), L"abi-layout") == 0)
    {
        return RunAbiLayoutProbe();
    }
    if (argc == 2)
    {
        const std::wstring_view mode(Detail::CheckedAt(commandArguments, 1));
        std::array<int, 2> values{};
        if (mode == L"checked-negative-index")
        {
            return Detail::CheckedAt(std::span{values}, -1);
        }
        if (mode == L"checked-end-index")
        {
            return Detail::CheckedAt(std::span{values}, values.size());
        }
        if (mode == L"checked-empty-index")
        {
            return Detail::CheckedAt(std::span<int>{}, 0);
        }
        if (mode == L"checked-short-slot")
        {
            return *Detail::StorageSlot<int>(std::as_writable_bytes(std::span{values}), sizeof(values) - 1);
        }
        if (mode == L"checked-unaligned-slot")
        {
            return *Detail::StorageSlot<int>(std::as_writable_bytes(std::span{values}), 1);
        }
        if (mode == L"checked-integer-range")
        {
            return Detail::Integer<std::uint8_t>(-1);
        }
    }
    using OpenDot::Sol::CheckedInteger;
    std::array<std::uint64_t, 2> storage{};
    *Detail::StorageSlot<std::uint64_t>(std::as_writable_bytes(std::span{storage}), sizeof(std::uint64_t)) = 17;
    if (storage.at(1) != 17)
    {
        return 95;
    }
    static_assert(CheckedInteger<std::uint8_t>(255) == 255);
    static_assert(CheckedInteger<std::int8_t>(-128) == -128);
    static_assert(CheckedInteger<std::uint64_t>((std::numeric_limits<std::int64_t>::max)())
                  == 0x7FFFFFFFFFFFFFFFULL);
    for (const int invalid : {-1, 256})
    {
        std::uint8_t unchanged = 23;
        if (OpenDot::Sol::TryInteger(invalid, unchanged) || unchanged != 23)
        {
            return 96;
        }
        bool rejected = false;
        try
        {
            std::ignore = CheckedInteger<std::uint8_t>(invalid);
        }
        catch (const std::out_of_range &)
        {
            rejected = true;
        }
        if (!rejected)
        {
            return 93;
        }
    }
    bool overflowRejected = false;
    try
    {
        std::ignore = CheckedInteger<std::int64_t>((std::numeric_limits<std::uint64_t>::max)());
    }
    catch (const std::out_of_range &)
    {
        overflowRejected = true;
    }
    if (!overflowRejected)
    {
        return 94;
    }
    // NOLINTNEXTLINE(clang-diagnostic-padded): Preserve natural ABI and compiler capture alignment.
    struct StringStorage
    {
        String Value;
        std::array<char, 4> Bytes;
    } stringStorage{};
    struct ArrayStorage
    {
        StringArray Value;
        std::array<String *, 2> Items;
    } arrayStorage{};
    static_assert(offsetof(StringStorage, Bytes) == 24);
    static_assert(offsetof(ArrayStorage, Items) == 32);
    const auto &constantString = stringStorage;
    const auto &constantArray = arrayStorage;
    if (stringStorage.Value.Data() != stringStorage.Bytes.data()
        || constantString.Value.Data() != constantString.Bytes.data()
        || arrayStorage.Value.Data() != arrayStorage.Items.data()
        || constantArray.Value.Data() != constantArray.Items.data())
    {
        return 91;
    }
    std::span{stringStorage.Value.Data(), 4}.subspan<3>().front() = 'x';
    std::span{arrayStorage.Value.Data(), 2}.subspan<1>().front() = &stringStorage.Value;
    if (constantString.Bytes.at(3) != 'x' || constantArray.Items.at(1) != &stringStorage.Value)
    {
        return 92;
    }
    if (argc > 1 && std::wcscmp(Detail::CheckedAt(commandArguments, 1), L"gc-profile") == 0)
    {
        return OpenDot::Sol::Runtime::Test::RunGcProfile(argc - 2, commandArguments.subspan(2).data());
    }
    if (argc == 2 && std::wcsncmp(Detail::CheckedAt(commandArguments, 1), L"descriptor-", 11) == 0)
    {
        return RunDescriptorFailure(std::wstring(Detail::CheckedAt(commandArguments, 1)).substr(11).c_str());
    }
    if (argc == 2 && std::wcscmp(Detail::CheckedAt(commandArguments, 1), L"cache-use-after-return") == 0)
    {
        return RunThreadAllocationPoisonTest();
    }
    if (argc == 2 && std::wcscmp(Detail::CheckedAt(commandArguments, 1), L"slot-use-after-free") == 0)
    {
        return RunSpanPoisonTest(true);
    }
    if (argc == 2 && std::wcscmp(Detail::CheckedAt(commandArguments, 1), L"slot-overflow") == 0)
    {
        return RunSpanPoisonTest(false);
    }
    if (argc == 2 && std::wcsncmp(Detail::CheckedAt(commandArguments, 1), L"array-", 6) == 0)
    {
        return RunArrayAccessFailure(std::wstring(Detail::CheckedAt(commandArguments, 1)).substr(6).c_str());
    }
    if (argc == 2 && std::wcscmp(Detail::CheckedAt(commandArguments, 1), L"heap-pressure") == 0)
    {
        sol_gc_attach();
        sol_gc_alloc(&sol_rt_object_type, 20ULL * 1024 * 1024);
        return 90;
    }
    if (argc == 2 && std::wcscmp(Detail::CheckedAt(commandArguments, 1), L"function-array") == 0)
    {
        return RunFunctionArrayTests();
    }
    constexpr std::array arguments{L"harness.exe", L"Sol", L"\u0130stanbul", L""};
    for (int iteration = 0; iteration < 100; ++iteration)
    {
        auto array = sol_rt_arguments(4, arguments.data());
        if (std::bit_cast<std::uintptr_t>(array) % 16 != 0 || sol_rt_array_length(array) != 3 || array->Stride != 8)
        {
            return 2;
        }
        auto value = sol_rt_array_get(array, 0);
        if (!value)
        {
            sol_rt_fail(7);
        }
        if (value->Object.Type != &sol_rt_string_type || value->ByteLength != 3 ||
            std::memcmp(value->Data(), "Sol", 3) != 0)
        {
            return 3;
        }
        auto unicode = sol_rt_array_get(array, 1);
        if (!unicode)
        {
            sol_rt_fail(7);
        }
        constexpr auto expected = std::to_array("\xC4\xB0stanbul");
        if (unicode->ByteLength != Detail::Integer<int>(expected.size() - 1) ||
            std::memcmp(unicode->Data(), expected.data(), expected.size() - 1) != 0)
        {
            return 4;
        }
        if (sol_rt_array_get(array, 2)->ByteLength != 0)
        {
            return 5;
        }
        sol_rt_shutdown();
    }
    // NOLINTNEXTLINE(clang-diagnostic-padded): Preserve natural ABI and compiler capture alignment.
    struct alignas(16) Literal
    {
        String Value;
        std::array<char, 4> Bytes;
    } literal{};
    literal.Value.ByteLength = 4;
    std::memcpy(literal.Bytes.data(), "a\0bc", 4);
    auto value = sol_rt_literal(&literal.Value);
    if (!value)
    {
        sol_rt_fail(7);
    }
    if (value->Object.Type != &sol_rt_string_type || std::memcmp(value->Data(), "a\0bc", 4) != 0)
    {
        return 6;
    }
    const int model = RunGcModelTests();
    if (model)
    {
        return model;
    }
    const int spans = RunSpanTests();
    if (spans)
    {
        return spans;
    }
    const int regions = RunRegionTests();
    if (regions)
    {
        return regions;
    }
    const int handles = RunHandleTests();
    if (handles)
    {
        return handles;
    }
    const int caches = RunThreadAllocationTests();
    if (caches)
    {
        return caches;
    }
    const std::array<int (*)(), 10> suites{RunMarkBitmapTests, RunReadyWorkModel, RunBarrierPhaseModel,
                                           RunSpanTraceTests, RunTraceLayoutTests, RunProfileOptionsTests,
                                           RunDiagnosticsTests, RunPacingTests, RunBarrierTests, RunGcTests};
    for (auto suite : suites)
    {
        if (const int failure = suite ? suite() : 7)
        {
            return failure;
        }
    }
    if (const int failure = RunReferenceArrayTests())
    {
        return failure;
    }
    return RunFunctionArrayTests();
}
