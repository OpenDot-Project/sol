// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "GcInternal.h"
#include "NativeScope.h"
#include <algorithm>
#include <cstdlib>
#include <cstring>
#include <limits>

namespace OpenDot::Sol::Runtime::Heap
{
thread_local ThreadState *CurrentThread = nullptr;

State &Get()
{
    static State state;
    return state;
}

State::~State()
{
    {
        const std::lock_guard guard(Mutex);
        Stop = true;
        Changed.notify_all();
    }
    if (Worker.joinable())
    {
        Worker.join();
    }
    for (auto &entry : Objects)
    {
        Allocator.Release(entry->Storage);
    }
}

bool Handshake(Phase phase)
{
    return phase == Phase::Start || phase == Phase::Terminate;
}

void RequireManaged()
{
    if (!CurrentThread || CurrentThread->Mode != ThreadMode::Running)
    {
        sol_rt_fail(7);
    }
}

void StartWorker(State &state)
{
    if (!state.Worker.joinable())
    {
        state.Worker = std::thread(Run);
    }
}

void Shade(State &state, Header *value)
{
    if (!value)
    {
        return;
    }
    const auto found = state.Index.find(value);
    if (found == state.Index.end())
    {
        // Only registered immutable process-lifetime literals may be outside the managed heap.
        if ((std::atomic_ref(value->Flags).load(std::memory_order_acquire) & 1U) == 0)
        {
            sol_rt_fail(7);
        }
        return;
    }
    auto *entry = found->second;
    if (entry->Mark == state.Epoch)
    {
        return;
    }
    entry->Mark = state.Epoch;
    entry->Cursor = 0;
    entry->NextGray = state.Gray;
    state.Gray = entry;
}

void Roots(State &state)
{
    for (const auto &thread : state.Threads)
    {
        for (auto *frame = thread->Top; frame; frame = frame->Previous)
        {
            for (std::uint32_t index = 0; index < frame->Count; ++index)
            {
                Shade(state, std::atomic_ref(frame->Slots[index]).load(std::memory_order_acquire));
            }
        }
    }
    for (const auto &[identity, handle] : state.Handles)
    {
        (void)identity;
        if (!handle->Closed && !handle->Weak)
        {
            Shade(state, handle->Value);
        }
    }
}
}

using namespace OpenDot::Sol::Runtime;
using namespace OpenDot::Sol::Runtime::Heap;

extern "C"
{
void sol_gc_attach()
{
    if (CurrentThread)
    {
        return;
    }
    auto &state = Get();
    std::unique_lock guard(state.Mutex);
    state.Changed.wait(guard, [&] {
        return !Handshake(state.Current);
    });
    try
    {
        auto thread = std::make_unique<ThreadState>();
        if (!state.Limit)
        {
            state.Limit = ReadHeapLimit();
        }
        CurrentThread = thread.get();
        state.Threads.push_back(std::move(thread));
        StartWorker(state);
    }
    catch (...)
    {
        sol_rt_fail(6);
    }
}

void sol_gc_detach()
{
    RequireManaged();
    sol_gc_poll();
    auto &state = Get();
    const std::lock_guard guard(state.Mutex);
    if (CurrentThread->Top)
    {
        sol_rt_fail(7);
    }
    state.Threads.remove_if([](const auto &thread) {
        return thread.get() == CurrentThread;
    });
    CurrentThread = nullptr;
    state.Changed.notify_all();
}

void sol_gc_poll()
{
    RequireManaged();
    auto &state = Get();
    std::unique_lock guard(state.Mutex);
    if (!Handshake(state.Current))
    {
        return;
    }
    CurrentThread->Mode = ThreadMode::Parked;
    state.Changed.notify_all();
    state.Changed.wait(guard, [&] {
        return !Handshake(state.Current) || state.Stop;
    });
    CurrentThread->Mode = ThreadMode::Running;
}

void sol_gc_push_frame(RootFrame *frame)
{
    RequireManaged();
    if (!frame || (frame->Count && !frame->Slots))
    {
        sol_rt_fail(7);
    }
    auto &state = Get();
    const std::lock_guard guard(state.Mutex);
    for (auto *active = CurrentThread->Top; active; active = active->Previous)
    {
        if (active == frame)
        {
            sol_rt_fail(7);
        }
    }
    frame->Previous = CurrentThread->Top;
    CurrentThread->Top = frame;
    if (state.Current == Phase::Mark)
    {
        for (std::uint32_t index = 0; index < frame->Count; ++index)
        {
            Shade(state, std::atomic_ref(frame->Slots[index]).load(std::memory_order_acquire));
        }
    }
}

void sol_gc_pop_frame(RootFrame *frame)
{
    RequireManaged();
    auto &state = Get();
    const std::lock_guard guard(state.Mutex);
    if (!frame || CurrentThread->Top != frame)
    {
        sol_rt_fail(7);
    }
    CurrentThread->Top = frame->Previous;
    frame->Previous = nullptr;
}

void sol_gc_enter_native()
{
    RequireManaged();
    auto &state = Get();
    const std::lock_guard guard(state.Mutex);
    CurrentThread->Mode = ThreadMode::Native;
    state.Changed.notify_all();
}

void sol_gc_leave_native()
{
    if (!CurrentThread || CurrentThread->Mode != ThreadMode::Native)
    {
        sol_rt_fail(7);
    }
    auto &state = Get();
    std::unique_lock guard(state.Mutex);
    state.Changed.wait(guard, [&] {
        return !Handshake(state.Current);
    });
    CurrentThread->Mode = ThreadMode::Running;
}

int sol_gc_enter_export()
{
    if (!CurrentThread)
    {
        sol_gc_attach();
        return 0;
    }
    if (CurrentThread->Mode == ThreadMode::Native)
    {
        sol_gc_leave_native();
        return 2;
    }
    RequireManaged();
    return 1;
}

void sol_gc_leave_export(int transition)
{
    if (transition == 0)
    {
        sol_gc_detach();
    }
    else if (transition == 2)
    {
        sol_gc_enter_native();
    }
    else if (transition != 1)
    {
        sol_rt_fail(7);
    }
}

Header *sol_gc_alloc(const TypeDescriptor *type, std::uint64_t totalBytes)
{
    RequireManaged();
    sol_gc_poll();
    if (!type || type->Alignment != 16 || totalBytes < type->Size || totalBytes < sizeof(Header)
        || totalBytes > (std::numeric_limits<std::size_t>::max)() - 15)
    {
        sol_rt_fail(7);
    }
    const auto bytes = (totalBytes + 15) & ~std::uint64_t{15};
    auto &state = Get();
    Block storage;
    {
        std::unique_lock guard(state.Mutex);
        if (state.Bytes >= state.Threshold && state.Bytes - state.Threshold >= 8 * 1024 * 1024)
        {
            // Apply allocation backpressure while the concurrent worker catches up. Other mutators
            // can continue; this thread waits native-safe with its already published roots.
            guard.unlock();
            sol_gc_collect();
            guard.lock();
        }
        try
        {
            storage = state.Allocator.TryAllocate(bytes, state.Limit);
        }
        catch (...)
        {
            sol_rt_fail(6);
        }
        if (!storage.Object)
        {
            guard.unlock();
            sol_gc_collect();
            guard.lock();
            try
            {
                storage = state.Allocator.TryAllocate(bytes, state.Limit);
            }
            catch (...)
            {
                sol_rt_fail(6);
            }
            if (!storage.Object)
            {
                sol_rt_fail(6);
            }
        }
        state.Bytes += bytes;
    }
    Header *object = storage.Object;
    {
        // Reserved but unpublished memory is not part of the traced graph yet.
        const NativeScope transition(bytes >= 65536);
        std::memset(object, 0, static_cast<std::size_t>(bytes));
    }
    object->Type = type;
    const std::lock_guard guard(state.Mutex);
    try
    {
        auto entry = std::make_unique<Allocation>();
        entry->Storage = storage;
        entry->Object = object;
        entry->Bytes = bytes;
        entry->Mark = state.Epoch; // Black allocation; every initialized reference uses the store barrier.
        state.Objects.push_back(std::move(entry));
        state.Index.emplace(object, state.Objects.back().get());
    }
    catch (...)
    {
        sol_rt_fail(6);
    }
    state.Allocated += bytes;
    if (state.Bytes >= state.Threshold)
    {
        state.Requested = (std::max)(state.Requested, state.Epoch + 1);
        state.Changed.notify_all();
    }
    return object;
}

void sol_gc_store_ref(Header **destination, Header *value)
{
    RequireManaged();
    if (!destination || reinterpret_cast<std::uintptr_t>(destination) % alignof(Header *) != 0)
    {
        sol_rt_fail(7);
    }
    auto &state = Get();
    const std::lock_guard guard(state.Mutex);
    if (state.Current == Phase::Mark || Handshake(state.Current))
    {
        Shade(state, value);
    }
    std::atomic_ref(*destination).store(value, std::memory_order_release);
}

std::uint64_t sol_gc_request_collection()
{
    RequireManaged();
    auto &state = Get();
    const std::lock_guard guard(state.Mutex);
    state.Requested = (std::max)(state.Requested, state.Current == Phase::Idle ? state.Epoch + 1 : state.Epoch);
    state.Changed.notify_all();
    return state.Requested;
}

void sol_gc_wait(std::uint64_t epoch)
{
    RequireManaged();
    auto &state = Get();
    std::unique_lock guard(state.Mutex);
    if (epoch > state.Requested)
    {
        sol_rt_fail(7);
    }
    CurrentThread->Mode = ThreadMode::Native;
    state.Changed.notify_all();
    state.Changed.wait(guard, [&] {
        return state.Completed >= epoch && !Handshake(state.Current);
    });
    CurrentThread->Mode = ThreadMode::Running;
}

void sol_gc_collect()
{
    sol_gc_wait(sol_gc_request_collection());
}

GcStats sol_gc_stats()
{
    auto &state = Get();
    const std::lock_guard guard(state.Mutex);
    return {state.Allocated, state.Live, state.Bytes, state.Objects.size(), state.Collections, state.Completed};
}

RuntimeHandle *sol_gc_create_handle(Header *value, bool weak)
{
    RequireManaged();
    auto &state = Get();
    const std::lock_guard guard(state.Mutex);
    try
    {
        auto handle = std::make_unique<RuntimeHandle>(RuntimeHandle{value, weak});
        auto *identity = handle.get();
        if (!weak && state.Current == Phase::Mark)
        {
            Shade(state, value);
        }
        state.Handles.emplace(identity, std::move(handle));
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
    if (!root)
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
    auto *value = found->second->Value;
    if (value && found->second->Weak && state.Current == Phase::Sweep)
    {
        const auto entry = state.Index.find(value);
        if (entry != state.Index.end() && entry->second->Mark != state.Epoch)
        {
            value = nullptr;
        }
    }
    if (state.Current == Phase::Mark || Handshake(state.Current))
    {
        Shade(state, value);
    }
    std::atomic_ref(*root).store(value, std::memory_order_release);
    return true;
}

void sol_gc_release_handle(RuntimeHandle *handle)
{
    auto &state = Get();
    const std::lock_guard guard(state.Mutex);
    const auto found = state.Handles.find(handle);
    if (found == state.Handles.end())
    {
        return;
    }
    found->second->Closed = true;
    found->second->Value = nullptr;
}

AllocatorStats sol_gc_allocator_stats()
{
    auto &state = Get();
    const std::lock_guard guard(state.Mutex);
    return state.Allocator.Stats();
}
}

namespace OpenDot::Sol::Runtime
{
void ShutdownHeap()
{
    auto &state = Heap::Get();
    {
        const std::lock_guard guard(state.Mutex);
        if (state.Threads.size() > 1 || (Heap::CurrentThread && Heap::CurrentThread->Top))
        {
            sol_rt_fail(7);
        }
        state.Stop = true;
        state.Changed.notify_all();
    }
    if (state.Worker.joinable())
    {
        state.Worker.join();
    }
    const std::lock_guard guard(state.Mutex);
    for (auto &entry : state.Objects)
    {
        state.Allocator.Release(entry->Storage);
    }
    state.Allocator.Reset();
    state.Objects.clear();
    state.Index.clear();
    state.Handles.clear();
    state.Threads.clear();
    Heap::CurrentThread = nullptr;
    state.Bytes = 0;
    state.Live = 0;
    state.Gray = nullptr;
    state.Requested = state.Epoch;
    state.Completed = state.Epoch;
    state.Stop = false;
    state.Current = Heap::Phase::Idle;
}

bool IsInstance(const Header *object, const TypeDescriptor *type) noexcept
{
    if (!object || !type)
    {
        return false;
    }
    for (auto *current = object->Type; current; current = current->Base)
    {
        if (current == type)
        {
            return true;
        }
    }
    return false;
}
}
