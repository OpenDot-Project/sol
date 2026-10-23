// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "GcInternal.h"

using namespace OpenDot::Sol::Runtime;
using namespace OpenDot::Sol::Runtime::Heap;

extern "C"
{
RuntimeHandle *sol_gc_create_handle(Header *value, bool weak)
{
    RequireManaged();
    auto &state = Get();
    const std::lock_guard guard(state.Mutex);
    try
    {
        auto handle = std::make_unique<RuntimeHandle>();
        handle->Value = value;
        handle->Weak = weak;
        if (value)
        {
            const auto entry = state.Index.find(value);
            if (entry != state.Index.end())
            {
                handle->ObjectIdentity = entry->second->Identity;
                // Creating ownership cannot resurrect an already sealed white object.
                if (state.Current == Phase::Sweep
                    && !entry->second->Tracing->Marked(entry->second->Storage.Slot, state.Epoch))
                {
                    sol_rt_fail(7);
                }
            }
            else
            {
                // The caller may supply an immutable process-lifetime literal, never a stale pointer.
                if ((std::atomic_ref(value->Flags).load(std::memory_order_acquire) & 1U) == 0)
                {
                    sol_rt_fail(7);
                }
                handle->Immortal = true;
            }
        }
        auto *identity = handle.get();
        state.Handles.emplace(identity, std::move(handle));
        if (weak)
        {
            ++state.HandleInventory.WeakCount;
        }
        else
        {
            identity->NextStrong = state.StrongHandles;
            if (state.StrongHandles)
            {
                state.StrongHandles->PreviousStrong = identity;
            }
            state.StrongHandles = identity;
            ++state.HandleInventory.StrongCount;
            if (state.Current == Phase::Mark || Handshake(state.Current))
            {
                Shade(state, value);
            }
        }
        return identity;
    }
    catch (...)
    {
        sol_rt_fail(6);
    }
}

bool sol_gc_resolve_handle(RuntimeHandle *handle, Header **root)
{
    RequireManaged();
    if (!root || reinterpret_cast<std::uintptr_t>(root) % alignof(Header *) != 0)
    {
        sol_rt_fail(7);
    }
    auto &state = Get();
    const std::lock_guard guard(state.Mutex);
    const auto found = state.Handles.find(handle);
    if (found == state.Handles.end() || found->second->Closed)
    {
        return false;
    }
    auto &record = *found->second;
    if (record.Weak && record.Value && !record.Immortal)
    {
        ++state.HandleInventory.WeakLookups;
        // Never dereference the stored address until both registry membership and identity match.
        const auto entry = state.Index.find(record.Value);
        if (entry == state.Index.end() || entry->second->Identity != record.ObjectIdentity
            || (state.Current == Phase::Sweep
                && !entry->second->Tracing->Marked(entry->second->Storage.Slot, state.Epoch)))
        {
            record.Value = nullptr;
            record.ObjectIdentity = 0;
            ++state.HandleInventory.RejectedWeakTargets;
        }
    }
    if (state.Current == Phase::Mark || Handshake(state.Current))
    {
        Shade(state, record.Value);
    }
    // Validation, promotion and root publication share the collector's mutex with retirement.
    std::atomic_ref(*root).store(record.Value, std::memory_order_release);
    return true;
}

void sol_gc_release_handle(RuntimeHandle *handle)
{
    auto &state = Get();
    const std::lock_guard guard(state.Mutex);
    const auto found = state.Handles.find(handle);
    if (found == state.Handles.end() || found->second->Closed)
    {
        return;
    }
    auto &record = *found->second;
    if (record.Weak)
    {
        --state.HandleInventory.WeakCount;
    }
    else
    {
        if (record.PreviousStrong)
        {
            record.PreviousStrong->NextStrong = record.NextStrong;
        }
        else
        {
            state.StrongHandles = record.NextStrong;
        }
        if (record.NextStrong)
        {
            record.NextStrong->PreviousStrong = record.PreviousStrong;
        }
        record.PreviousStrong = nullptr;
        record.NextStrong = nullptr;
        --state.HandleInventory.StrongCount;
    }
    record.Closed = true;
    record.Value = nullptr;
    record.ObjectIdentity = 0;
    ++state.HandleInventory.ClosedCount;
}

HandleStats sol_gc_handle_stats()
{
    auto &state = Get();
    const std::lock_guard guard(state.Mutex);
    return state.HandleInventory;
}
}
