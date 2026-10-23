// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "GcInternal.h"
#include <algorithm>
#include <chrono>
#include <cstdlib>
#include <cstdio>
#include <cstring>
#include <limits>

namespace OpenDot::Sol::Runtime::Heap
{
std::uint64_t MonotonicNanoseconds() noexcept
{
    return static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count());
}

std::uint64_t ElapsedNanoseconds(std::uint64_t start) noexcept
{
    const auto end = MonotonicNanoseconds();
    return end >= start ? end - start : 0;
}

std::uint64_t SaturatingAdd(std::uint64_t left, std::uint64_t right) noexcept
{
    return right > UINT64_MAX - left ? UINT64_MAX : left + right;
}

std::uint64_t DistributionUpperBound(const GcDistribution &value, unsigned percentile)
{
    if (!percentile || percentile > 100)
    {
        sol_rt_fail(7);
    }
    if (!value.Samples)
    {
        return 0;
    }
    // Ceil(samples * p / 100), without overflowing the multiplication.
    const auto target = (value.Samples / 100) * percentile
                        + ((value.Samples % 100) * percentile + 99) / 100;
    std::uint64_t cumulative = 0;
    for (std::size_t index = 0; index < value.Buckets.size(); ++index)
    {
        cumulative = SaturatingAdd(cumulative, value.Buckets[index]);
        if (cumulative >= target)
        {
            return GcHistogramBounds[index];
        }
    }
    sol_rt_fail(7);
}

void EventRing::Enable(bool enabled) noexcept
{
    _enabled = enabled;
}

void EventRing::Reset() noexcept
{
    _next = 1;
    _dropped = 0;
    _enabled = false;
}

std::uint64_t EventRing::First() const noexcept
{
    return _next > _events.size() ? _next - _events.size() : 1;
}

std::uint64_t EventRing::Next() const noexcept
{
    return _next;
}

std::uint64_t EventRing::Dropped() const noexcept
{
    return _dropped;
}

bool EventRing::Enabled() const noexcept
{
    return _enabled;
}

void EventRing::Append(GcEventKind kind, std::uint64_t epoch, std::uint64_t work, std::uint64_t duration) noexcept
{
    if (!_enabled)
    {
        return;
    }
    if (_next == UINT64_MAX)
    {
        // Do not wrap sequence identities; saturate loss rather than fabricate an old sequence.
        _dropped = SaturatingAdd(_dropped, 1);
        return;
    }
    const auto sequence = _next++;
    if (sequence > _events.size())
    {
        _dropped = SaturatingAdd(_dropped, 1);
    }
    _events[(sequence - 1) % _events.size()] = {
        sequence, MonotonicNanoseconds(), epoch, static_cast<std::uint64_t>(kind), work, duration};
}

std::uint32_t EventRing::Read(std::uint64_t after, GcEvent *output, std::uint32_t capacity) const
{
    if (!capacity)
    {
        return 0;
    }
    if (!output)
    {
        sol_rt_fail(7);
    }
    if (after >= _next - 1)
    {
        return 0;
    }
    const auto begin = (std::max)(First(), after + 1);
    const auto count = static_cast<std::uint32_t>((std::min)(static_cast<std::uint64_t>(capacity), _next - begin));
    for (std::uint32_t index = 0; index < count; ++index)
    {
        output[index] = _events[(begin + index - 1) % _events.size()];
    }
    return count;
}

void Diagnostics::Increment(GcCounter counter, std::uint64_t count) noexcept
{
    auto &value = _counters[static_cast<std::size_t>(counter)];
    value = SaturatingAdd(value, count);
}

void Diagnostics::Maximum(GcCounter counter, std::uint64_t value) noexcept
{
    auto &current = _counters[static_cast<std::size_t>(counter)];
    current = (std::max)(current, value);
}

void Diagnostics::Observe(GcHistogram histogram, std::uint64_t duration) noexcept
{
    auto &value = _distributions[static_cast<std::size_t>(histogram)];
    value.Samples = SaturatingAdd(value.Samples, 1);
    value.TotalNanoseconds = SaturatingAdd(value.TotalNanoseconds, duration);
    value.MaximumNanoseconds = (std::max)(value.MaximumNanoseconds, duration);
    for (std::size_t index = 0; index < GcHistogramBounds.size(); ++index)
    {
        if (duration <= GcHistogramBounds[index])
        {
            value.Buckets[index] = SaturatingAdd(value.Buckets[index], 1);
            break;
        }
    }
}

void Diagnostics::IdleBarrier() noexcept
{
    _idleBarriers.fetch_add(1, std::memory_order_relaxed);
}

GcDiagnosticSnapshot Diagnostics::Snapshot() const noexcept
{
    GcDiagnosticSnapshot result;
    result.Size = sizeof(result);
    result.Counters = _counters;
    result.Counters[static_cast<std::size_t>(GcCounter::IdleBarriers)] = _idleBarriers.load(std::memory_order_relaxed);
    result.Distributions = _distributions;
    result.FirstEvent = Events.First();
    result.NextEvent = Events.Next();
    result.DroppedEvents = Events.Dropped();
    result.EventsEnabled = Events.Enabled();
    return result;
}

void Diagnostics::Reset() noexcept
{
    _counters.fill(0);
    _distributions = {};
    _idleBarriers.store(0, std::memory_order_relaxed);
    Events.Reset();
}

bool ReadEventSetting()
{
    char *text = nullptr;
    std::size_t length = 0;
    if (_dupenv_s(&text, &length, "SOL_GC_DIAGNOSTICS") != 0)
    {
        sol_rt_fail(6);
    }
    const std::unique_ptr<char, decltype(&std::free)> owned(text, std::free);
    if (!text || !std::strcmp(text, "0"))
    {
        return false;
    }
    if (!std::strcmp(text, "1"))
    {
        return true;
    }
    std::fprintf(stderr, "sol: invalid SOL_GC_DIAGNOSTICS; expected 0 or 1\n");
    std::_Exit(1);
}
}

extern "C"
{
bool sol_gc_diagnostics(OpenDot::Sol::Runtime::GcDiagnosticSnapshot *snapshot, std::uint64_t bytes)
{
    using namespace OpenDot::Sol::Runtime::Heap;
    if (!snapshot || bytes != sizeof(*snapshot))
    {
        return false;
    }
    auto &state = Get();
    const std::lock_guard guard(state.Mutex);
    *snapshot = state.Cost.Snapshot();
    snapshot->Epoch = state.Epoch;
    snapshot->Phase = static_cast<std::uint64_t>(state.Current.load(std::memory_order_acquire));
    snapshot->ReadyRegions = state.Work.Count();
    snapshot->PeakReadyRegions = state.Work.Peak();
    snapshot->RegionClaims = state.Work.Claims();
    snapshot->RegionPublications = state.Work.Publications();
    snapshot->CoalescedPublications = state.Work.Coalesced();
    return true;
}

std::uint32_t sol_gc_read_events(std::uint64_t after, OpenDot::Sol::Runtime::GcEvent *events, std::uint32_t capacity)
{
    auto &state = OpenDot::Sol::Runtime::Heap::Get();
    const std::lock_guard guard(state.Mutex);
    return state.Cost.Events.Read(after, events, capacity);
}
}
