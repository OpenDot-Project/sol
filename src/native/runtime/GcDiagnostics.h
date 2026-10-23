// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include "SolRT.h"
#include <array>
#include <atomic>
#include <cstdint>

namespace OpenDot::Sol::Runtime
{
enum class GcCounter : std::uint32_t
{
    RootSlots,
    RootFrames,
    StrongRoots,
    CacheSlotsReturned,
    MarkObjects,
    MarkReferenceSlots,
    MarkBatches,
    AssistBatches,
    AssistReferenceSlots,
    AssistObjects,
    ShadeCalls,
    NewMarks,
    AlreadyMarked,
    ReclaimedObjects,
    ReclaimedBytes,
    SweepObjects,
    SweepBatches,
    Collections,
    TerminationRetries,
    AllocationBytes,
    AllocationCalls,
    PressureWaits,
    RefillFailures,
    PhaseChanges,
    MarkMaxReferenceSlots,
    MarkMaxObjects,
    AssistDebtPaid,
    ActiveBarriers,
    IdleBarriers,
    Count
};

enum class GcHistogram : std::uint32_t
{
    SafepointWait,
    RootSnapshot,
    MarkBatch,
    AssistBatch,
    SweepBatch,
    Count
};

enum class GcEventKind : std::uint32_t
{
    StartRequested = 1,
    RootsScanned,
    MarkStarted,
    MarkBatch,
    AssistBatch,
    TerminationRequested,
    SweepStarted,
    SweepBatch,
    CycleCompleted,
    PressureWait,
    RefillFailure
};

inline constexpr std::array<std::uint64_t, 12> GcHistogramBounds{
    1000, 4000, 16000, 64000, 256000, 1000000, 4000000, 16000000,
    64000000, 256000000, 1000000000, UINT64_MAX};

struct GcDistribution final
{
    std::array<std::uint64_t, GcHistogramBounds.size()> Buckets{};
    std::uint64_t Samples = 0;
    std::uint64_t TotalNanoseconds = 0;
    std::uint64_t MaximumNanoseconds = 0;
};

/// Native ABI version 1 of diagnostics only; object/descriptor ABI remains provisional version 0.
struct GcDiagnosticSnapshot final
{
    std::uint64_t Version = 1;
    std::uint64_t Size = 0;
    std::uint64_t Epoch = 0;
    std::uint64_t Phase = 0;
    std::array<std::uint64_t, static_cast<std::size_t>(GcCounter::Count)> Counters{};
    std::array<GcDistribution, static_cast<std::size_t>(GcHistogram::Count)> Distributions{};
    std::uint64_t ReadyRegions = 0;
    std::uint64_t PeakReadyRegions = 0;
    std::uint64_t RegionClaims = 0;
    std::uint64_t RegionPublications = 0;
    std::uint64_t CoalescedPublications = 0;
    std::uint64_t FirstEvent = 0;
    std::uint64_t NextEvent = 1;
    std::uint64_t DroppedEvents = 0;
    std::uint64_t EventsEnabled = 0;
};

/// Numeric runtime events contain no object addresses, source strings, or user payload.
struct GcEvent final
{
    std::uint64_t Sequence = 0;
    std::uint64_t TimestampNanoseconds = 0;
    std::uint64_t Epoch = 0;
    std::uint64_t Kind = 0;
    std::uint64_t Work = 0;
    std::uint64_t DurationNanoseconds = 0;
};

static_assert(sizeof(GcEvent) == 48);
}

namespace OpenDot::Sol::Runtime::Heap
{
std::uint64_t MonotonicNanoseconds() noexcept;
std::uint64_t ElapsedNanoseconds(std::uint64_t start) noexcept;
std::uint64_t SaturatingAdd(std::uint64_t left, std::uint64_t right) noexcept;
std::uint64_t DistributionUpperBound(const GcDistribution &value, unsigned percentile);
const char *CounterName(GcCounter value);
const char *HistogramName(GcHistogram value);
const char *EventName(GcEventKind value);

class EventRing final
{
    std::array<GcEvent, 4096> _events{};
    std::uint64_t _next = 1;
    std::uint64_t _dropped = 0;
    bool _enabled = false;

public:
    void Enable(bool enabled) noexcept;
    void Reset() noexcept;
    void Append(GcEventKind kind, std::uint64_t epoch, std::uint64_t work, std::uint64_t duration) noexcept;
    std::uint32_t Read(std::uint64_t after, GcEvent *output, std::uint32_t capacity) const;
    std::uint64_t First() const noexcept;
    std::uint64_t Next() const noexcept;
    std::uint64_t Dropped() const noexcept;
    bool Enabled() const noexcept;
};

/// Shared counter mutations require the heap mutex, except the explicitly atomic idle-barrier count.
class Diagnostics final
{
    std::array<std::uint64_t, static_cast<std::size_t>(GcCounter::Count)> _counters{};
    std::array<GcDistribution, static_cast<std::size_t>(GcHistogram::Count)> _distributions{};
    std::atomic<std::uint64_t> _idleBarriers{0};

public:
    EventRing Events;
    void Increment(GcCounter counter, std::uint64_t count = 1) noexcept;
    void Maximum(GcCounter counter, std::uint64_t value) noexcept;
    void Observe(GcHistogram histogram, std::uint64_t duration) noexcept;
    void IdleBarrier() noexcept;
    GcDiagnosticSnapshot Snapshot() const noexcept;
    void Reset() noexcept;
};

bool ReadEventSetting();
}

extern "C"
{
SOL_RT_API bool sol_gc_diagnostics(OpenDot::Sol::Runtime::GcDiagnosticSnapshot *snapshot, std::uint64_t bytes);
SOL_RT_API std::uint32_t sol_gc_read_events(std::uint64_t after, OpenDot::Sol::Runtime::GcEvent *events,
                                            std::uint32_t capacity);
}
