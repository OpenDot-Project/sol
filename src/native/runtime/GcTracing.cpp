// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "GcInternal.h"
#include <algorithm>

namespace OpenDot::Sol::Runtime::Heap
{
namespace
{
bool ScanObject(State &state, Allocation &entry, TraceBudget &budget, TraceResult &result)
{
    auto *object = entry.Object;
    const auto *shape = static_cast<const ScanShape *>(object->Type->ScanShape);
    if (!shape || shape->Count == 0)
    {
        ++result.Objects;
        --budget.Objects;
        result.CompletedBytes = SaturatingAdd(result.CompletedBytes, entry.Bytes);
        return true;
    }
    if (!shape->Offsets || (object->Type->Kind == 2 && entry.Bytes < sizeof(StringArray)))
    {
        sol_rt_fail(7);
    }
    const auto *array = object->Type->Kind == 2 ? reinterpret_cast<const StringArray *>(object) : nullptr;
    if (array && (array->Length < 0 || !array->Stride
                  || static_cast<std::uint64_t>(array->Length) > (entry.Bytes - sizeof(StringArray)) / array->Stride))
    {
        sol_rt_fail(7);
    }
    const auto count = array ? static_cast<std::uint64_t>(array->Length) * shape->Count : shape->Count;
    while (entry.Cursor < count && budget.ReferenceSlots)
    {
        const auto index = entry.Cursor;
        if (array && (shape->Offsets[index % shape->Count] > array->Stride
                      || array->Stride - shape->Offsets[index % shape->Count] < sizeof(Header *)))
        {
            sol_rt_fail(7);
        }
        const auto offset = array
                                ? sizeof(StringArray) + (index / shape->Count) * array->Stride
                                  + shape->Offsets[index % shape->Count]
                                : shape->Offsets[index];
        if (offset < sizeof(Header) || offset > entry.Bytes - sizeof(Header *) || offset % alignof(Header *) != 0)
        {
            sol_rt_fail(7);
        }
        auto **slot = reinterpret_cast<Header **>(reinterpret_cast<char *>(object) + offset);
        Shade(state, std::atomic_ref(*slot).load(std::memory_order_acquire));
        ++entry.Cursor;
        --budget.ReferenceSlots;
        ++result.ReferenceSlots;
    }
    if (entry.Cursor != count)
    {
        return false;
    }
    ++result.Objects;
    --budget.Objects;
    result.CompletedBytes = SaturatingAdd(result.CompletedBytes, entry.Bytes);
    return true;
}
}

TraceResult TraceBatch(State &state, TraceBudget budget, bool assist)
{
    TraceResult result;
    if (!budget.Objects || !budget.ReferenceSlots)
    {
        sol_rt_fail(7);
    }
    auto *region = state.Work.Claim();
    if (!region)
    {
        return result;
    }
    const auto started = MonotonicNanoseconds();
    while (region->HasGray(state.Epoch) && budget.Objects && budget.ReferenceSlots)
    {
        const auto slot = region->FirstGray(state.Epoch);
        const auto found = state.Index.find(region->Address(slot));
        if (found == state.Index.end() || found->second->Tracing != region
            || found->second->Storage.Slot != slot)
        {
            sol_rt_fail(7);
        }
        if (ScanObject(state, *found->second, budget, result))
        {
            region->Complete(slot, state.Epoch);
        }
        else
        {
            break;
        }
    }
    state.Work.Finish(*region, state.Epoch);
    const auto elapsed = ElapsedNanoseconds(started);
    state.Cost.Increment(assist ? GcCounter::AssistBatches : GcCounter::MarkBatches);
    state.Cost.Increment(assist ? GcCounter::AssistObjects : GcCounter::MarkObjects, result.Objects);
    state.Cost.Increment(assist ? GcCounter::AssistReferenceSlots : GcCounter::MarkReferenceSlots,
                         result.ReferenceSlots);
    state.Cost.Maximum(GcCounter::MarkMaxReferenceSlots, result.ReferenceSlots);
    state.Cost.Maximum(GcCounter::MarkMaxObjects, result.Objects);
    state.Cost.Observe(assist ? GcHistogram::AssistBatch : GcHistogram::MarkBatch, elapsed);
    state.Cost.Events.Append(assist ? GcEventKind::AssistBatch : GcEventKind::MarkBatch,
                             state.Epoch, result.ReferenceSlots, elapsed);
    return result;
}

PressureSnapshot Pressure(const State &state)
{
    return {state.Bytes, state.Threshold, state.Allocator.Stats().CommittedBytes,
            state.Allocator.PendingBytes(), state.Limit};
}

void AssistAllocation(State &state)
{
    if (state.Current != Phase::Mark || state.Work.Empty())
    {
        return;
    }
    const bool pressure = PacingPolicy::UnderPressure(Pressure(state));
    auto &debt = CurrentThread->Debt;
    if (!debt.ShouldAssist(state.Epoch, pressure))
    {
        return;
    }
    const auto work = TraceBatch(state, PacingPolicy::AssistBudget(pressure), true);
    state.Cost.Increment(GcCounter::AssistDebtPaid, debt.Pay(work));
}
}
