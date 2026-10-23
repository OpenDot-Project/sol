// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "GcInternal.h"
#include <algorithm>

namespace OpenDot::Sol::Runtime::Heap
{
namespace
{
bool Park(State &state, std::unique_lock<std::mutex> &guard, Phase phase)
{
    const auto started = MonotonicNanoseconds();
    state.Cost.Increment(GcCounter::PhaseChanges);
    state.Cost.Events.Append(phase == Phase::Start ? GcEventKind::StartRequested : GcEventKind::TerminationRequested,
                             state.Epoch, state.Threads.size(), 0);
    state.Current = phase;
    state.Changed.notify_all();
    state.Changed.wait(guard, [&] {
        return state.Stop || std::all_of(state.Threads.begin(), state.Threads.end(), [](const auto &thread) {
            return thread->Mode != ThreadMode::Running;
        });
    });
    state.Cost.Observe(GcHistogram::SafepointWait, ElapsedNanoseconds(started));
    return !state.Stop;
}

void RecordSweepBatch(State &state, std::uint64_t started, std::uint64_t count)
{
    if (!count)
    {
        return;
    }
    const auto elapsed = ElapsedNanoseconds(started);
    state.Cost.Increment(GcCounter::SweepBatches);
    state.Cost.Observe(GcHistogram::SweepBatch, elapsed);
    state.Cost.Events.Append(GcEventKind::SweepBatch, state.Epoch, count, elapsed);
}

void Sweep(State &state, std::unique_lock<std::mutex> &guard)
{
    state.Current = Phase::Sweep;
    state.Cost.Events.Append(GcEventKind::SweepStarted, state.Epoch, state.Objects.size(), 0);
    state.Changed.notify_all();
    state.Live = 0;
    const auto *last = state.Objects.empty() ? nullptr : state.Objects.back().get();
    unsigned batchCount = 0;
    std::uint64_t batchBytes = 0;
    auto batchStarted = MonotonicNanoseconds();
    for (auto entry = state.Objects.begin(); entry != state.Objects.end() && !state.Stop;)
    {
        auto &allocation = **entry;
        const bool finalEntry = &allocation == last;
        batchBytes += allocation.Bytes;
        ++batchCount;
        state.Cost.Increment(GcCounter::SweepObjects);
        if (!allocation.Tracing->Marked(allocation.Storage.Slot, state.Epoch))
        {
            state.Index.erase(allocation.Object);
            state.Bytes -= allocation.Bytes;
            state.Cost.Increment(GcCounter::ReclaimedObjects);
            state.Cost.Increment(GcCounter::ReclaimedBytes, allocation.Bytes);
            state.Allocator.Sweep(allocation.Storage, true, guard);
            entry = state.Objects.erase(entry);
        }
        else
        {
            state.Live += allocation.Bytes;
            state.Allocator.Sweep(allocation.Storage, false, guard);
            ++entry;
        }
        if (finalEntry)
        {
            break;
        }
        if (batchCount == 64 || batchBytes >= 256 * 1024)
        {
            RecordSweepBatch(state, batchStarted, batchCount);
            guard.unlock();
            std::this_thread::yield();
            guard.lock();
            batchCount = 0;
            batchBytes = 0;
            batchStarted = MonotonicNanoseconds();
        }
    }
    RecordSweepBatch(state, batchStarted, batchCount);
}
}

void Run()
{
    auto &state = Get();
    std::unique_lock guard(state.Mutex);
    while (!state.Stop)
    {
        state.Changed.wait(guard, [&] {
            return state.Stop || state.Requested > state.Completed;
        });
        if (state.Stop)
        {
            break;
        }
        ++state.Epoch;
        if (!Park(state, guard, Phase::Start))
        {
            break;
        }
        for (const auto &thread : state.Threads)
        {
            const auto cached = thread->AllocationCache.Stats().CachedSlots;
            thread->AllocationCache.Flush(state.Allocator);
            state.Cost.Increment(GcCounter::CacheSlotsReturned, cached);
        }
        state.Allocator.BeginCycle();
        Roots(state);
        state.Current = Phase::Mark;
        state.Cost.Events.Append(GcEventKind::MarkStarted, state.Epoch, state.Work.Count(), 0);
        state.Changed.notify_all();
        for (;;)
        {
            while (!state.Work.Empty() && !state.Stop)
            {
                (void)TraceBatch(state, TraceBudget{}, false);
                guard.unlock();
                std::this_thread::yield();
                guard.lock();
            }
            if (state.Stop || !Park(state, guard, Phase::Terminate))
            {
                return;
            }
            Roots(state);
            if (state.Work.Empty())
            {
                break;
            }
            state.Cost.Increment(GcCounter::TerminationRetries);
            state.Current = Phase::Mark;
            state.Changed.notify_all();
        }
        Sweep(state, guard);
        if (state.Stop)
        {
            return;
        }
        state.Completed = state.Epoch;
        ++state.Collections;
        state.Cost.Increment(GcCounter::Collections);
        state.Cost.Events.Append(GcEventKind::CycleCompleted, state.Epoch, state.Live, 0);
        state.Threshold = PacingPolicy::NextThreshold(state.Live, state.Limit);
        state.Current = Phase::Idle;
        state.Changed.notify_all();
    }
}
}
