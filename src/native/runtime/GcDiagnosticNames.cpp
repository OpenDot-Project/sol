// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "GcDiagnostics.h"

namespace OpenDot::Sol::Runtime::Heap
{
const char *CounterName(GcCounter value)
{
    switch (value)
    {
    case GcCounter::RootSlots:
        return "root_slots";
    case GcCounter::RootFrames:
        return "root_frames";
    case GcCounter::StrongRoots:
        return "strong_roots";
    case GcCounter::CacheSlotsReturned:
        return "cache_slots_returned";
    case GcCounter::MarkObjects:
        return "mark_objects";
    case GcCounter::MarkReferenceSlots:
        return "mark_reference_slots";
    case GcCounter::MarkBatches:
        return "mark_batches";
    case GcCounter::AssistBatches:
        return "assist_batches";
    case GcCounter::AssistReferenceSlots:
        return "assist_reference_slots";
    case GcCounter::AssistObjects:
        return "assist_objects";
    case GcCounter::ShadeCalls:
        return "shade_calls";
    case GcCounter::NewMarks:
        return "new_marks";
    case GcCounter::AlreadyMarked:
        return "already_marked";
    case GcCounter::ReclaimedObjects:
        return "reclaimed_objects";
    case GcCounter::ReclaimedBytes:
        return "reclaimed_bytes";
    case GcCounter::SweepObjects:
        return "sweep_objects";
    case GcCounter::SweepBatches:
        return "sweep_batches";
    case GcCounter::Collections:
        return "collections";
    case GcCounter::TerminationRetries:
        return "termination_retries";
    case GcCounter::AllocationBytes:
        return "allocation_bytes";
    case GcCounter::AllocationCalls:
        return "allocation_calls";
    case GcCounter::PressureWaits:
        return "pressure_waits";
    case GcCounter::RefillFailures:
        return "refill_failures";
    case GcCounter::PhaseChanges:
        return "phase_changes";
    case GcCounter::MarkMaxReferenceSlots:
        return "mark_max_reference_slots";
    case GcCounter::MarkMaxObjects:
        return "mark_max_objects";
    case GcCounter::AssistDebtPaid:
        return "assist_debt_paid";
    case GcCounter::ActiveBarriers:
        return "active_barriers";
    case GcCounter::IdleBarriers:
        return "idle_barriers";
    default:
        sol_rt_fail(7);
    }
}

const char *HistogramName(GcHistogram value)
{
    switch (value)
    {
    case GcHistogram::SafepointWait:
        return "safepoint_wait";
    case GcHistogram::RootSnapshot:
        return "root_snapshot";
    case GcHistogram::MarkBatch:
        return "mark_batch";
    case GcHistogram::AssistBatch:
        return "assist_batch";
    case GcHistogram::SweepBatch:
        return "sweep_batch";
    default:
        sol_rt_fail(7);
    }
}

const char *EventName(GcEventKind value)
{
    switch (value)
    {
    case GcEventKind::StartRequested:
        return "start_requested";
    case GcEventKind::RootsScanned:
        return "roots_scanned";
    case GcEventKind::MarkStarted:
        return "mark_started";
    case GcEventKind::MarkBatch:
        return "mark_batch";
    case GcEventKind::AssistBatch:
        return "assist_batch";
    case GcEventKind::TerminationRequested:
        return "termination_requested";
    case GcEventKind::SweepStarted:
        return "sweep_started";
    case GcEventKind::SweepBatch:
        return "sweep_batch";
    case GcEventKind::CycleCompleted:
        return "cycle_completed";
    case GcEventKind::PressureWait:
        return "pressure_wait";
    case GcEventKind::RefillFailure:
        return "refill_failure";
    default:
        sol_rt_fail(7);
    }
}
}
