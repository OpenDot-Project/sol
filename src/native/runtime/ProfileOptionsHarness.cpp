// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "GcProfile.h"
#include <array>
#include <cstdio>
#include <string>

using namespace OpenDot::Sol::Runtime::Test;

namespace
{
bool Parse(std::array<std::wstring, 6> values, ProfileOptions &options, int count = 6)
{
    std::array<wchar_t *, 6> arguments{};
    for (unsigned index = 0; index < arguments.size(); ++index)
    {
        arguments[index] = values[index].data();
    }
    return ParseProfileOptions(count, arguments.data(), options);
}

bool ValidBounds()
{
    ProfileOptions options;
    if (!Parse({L"dense", L"16", L"1", L"1", L"1", L"0"}, options)
        || options.Nodes != 16 || options.Cycles != 1 || options.Roots != 1 || options.Seed != 0)
    {
        return false;
    }
    if (!Parse({L"wide", L"200000", L"10", L"4096", L"1", L"4294967295"}, options)
        || options.Nodes != 200000 || options.Seed != UINT32_MAX)
    {
        return false;
    }
    if (!Parse({L"concurrent", L"25000", L"10", L"32", L"8", L"1"}, options))
    {
        return false;
    }
    for (const auto *scenario : {L"dense", L"sparse", L"wide", L"churn", L"roots"})
    {
        if (!Parse({scenario, L"100", L"2", L"7", L"1", L"42"}, options))
        {
            return false;
        }
    }
    return true;
}

bool InvalidNumbers()
{
    const std::array<std::wstring, 6> baseline{L"dense", L"100", L"2", L"7", L"1", L"42"};
    for (unsigned field = 1; field < baseline.size(); ++field)
    {
        for (const auto *invalid : {L"", L"-1", L"+1", L" 1", L"1 ", L"1.0", L"1e2", L"0x10",
                                    L"16MiB", L"18446744073709551616", L"999999999999999999999999999999999999999"})
        {
            auto values = baseline;
            values[field] = invalid;
            ProfileOptions options;
            if (Parse(values, options))
            {
                return false;
            }
        }
    }
    return true;
}

bool InvalidCombinations()
{
    struct Input final
    {
        unsigned Field;
        const wchar_t *Value;
    };
    constexpr Input inputs[]{
        {0, L""}, {0, L"Dense"}, {0, L"other"}, {1, L"15"}, {1, L"200001"},
        {2, L"0"}, {2, L"11"}, {3, L"0"}, {3, L"4097"}, {4, L"0"}, {4, L"9"},
        {4, L"2"}, {5, L"4294967296"}};
    for (const auto &input : inputs)
    {
        std::array<std::wstring, 6> values{L"dense", L"100", L"2", L"7", L"1", L"42"};
        values[input.Field] = input.Value;
        ProfileOptions options;
        if (Parse(values, options))
        {
            return false;
        }
    }
    ProfileOptions options;
    if (Parse({L"concurrent", L"100", L"2", L"7", L"1", L"42"}, options)
        || Parse({L"concurrent", L"25001", L"10", L"7", L"8", L"42"}, options))
    {
        return false;
    }
    for (const int count : {0, 1, 5, 7})
    {
        if (Parse({L"dense", L"100", L"2", L"7", L"1", L"42"}, options, count))
        {
            return false;
        }
    }
    return true;
}

bool AllocationRanks()
{
    AllocationTimings empty;
    if (empty.Percentile(99) != 0)
    {
        return false;
    }
    for (unsigned count = 1; count <= 107; ++count)
    {
        AllocationTimings first;
        AllocationTimings second;
        for (unsigned value = 1; value <= count; ++value)
        {
            (value % 2 ? first : second).Record(count + 1ULL - value);
        }
        first.Merge(std::move(second));
        if (first.Nanoseconds.size() != count || first.Maximum != count
            || first.Total != count * (count + 1ULL) / 2)
        {
            return false;
        }
        for (unsigned percentile = 1; percentile <= 100; ++percentile)
        {
            if (first.Percentile(percentile) != (count * percentile + 99) / 100)
            {
                return false;
            }
        }
    }
    return true;
}
}

int RunProfileOptionsTests()
{
    unsigned index = 0;
    for (auto test : {ValidBounds, InvalidNumbers, InvalidCombinations, AllocationRanks})
    {
        if (!test())
        {
            std::fprintf(stderr, "Profile option fixture failed: %u\n", index);
            return 234;
        }
        ++index;
    }
    std::fprintf(
        stderr, "Profile options: numeric grammar, workload combinations, operation caps and exact ranks passed.\n");
    return 0;
}
