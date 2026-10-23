// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "GcInternal.h"
#include <algorithm>
#include <cstdlib>

namespace OpenDot::Sol::Runtime::Heap
{
namespace
{
bool Park(State &state, std::unique_lock<std::mutex> &guard, Phase phase)
{
    state.Current = phase;
    state.Changed.notify_all();
    state.Changed.wait(guard, [&] {
        return state.Stop || std::all_of(state.Threads.begin(), state.Threads.end(), [](const auto &thread) {
            return thread->Mode != ThreadMode::Running;
        });
    });
    return !state.Stop;
}

// At most 64 reference slots are inspected with the heap lock held. No graph walk occurs in a handshake.
void ScanBatch(State &state)
{
    auto *entry = state.Gray;
    auto *object = entry->Object;
    const auto *shape = static_cast<const ScanShape *>(object->Type->ScanShape);
    if (!shape || shape->Count == 0)
    {
        state.Gray = entry->NextGray;
        return;
    }
    const auto *array = object->Type->Kind == 2 ? reinterpret_cast<const StringArray *>(object) : nullptr;
    if (array && (array->Length < 0 || array->Stride == 0))
    {
        sol_rt_fail(7);
    }
    const auto count = array ? static_cast<std::uint64_t>(array->Length) * shape->Count : shape->Count;
    for (unsigned budget = 0; entry->Cursor < count && budget < 64; ++budget, ++entry->Cursor)
    {
        const auto index = entry->Cursor;
        const auto offset = array
                                ? sizeof(StringArray) + (index / shape->Count) * array->Stride
                                  + shape->Offsets[index % shape->Count]
                                : shape->Offsets[index];
        if (offset < sizeof(Header) || offset > entry->Bytes - sizeof(Header *) || offset % alignof(Header *) != 0)
        {
            sol_rt_fail(7);
        }
        auto **slot = reinterpret_cast<Header **>(reinterpret_cast<char *>(object) + offset);
        Shade(state, std::atomic_ref(*slot).load(std::memory_order_acquire));
    }
    // Shade may have prepended new gray work. Unlink this entry only after its last batch.
    if (entry->Cursor == count)
    {
        auto **link = &state.Gray;
        while (*link != entry)
        {
            link = &(*link)->NextGray;
        }
        *link = entry->NextGray;
    }
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
        state.Allocator.BeginCycle();
        Roots(state);
        state.Current = Phase::Mark;
        state.Changed.notify_all();
        for (;;)
        {
            while (state.Gray && !state.Stop)
            {
                ScanBatch(state);
                guard.unlock();
                std::this_thread::yield();
                guard.lock();
            }
            if (state.Stop)
            {
                return;
            }
            if (!Park(state, guard, Phase::Terminate))
            {
                return;
            }
            Roots(state);
            if (!state.Gray)
            {
                break;
            }
            // New root work is processed with mutators running, never drained while parked.
            state.Current = Phase::Mark;
            state.Changed.notify_all();
        }
        state.Current = Phase::Sweep;
        state.Changed.notify_all();
        state.Live = 0;
        // Freeze the sweep boundary: mutators can append black allocations faster than a sweep.
        // Chasing those allocations would prevent cycle completion under sustained allocation.
        const auto *last = state.Objects.empty() ? nullptr : state.Objects.back().get();
        unsigned batchCount = 0;
        std::uint64_t batchBytes = 0;
        for (auto entry = state.Objects.begin(); entry != state.Objects.end() && !state.Stop;)
        {
            auto &allocation = **entry;
            const bool finalEntry = &allocation == last;
            batchBytes += allocation.Bytes;
            if (allocation.Mark != state.Epoch)
            {
                // Weak cleanup occurs incrementally during concurrent sweep, not during termination.
                for (auto &[identity, handle] : state.Handles)
                {
                    (void)identity;
                    if (handle->Weak && handle->Value == allocation.Object)
                    {
                        handle->Value = nullptr;
                    }
                }
                state.Index.erase(allocation.Object);
                state.Bytes -= allocation.Bytes;
                state.Allocator.Sweep(allocation.Storage, true);
                entry = state.Objects.erase(entry);
            }
            else
            {
                state.Live += allocation.Bytes;
                state.Allocator.Sweep(allocation.Storage, false);
                ++entry;
            }
            if (finalEntry)
            {
                break;
            }
            // Bound lock ownership without yielding once per tiny allocation. Per-object yields let
            // allocating mutators outrun reclamation even with a frozen sweep boundary.
            if (++batchCount == 64 || batchBytes >= 256 * 1024)
            {
                guard.unlock();
                std::this_thread::yield();
                guard.lock();
                batchCount = 0;
                batchBytes = 0;
            }
        }
        state.Completed = state.Epoch;
        ++state.Collections;
        state.Threshold = (std::max)(std::uint64_t{16 * 1024 * 1024}, state.Live + state.Live / 2 + 8 * 1024 * 1024);
        state.Current = Phase::Idle;
        state.Changed.notify_all();
    }
}
}
