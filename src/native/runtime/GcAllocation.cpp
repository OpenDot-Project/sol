// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "GcInternal.h"
#include "NativeScope.h"
#include <algorithm>
#include <cstring>
#include <limits>

using namespace OpenDot::Sol::Runtime;
using namespace OpenDot::Sol::Runtime::Heap;

namespace
{
#if defined(SOL_RT_TESTS)
std::atomic<PreparedAllocationHook> PreparedObserver{nullptr};
#endif
Block AcquireStorage(State &state, std::uint64_t bytes)
{
    auto &cache = CurrentThread->AllocationCache;
    // The allocator epoch cannot change while this thread is ManagedRunning.
    if (auto local = cache.TryTake(bytes, state.Allocator.Epoch()); local.Object)
    {
        return local;
    }
    const bool small = bytes <= SizeClasses.back();
    {
        std::unique_lock guard(state.Mutex);
        cache.Flush(state.Allocator);
        if (state.Bytes >= state.Threshold && state.Bytes - state.Threshold >= 8ULL * 1024 * 1024)
        {
            guard.unlock();
            sol_gc_collect();
            guard.lock();
        }
        if (small && cache.Fill(state.Allocator, bytes))
        {
            return cache.TryTake(bytes, state.Allocator.Epoch(), false);
        }
    }
    unsigned failures = 0;
    for (;;)
    {
        Block storage;
        bool prepared = false;
        {
            // No small slot is reserved while native-safe: preparation may cross entire GC cycles.
            const NativeScope transition(true);
            {
                std::unique_lock guard(state.Mutex);
                try
                {
                    if (small)
                    {
                        prepared = state.Allocator.PrepareSmall(bytes, state.Limit, guard);
                    }
                    else
                    {
                        storage = state.Allocator.TryAllocate(bytes, state.Limit, guard);
                    }
                }
                catch (...)
                {
                    sol_rt_fail(6);
                }
            }
#if defined(SOL_RT_TESTS)
            if (prepared)
            {
                if (const auto observer = PreparedObserver.load(std::memory_order_acquire))
                {
                    observer();
                }
            }
#endif
        }
        if (storage.Object)
        {
            cache.Direct();
            return storage;
        }
        if (small)
        {
            const std::lock_guard guard(state.Mutex);
            if (cache.Fill(state.Allocator, bytes))
            {
                return cache.TryTake(bytes, state.Allocator.Epoch(), false);
            }
        }
        if (prepared)
        {
            // Another mutator consumed the available span while this thread rejoined managed mode.
            sol_gc_poll();
            continue;
        }
        if (++failures == 2)
        {
            sol_rt_fail(6);
        }
        sol_gc_collect();
    }
}
}

#if defined(SOL_RT_TESTS)
void OpenDot::Sol::Runtime::Heap::SetPreparedAllocationHook(PreparedAllocationHook hook) noexcept
{
    PreparedObserver.store(hook, std::memory_order_release);
}
#endif

extern "C"
{
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
    const auto storage = AcquireStorage(state, bytes);
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
        if (!state.ObjectIdentities.TryNext(entry->Identity))
        {
            sol_rt_fail(6); // Exhaustion must not let an old weak identity alias a new allocation.
        }
        entry->Mark = state.Epoch; // Black allocation; every initialized reference uses the store barrier.
        state.Objects.push_back(std::move(entry));
        state.Index.emplace(object, state.Objects.back().get());
    }
    catch (...)
    {
        sol_rt_fail(6);
    }
    state.Allocated += bytes;
    state.Bytes += bytes;
    if (state.Bytes >= state.Threshold)
    {
        state.Requested = (std::max)(state.Requested, state.Epoch + 1);
        state.Changed.notify_all();
    }
    return object;
}

}
