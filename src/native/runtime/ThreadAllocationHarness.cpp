// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "GcInternal.h"
#include <chrono>
#include <cstdio>
#include <semaphore>
#include <thread>
#include <unordered_set>
#include <vector>

using namespace OpenDot::Sol::Runtime;
using namespace OpenDot::Sol::Runtime::Heap;

namespace
{
struct PreparedProbe final
{
    std::atomic<bool> Claimed{false};
    std::binary_semaphore Arrived{0}, Continue{0};
};

std::atomic<PreparedProbe *> PreparedPause{nullptr};

void PausePrepared() noexcept
{
    auto *probe = PreparedPause.load(std::memory_order_acquire);
    if (probe && !probe->Claimed.exchange(true))
    {
        probe->Arrived.release();
        probe->Continue.acquire();
    }
}

int PreparedAcrossEpochs()
{
    sol_gc_attach();
    PreparedProbe probe;
    PreparedPause.store(&probe, std::memory_order_release);
    SetPreparedAllocationHook(PausePrepared);
    RuntimeHandle *retained = nullptr;
    sol_gc_enter_native();
    std::thread worker([&] {
        sol_gc_attach();
        retained = sol_gc_create_handle(sol_gc_alloc(&sol_rt_object_type, 64), false);
        sol_gc_detach();
    });
    const bool arrived = probe.Arrived.try_acquire_for(std::chrono::seconds(5));
    sol_gc_leave_native();
    int result = arrived ? 0 : 186;
    if (arrived)
    {
        for (unsigned cycle = 0; cycle < 3; ++cycle)
        {
            sol_gc_collect();
            if (sol_gc_stats().ObjectCount || sol_gc_allocator_stats().SmallObjectCount
                || sol_gc_thread_allocation_stats().CachedSlots)
            {
                result = 187;
            }
        }
    }
    sol_gc_enter_native();
    probe.Continue.release();
    worker.join();
    SetPreparedAllocationHook(nullptr);
    PreparedPause.store(nullptr, std::memory_order_release);
    sol_gc_leave_native();
    if (!retained || sol_gc_stats().ObjectCount != 1 || sol_gc_allocator_stats().SmallObjectCount != 1)
    {
        result = 188;
    }
    sol_gc_collect();
    if (sol_gc_stats().ObjectCount != 1)
    {
        result = 189;
    }
    sol_gc_release_handle(retained);
    sol_gc_collect();
    if (sol_gc_stats().ObjectCount || sol_gc_allocator_stats().SmallObjectCount)
    {
        result = 190;
    }
    sol_rt_shutdown();
    return result;
}

int AllClasses()
{
    std::mutex mutex;
    std::unique_lock guard(mutex);
    SpanAllocator allocator;
    for (auto bytes : SizeClasses)
    {
        ThreadAllocationCache cache;
        if (!allocator.PrepareSmall(bytes, 16ULL * 1024 * 1024, guard)
            || allocator.Stats().SmallObjectCount || !cache.Fill(allocator, bytes))
        {
            return 170;
        }
        const auto first = cache.TryTake(bytes, allocator.Epoch(), false);
        const auto stats = cache.Stats();
        if (!first.Object || stats.CachedSlots > 7 || stats.BatchRefills != 1
            || allocator.Stats().SmallObjectCount != stats.CachedSlots + 1)
        {
            return 171;
        }
        cache.Flush(allocator);
        if (cache.Stats().CachedSlots || cache.Stats().ReturnedSlots != stats.CachedSlots
            || allocator.Stats().SmallObjectCount != 1)
        {
            return 172;
        }
        allocator.Release(first, guard);
        allocator.BeginCycle();
    }
    allocator.Reset(guard);
    return 0;
}

int RuntimeBatchAndSealing()
{
    sol_gc_attach();
    Header *roots[8]{};
    RootFrame frame{nullptr, roots, 8, 0};
    sol_gc_push_frame(&frame);
    for (unsigned index = 0; index < 8; ++index)
    {
        sol_gc_store_ref(&roots[index], sol_gc_alloc(&sol_rt_object_type, sizeof(Header)));
    }
    const auto full = sol_gc_thread_allocation_stats();
    if (full.CacheHits != 7 || full.BatchRefills != 1 || full.CachedSlots
        || sol_gc_stats().ObjectCount != 8 || sol_gc_stats().HeapBytes != 8 * sizeof(Header))
    {
        return 173;
    }
    // A new request size returns the unused slots of the previous batch.
    (void)sol_gc_alloc(&sol_rt_object_type, 32);
    (void)sol_gc_alloc(&sol_rt_object_type, 48);
    const auto changed = sol_gc_thread_allocation_stats();
    if (changed.ReturnedSlots != 7 || changed.CachedSlots != 7 || changed.BatchRefills != 3)
    {
        return 174;
    }
    sol_gc_collect();
    const auto sealed = sol_gc_thread_allocation_stats();
    if (sealed.CachedSlots || sealed.ReturnedSlots != 14 || sol_gc_stats().ObjectCount != 8
        || sol_gc_allocator_stats().SmallObjectCount != 8)
    {
        return 175;
    }
    for (auto *root : roots)
    {
        if (!root || root->Type != &sol_rt_object_type)
        {
            return 176;
        }
    }
    // Fresh cache allocations must remain visible through more than one epoch.
    for (unsigned cycle = 0; cycle < 4; ++cycle)
    {
        sol_gc_store_ref(&roots[0], sol_gc_alloc(&sol_rt_object_type, 80));
        sol_gc_collect();
        if (sol_gc_thread_allocation_stats().CachedSlots || roots[0]->Type != &sol_rt_object_type)
        {
            return 177;
        }
    }
    (void)sol_gc_alloc(&sol_rt_object_type, 65536);
    if (sol_gc_thread_allocation_stats().DirectAllocations != 1)
    {
        return 178;
    }
    sol_gc_pop_frame(&frame);
    sol_rt_shutdown();
    if (sol_gc_thread_allocation_stats().BatchRefills || sol_gc_allocator_stats().CommittedBytes)
    {
        return 179;
    }
    return 0;
}

int DetachedOwnership()
{
    sol_gc_attach();
    RuntimeHandle *retained = nullptr;
    sol_gc_enter_native();
    std::thread worker([&] {
        sol_gc_attach();
        retained = sol_gc_create_handle(sol_gc_alloc(&sol_rt_object_type, sizeof(Header)), false);
        sol_gc_detach();
    });
    worker.join();
    sol_gc_leave_native();
    const auto detached = sol_gc_thread_allocation_stats();
    if (detached.BatchRefills != 1 || detached.ReturnedSlots != 7 || detached.CachedSlots
        || sol_gc_allocator_stats().SmallObjectCount != 1)
    {
        return 180;
    }
    sol_gc_collect();
    if (sol_gc_stats().ObjectCount != 1)
    {
        return 181;
    }
    sol_gc_release_handle(retained);
    sol_gc_collect();
    if (sol_gc_stats().ObjectCount)
    {
        return 182;
    }
    sol_rt_shutdown();
    return 0;
}

int NativeCachesDuringCollection()
{
    sol_gc_attach();
    std::counting_semaphore<4> ready{0}, resume{0};
    std::vector<std::thread> workers;
    std::vector<Header *> addresses[4];
    sol_gc_enter_native();
    for (unsigned worker = 0; worker < 4; ++worker)
    {
        workers.emplace_back([&, worker] {
            sol_gc_attach();
            Header *roots[18]{};
            RootFrame frame{nullptr, roots, 18, 0};
            sol_gc_push_frame(&frame);
            for (unsigned index = 0; index < 17; ++index)
            {
                auto *object = sol_gc_alloc(&sol_rt_object_type, 64);
                sol_gc_store_ref(&roots[index], object);
                addresses[worker].push_back(object);
            }
            sol_gc_enter_native();
            ready.release();
            resume.acquire();
            sol_gc_leave_native();
            sol_gc_store_ref(&roots[17], sol_gc_alloc(&sol_rt_object_type, 64));
            sol_gc_collect();
            for (auto *root : roots)
            {
                if (!root || root->Type != &sol_rt_object_type)
                {
                    std::terminate();
                }
            }
            sol_gc_pop_frame(&frame);
            sol_gc_detach();
        });
    }
    for (unsigned worker = 0; worker < 4; ++worker)
    {
        ready.acquire();
    }
    sol_gc_leave_native();
    int result = 0;
    std::unordered_set<Header *> unique;
    for (const auto &group : addresses)
    {
        unique.insert(group.begin(), group.end());
    }
    if (unique.size() != 68 || sol_gc_thread_allocation_stats().CachedSlots != 28)
    {
        result = 183;
    }
    for (unsigned cycle = 0; cycle < 3; ++cycle)
    {
        sol_gc_collect();
        if (sol_gc_stats().ObjectCount != 68 || sol_gc_allocator_stats().SmallObjectCount != 68
            || sol_gc_thread_allocation_stats().CachedSlots)
        {
            result = 184;
        }
    }
    sol_gc_enter_native();
    resume.release(4);
    for (auto &worker : workers)
    {
        worker.join();
    }
    sol_gc_leave_native();
    sol_gc_collect();
    if (sol_gc_stats().ObjectCount || sol_gc_thread_allocation_stats().CachedSlots)
    {
        result = 185;
    }
    sol_rt_shutdown();
    return result;
}
}

int RunThreadAllocationTests()
{
    for (auto test : {AllClasses, RuntimeBatchAndSealing, DetachedOwnership, NativeCachesDuringCollection,
                      PreparedAcrossEpochs})
    {
        if (const int result = test())
        {
            return result;
        }
    }
    std::fprintf(
        stderr,
        "Thread allocation: 44 classes, bounded batches, epoch sealing, detach and concurrent native caches passed.\n");
    return 0;
}

int RunThreadAllocationPoisonTest()
{
    std::mutex mutex;
    std::unique_lock guard(mutex);
    SpanAllocator allocator;
    ThreadAllocationCache cache;
    if (!allocator.PrepareSmall(16, SpanBytes, guard) || !cache.Fill(allocator, 16))
    {
        return 99;
    }
    const auto first = cache.TryTake(16, allocator.Epoch(), false);
    auto *unused = reinterpret_cast<volatile unsigned char *>(first.Object) - 16;
    cache.Flush(allocator);
    *unused = 1; // A returned, unconsumed cached slot must be poisoned.
    return 99;
}
