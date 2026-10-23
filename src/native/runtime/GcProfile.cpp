// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "GcProfile.h"
#include "GcInternal.h"
#include <windows.h>
#include <algorithm>
#include <cstdio>
#include <cwchar>
#include <iterator>

namespace OpenDot::Sol::Runtime::Test
{
namespace
{
bool Number(const wchar_t *text, unsigned minimum, unsigned maximum, unsigned &value)
{
    if (!text || !*text)
    {
        return false;
    }
    std::uint64_t parsed = 0;
    for (const auto *cursor = text; *cursor; ++cursor)
    {
        if (*cursor < L'0' || *cursor > L'9')
        {
            return false;
        }
        const auto digit = static_cast<unsigned>(*cursor - L'0');
        if (parsed > (static_cast<std::uint64_t>(maximum) - digit) / 10)
        {
            return false;
        }
        parsed = parsed * 10 + digit;
    }
    if (parsed < minimum || parsed > maximum)
    {
        return false;
    }
    value = static_cast<unsigned>(parsed);
    return true;
}

bool Scenario(const wchar_t *text, ProfileScenario &scenario)
{
    struct Choice final
    {
        const wchar_t *Name;
        ProfileScenario Value;
    };
    constexpr Choice choices[]{
        {L"dense", ProfileScenario::Dense}, {L"sparse", ProfileScenario::Sparse},
        {L"wide", ProfileScenario::Wide}, {L"churn", ProfileScenario::Churn},
        {L"roots", ProfileScenario::Roots}, {L"concurrent", ProfileScenario::Concurrent}};
    for (const auto &choice : choices)
    {
        if (std::wcscmp(text, choice.Name) == 0)
        {
            scenario = choice.Value;
            return true;
        }
    }
    return false;
}
}

const char *ScenarioName(ProfileScenario scenario)
{
    switch (scenario)
    {
    case ProfileScenario::Dense:
        return "dense";
    case ProfileScenario::Sparse:
        return "sparse";
    case ProfileScenario::Wide:
        return "wide";
    case ProfileScenario::Churn:
        return "churn";
    case ProfileScenario::Roots:
        return "roots";
    case ProfileScenario::Concurrent:
        return "concurrent";
    }
    sol_rt_fail(7);
}

bool ParseProfileOptions(int count, wchar_t **arguments, ProfileOptions &options)
{
    if (count != 6 || !Scenario(arguments[0], options.Scenario)
        || !Number(arguments[1], 16, 200000, options.Nodes)
        || !Number(arguments[2], 1, 10, options.Cycles)
        || !Number(arguments[3], 1, 4096, options.Roots)
        || !Number(arguments[4], 1, 8, options.Threads)
        || !Number(arguments[5], 0, UINT32_MAX, options.Seed))
    {
        return false;
    }
    // Only the concurrent workload accepts multiple mutators. Reject unused settings.
    if ((options.Scenario != ProfileScenario::Concurrent && options.Threads != 1)
        || (options.Scenario == ProfileScenario::Concurrent && options.Threads < 2))
    {
        return false;
    }
    return static_cast<std::uint64_t>(options.Nodes) * options.Cycles * options.Threads <= 2000000;
}

void AllocationTimings::Record(std::uint64_t duration)
{
    Nanoseconds.push_back(duration);
    Total = Heap::SaturatingAdd(Total, duration);
    Maximum = (std::max)(Maximum, duration);
}

void AllocationTimings::Merge(AllocationTimings &&other)
{
    Total = Heap::SaturatingAdd(Total, other.Total);
    Maximum = (std::max)(Maximum, other.Maximum);
    Nanoseconds.insert(Nanoseconds.end(), std::make_move_iterator(other.Nanoseconds.begin()),
                       std::make_move_iterator(other.Nanoseconds.end()));
}

std::uint64_t AllocationTimings::Percentile(unsigned percentile) const
{
    if (Nanoseconds.empty())
    {
        return 0;
    }
    if (!percentile || percentile > 100)
    {
        sol_rt_fail(7);
    }
    auto sorted = Nanoseconds;
    const auto rank = (sorted.size() * percentile + 99) / 100;
    std::nth_element(sorted.begin(), sorted.begin() + static_cast<std::ptrdiff_t>(rank - 1), sorted.end());
    return sorted[rank - 1];
}

int RunGcProfile(int count, wchar_t **arguments)
{
    ProfileResult result;
    if (!ParseProfileOptions(count, arguments, result.Options))
    {
        std::fprintf(
            stderr, "gc-profile expects: dense|sparse|wide|churn|roots|concurrent nodes cycles roots threads seed\n");
        return 64;
    }
#if defined(__SANITIZE_ADDRESS__)
    result.AddressSanitized = true;
#endif
    SYSTEM_INFO system{};
    GetSystemInfo(&system);
    result.LogicalProcessors = system.dwNumberOfProcessors;
    sol_gc_attach();
    {
        auto &state = Heap::Get();
        const std::lock_guard guard(state.Mutex);
        result.HeapLimit = state.Limit;
    }
    const auto started = Heap::MonotonicNanoseconds();
    const bool okay = ExecuteProfile(result);
    result.ElapsedNanoseconds = Heap::ElapsedNanoseconds(started);
    if (!okay)
    {
        std::fprintf(stderr, "GC profile failed its reachability/reclamation checkpoint.\n");
        sol_gc_detach();
        sol_rt_shutdown();
        return 65;
    }
    if (!sol_gc_diagnostics(&result.Diagnostics, sizeof(result.Diagnostics)))
    {
        sol_rt_fail(7);
    }
    result.Heap = sol_gc_stats();
    result.Allocator = sol_gc_allocator_stats();
    result.Cache = sol_gc_thread_allocation_stats();
    result.Events.resize(4096);
    const auto read = sol_gc_read_events(0, result.Events.data(), static_cast<std::uint32_t>(result.Events.size()));
    result.Events.resize(read);
    sol_gc_detach();
    sol_rt_shutdown();
    const auto json = SerializeProfile(result);
    if (std::fwrite(json.data(), 1, json.size(), stdout) != json.size() || std::fflush(stdout) != 0)
    {
        return 74;
    }
    return 0;
}
}
