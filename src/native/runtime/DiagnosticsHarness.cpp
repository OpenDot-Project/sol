// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "TraceHarnessSupport.h"
#include <algorithm>
#include <array>
#include <cstdio>
#include <cstring>
#include <thread>
#include <unordered_set>

using namespace OpenDot::Sol::Runtime;
using namespace OpenDot::Sol::Runtime::Heap;
using namespace OpenDot::Sol::Runtime::Test;

namespace
{
bool DisabledEvents()
{
    EventRing ring;
    for (unsigned index = 0; index < 9000; ++index)
    {
        ring.Append(GcEventKind::MarkBatch, 1, index, 3);
    }
    std::array<GcEvent, 2> output;
    return !ring.Enabled() && ring.Next() == 1 && ring.First() == 1 && ring.Dropped() == 0
           && ring.Read(0, output.data(), 2) == 0 && ring.Read(0, nullptr, 0) == 0;
}

bool RingPagination()
{
    EventRing ring;
    ring.Enable(true);
    for (unsigned index = 1; index <= 102; ++index)
    {
        ring.Append(GcEventKind::SweepBatch, 7, index * 2, index * 5);
    }
    std::array<GcEvent, 7> page;
    std::uint64_t after = 0;
    std::uint64_t timestamp = 0;
    unsigned total = 0;
    for (;;)
    {
        const auto count = ring.Read(after, page.data(), static_cast<std::uint32_t>(page.size()));
        if (!count)
        {
            break;
        }
        for (unsigned index = 0; index < count; ++index)
        {
            const auto &event = page[index];
            if (event.Sequence != after + 1 || event.Epoch != 7 || event.Work != event.Sequence * 2
                || event.DurationNanoseconds != event.Sequence * 5
                || event.Kind != static_cast<std::uint64_t>(GcEventKind::SweepBatch)
                || event.TimestampNanoseconds < timestamp)
            {
                return false;
            }
            after = event.Sequence;
            timestamp = event.TimestampNanoseconds;
            ++total;
        }
    }
    return total == 102 && ring.First() == 1 && ring.Next() == 103 && ring.Dropped() == 0
           && ring.Read(UINT64_MAX, page.data(), 7) == 0;
}

bool RingOverwrite()
{
    EventRing ring;
    ring.Enable(true);
    for (unsigned index = 1; index <= 10000; ++index)
    {
        ring.Append(GcEventKind::MarkBatch, index / 100, index, 0);
    }
    if (ring.First() != 5905 || ring.Next() != 10001 || ring.Dropped() != 5904)
    {
        return false;
    }
    std::array<GcEvent, 64> page;
    std::uint64_t after = 0;
    unsigned total = 0;
    for (;;)
    {
        const auto count = ring.Read(after, page.data(), static_cast<std::uint32_t>(page.size()));
        if (!count)
        {
            break;
        }
        for (unsigned index = 0; index < count; ++index)
        {
            if (page[index].Sequence != 5905ULL + total || page[index].Work != page[index].Sequence)
            {
                return false;
            }
            after = page[index].Sequence;
            ++total;
        }
    }
    ring.Enable(false);
    ring.Append(GcEventKind::MarkBatch, 1, 0, 0);
    if (ring.Next() != 10001 || total != 4096)
    {
        return false;
    }
    ring.Reset();
    return !ring.Enabled() && ring.Next() == 1 && ring.First() == 1 && ring.Dropped() == 0
           && ring.Read(0, page.data(), 64) == 0;
}

bool HistogramBoundaries()
{
    Diagnostics diagnostics;
    std::array<std::uint64_t, GcHistogramBounds.size()> expected{};
    std::uint64_t samples = 0;
    std::uint64_t sum = 0;
    for (std::size_t bucket = 0; bucket < GcHistogramBounds.size(); ++bucket)
    {
        const auto upper = GcHistogramBounds[bucket];
        const auto duration = bucket ? GcHistogramBounds[bucket - 1] + 1 : 0;
        diagnostics.Observe(GcHistogram::MarkBatch, duration);
        diagnostics.Observe(GcHistogram::MarkBatch, upper);
        expected[bucket] += 2;
        samples += 2;
        sum = SaturatingAdd(sum, SaturatingAdd(duration, upper));
    }
    const auto snapshot = diagnostics.Snapshot();
    const auto &distribution = snapshot.Distributions[static_cast<std::size_t>(GcHistogram::MarkBatch)];
    if (distribution.Buckets != expected || distribution.Samples != samples
        || distribution.TotalNanoseconds != sum || distribution.MaximumNanoseconds != UINT64_MAX)
    {
        return false;
    }
    if (DistributionUpperBound(distribution, 50) != GcHistogramBounds[5]
        || DistributionUpperBound(distribution, 95) != UINT64_MAX
        || DistributionUpperBound(distribution, 100) != UINT64_MAX)
    {
        return false;
    }
    return DistributionUpperBound(GcDistribution{}, 99) == 0;
}

bool PercentileRanks()
{
    for (unsigned count = 1; count <= 103; ++count)
    {
        Diagnostics diagnostics;
        std::vector<std::uint64_t> observations;
        for (unsigned index = 0; index < count; ++index)
        {
            const auto value = GcHistogramBounds[index % 11];
            observations.push_back(value);
            diagnostics.Observe(GcHistogram::AssistBatch, value);
        }
        std::sort(observations.begin(), observations.end());
        const auto snapshot = diagnostics.Snapshot();
        const auto &distribution = snapshot.Distributions[static_cast<std::size_t>(GcHistogram::AssistBatch)];
        for (unsigned percentile = 1; percentile <= 100; ++percentile)
        {
            const auto rank = (count * percentile + 99) / 100;
            if (DistributionUpperBound(distribution, percentile) != observations[rank - 1])
            {
                return false;
            }
        }
    }
    return true;
}

bool CounterSemantics()
{
    Diagnostics diagnostics;
    diagnostics.Increment(GcCounter::ReclaimedBytes, UINT64_MAX - 1);
    diagnostics.Increment(GcCounter::ReclaimedBytes, 100);
    diagnostics.Maximum(GcCounter::MarkMaxObjects, 20);
    diagnostics.Maximum(GcCounter::MarkMaxObjects, 3);
    std::vector<std::thread> threads;
    for (unsigned thread = 0; thread < 4; ++thread)
    {
        threads.emplace_back([&] {
            for (unsigned index = 0; index < 10000; ++index)
            {
                diagnostics.IdleBarrier();
            }
        });
    }
    for (auto &thread : threads)
    {
        thread.join();
    }
    const auto snapshot = diagnostics.Snapshot();
    if (snapshot.Version != 1 || snapshot.Size != sizeof(snapshot)
        || Counter(snapshot, GcCounter::ReclaimedBytes) != UINT64_MAX
        || Counter(snapshot, GcCounter::MarkMaxObjects) != 20
        || Counter(snapshot, GcCounter::IdleBarriers) != 40000)
    {
        return false;
    }
    diagnostics.Reset();
    const auto reset = diagnostics.Snapshot();
    for (auto value : reset.Counters)
    {
        if (value)
        {
            return false;
        }
    }
    return true;
}

bool NamesAreStable()
{
    std::unordered_set<std::string> counters;
    for (unsigned index = 0; index < static_cast<unsigned>(GcCounter::Count); ++index)
    {
        const auto *name = CounterName(static_cast<GcCounter>(index));
        if (!*name || !counters.emplace(name).second)
        {
            return false;
        }
        for (const char *letter = name; *letter; ++letter)
        {
            if ((*letter < 'a' || *letter > 'z') && *letter != '_')
            {
                return false;
            }
        }
    }
    std::unordered_set<std::string> histograms;
    for (unsigned index = 0; index < static_cast<unsigned>(GcHistogram::Count); ++index)
    {
        if (!histograms.emplace(HistogramName(static_cast<GcHistogram>(index))).second)
        {
            return false;
        }
    }
    std::unordered_set<std::string> events;
    for (unsigned index = 1; index <= static_cast<unsigned>(GcEventKind::RefillFailure); ++index)
    {
        if (!events.emplace(EventName(static_cast<GcEventKind>(index))).second)
        {
            return false;
        }
    }
    return std::strcmp(CounterName(GcCounter::RootSlots), "root_slots") == 0
           && std::strcmp(HistogramName(GcHistogram::SafepointWait), "safepoint_wait") == 0;
}

bool PublicSnapshot()
{
    sol_gc_attach();
    bool okay = !sol_gc_diagnostics(nullptr, sizeof(GcDiagnosticSnapshot));
    GcDiagnosticSnapshot incompatible;
    incompatible.Version = 99;
    okay = okay && !sol_gc_diagnostics(&incompatible, sizeof(incompatible) - 8) && incompatible.Version == 99;
    {
        PublicFrame roots(200);
        auto *object = sol_gc_alloc(&NodeType, sizeof(Node));
        for (unsigned index = 0; index < 200; ++index)
        {
            roots.Store(index, object);
        }
        const auto before = Snapshot();
        sol_gc_collect();
        const auto after = Snapshot();
        const auto &rootTime = after.Distributions[static_cast<std::size_t>(GcHistogram::RootSnapshot)];
        const auto &waitTime = after.Distributions[static_cast<std::size_t>(GcHistogram::SafepointWait)];
        okay = okay && after.Epoch > before.Epoch && after.Phase == 0 && after.ReadyRegions == 0
               && Counter(after, GcCounter::RootSlots) - Counter(before, GcCounter::RootSlots) == 400
               && Counter(after, GcCounter::RootFrames) - Counter(before, GcCounter::RootFrames) == 2
               && Counter(after, GcCounter::Collections) - Counter(before, GcCounter::Collections) == 1
               && rootTime.Samples == 2 && waitTime.Samples == 2
               && sol_gc_stats().ObjectCount == 1;
    }
    sol_gc_collect();
    okay = okay && sol_gc_stats().ObjectCount == 0;
    sol_gc_detach();
    sol_rt_shutdown();
    return okay;
}
}

int RunDiagnosticsTests()
{
    const auto cases = {DisabledEvents, RingPagination, RingOverwrite, HistogramBoundaries,
                        PercentileRanks, CounterSemantics, NamesAreStable, PublicSnapshot};
    unsigned index = 0;
    for (auto test : cases)
    {
        if (!test())
        {
            std::fprintf(stderr, "Diagnostics fixture failed: %u\n", index);
            return 230;
        }
        ++index;
    }
    std::fprintf(
        stderr,
        "Diagnostics: bounded event ring, pagination, histogram ranks, atomic counters, root snapshots passed.\n");
    return 0;
}
