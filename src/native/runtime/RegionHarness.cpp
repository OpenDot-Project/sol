// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "SpanAllocator.h"
#include <Windows.h>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <semaphore>
#include <thread>

using namespace OpenDot::Sol::Runtime;
using namespace OpenDot::Sol::Runtime::Heap;
using namespace std::chrono_literals;

namespace
{
struct Probe;
std::atomic<Probe *> Active{nullptr};
bool Observe(RegionOperation operation, void *memory, std::uint64_t bytes) noexcept;

struct Probe final
{
    RegionOperation Pause;
    bool Fail;
    std::atomic<DWORD> Thread{0};
    std::atomic<bool> Claimed{false};
    std::atomic<unsigned> Releases{0};
    std::atomic<bool> ReservedAtRelease{false};
    std::binary_semaphore Arrived{0}, Continue{0};

    explicit Probe(RegionOperation pause, bool fail = false) : Pause(pause), Fail(fail)
    {
        Active.store(this, std::memory_order_release);
        SetRegionHook(Observe);
    }

    ~Probe()
    {
        // All observed threads have joined before the probe leaves scope.
        SetRegionHook(nullptr);
        Active.store(nullptr, std::memory_order_release);
    }
};

bool Observe(RegionOperation operation, void *memory, std::uint64_t bytes) noexcept
{
    auto *probe = Active.load(std::memory_order_acquire);
    if (!probe)
    {
        return true;
    }
    const auto expectedThread = probe->Thread.load(std::memory_order_acquire);
    if (expectedThread && expectedThread != GetCurrentThreadId())
    {
        return true;
    }
    if (operation == RegionOperation::Release)
    {
        ++probe->Releases;
        MEMORY_BASIC_INFORMATION info{};
        if (bytes == 0 && VirtualQuery(memory, &info, sizeof(info)) && info.State == MEM_RESERVE)
        {
            probe->ReservedAtRelease = true;
        }
    }
    if (operation != probe->Pause || probe->Claimed.exchange(true))
    {
        return true;
    }
    probe->Arrived.release();
    probe->Continue.acquire();
    return !probe->Fail;
}

int PendingGrowth(std::uint64_t bytes)
{
    std::mutex mutex;
    SpanAllocator allocator;
    Probe probe(RegionOperation::Reserve);
    Block first;
    std::thread allocating([&] {
        probe.Thread.store(GetCurrentThreadId(), std::memory_order_release);
        std::unique_lock guard(mutex);
        first = allocator.TryAllocate(bytes, SpanBytes, guard);
    });
    int result = 0;
    const bool arrived = probe.Arrived.try_acquire_for(5s);
    if (!arrived || !mutex.try_lock())
    {
        result = 150;
    }
    else
    {
        std::unique_lock guard(mutex, std::adopt_lock);
        if (allocator.PendingBytes() != SpanBytes || allocator.Stats().CommittedBytes
            || allocator.TryAllocate(bytes, SpanBytes, guard).Object)
        {
            result = 151;
        }
        // A prepared region must adopt the publication epoch, rather than its reservation epoch.
        for (unsigned cycle = 0; cycle < 3; ++cycle)
        {
            allocator.BeginCycle();
        }
    }
    probe.Continue.release();
    allocating.join();
    std::unique_lock guard(mutex);
    if (!first.Object || allocator.PendingBytes() || allocator.Stats().CommittedBytes != SpanBytes)
    {
        result = 152;
    }
    if (first.Object)
    {
        if (bytes <= SizeClasses.back())
        {
            const auto next = allocator.TryReuse(bytes);
            if (!next.Object || next.Owner != first.Owner || allocator.Stats().SpanCount != 1)
            {
                result = 153;
            }
            if (next.Object)
            {
                allocator.Release(next, guard);
            }
        }
        allocator.Release(first, guard);
    }
    allocator.Reset(guard);
    return result;
}

int FailedGrowth(RegionOperation operation, std::uint64_t bytes)
{
    std::mutex mutex;
    SpanAllocator allocator;
    Probe probe(operation, true);
    Block failed;
    std::thread allocating([&] {
        probe.Thread.store(GetCurrentThreadId(), std::memory_order_release);
        std::unique_lock guard(mutex);
        failed = allocator.TryAllocate(bytes, SpanBytes, guard);
    });
    const bool arrived = probe.Arrived.try_acquire_for(5s);
    probe.Continue.release();
    allocating.join();
    std::unique_lock guard(mutex);
    int result = 0;
    if (!arrived || failed.Object || allocator.PendingBytes() || allocator.Stats().CommittedBytes
        || allocator.Stats().ReservedBytes || allocator.Stats().SpanCount || allocator.Stats().LargeObjectCount)
    {
        result = 154;
    }
    const bool commitFailure = operation == RegionOperation::Commit;
    if (probe.Releases != (commitFailure ? 1U : 0U) || probe.ReservedAtRelease != commitFailure)
    {
        result = 155;
    }
    // Failure must return its budget permit, including a reservation whose commit failed.
    const auto retry = allocator.TryAllocate(bytes, SpanBytes, guard);
    if (!retry.Object)
    {
        result = 156;
    }
    if (retry.Object)
    {
        allocator.Release(retry, guard);
    }
    allocator.Reset(guard);
    return result;
}

int PendingRelease(bool small)
{
    std::mutex mutex;
    SpanAllocator allocator;
    Block blocks[3];
    {
        std::unique_lock guard(mutex);
        if (small)
        {
            // Two slots fill a 32768-byte class span; the third uses another span.
            for (auto &block : blocks)
            {
                block = allocator.TryAllocate(32768, 2 * SpanBytes, guard);
            }
            allocator.Release(blocks[0], guard);
            allocator.Release(blocks[1], guard); // Occupy the sole empty-span cache.
        }
        else
        {
            blocks[2] = allocator.TryAllocate(SpanBytes, SpanBytes, guard);
        }
    }
    Probe probe(RegionOperation::Release);
    std::thread releasing([&] {
        probe.Thread.store(GetCurrentThreadId(), std::memory_order_release);
        std::unique_lock guard(mutex);
        allocator.Release(blocks[2], guard);
    });
    int result = 0;
    if (!probe.Arrived.try_acquire_for(5s) || !mutex.try_lock())
    {
        result = 157;
    }
    else
    {
        std::unique_lock guard(mutex, std::adopt_lock);
        const auto stats = allocator.Stats();
        const auto expected = small ? 2 * SpanBytes : SpanBytes;
        if (stats.CommittedBytes != expected || stats.LargeObjectCount || stats.SmallObjectCount
            || allocator.TryAllocate(SpanBytes, SpanBytes, guard).Object)
        {
            result = 158;
        }
        allocator.BeginCycle();
    }
    probe.Continue.release();
    releasing.join();
    std::unique_lock guard(mutex);
    allocator.Reset(guard);
    if (allocator.Stats().CommittedBytes || allocator.Stats().ReservedBytes || allocator.PendingBytes())
    {
        result = 159;
    }
    return result;
}

int CollectorDuringGrowth()
{
    sol_gc_attach();
    Header *roots[2]{sol_gc_alloc(&sol_rt_object_type, sizeof(Header)), nullptr};
    RootFrame frame{nullptr, roots, 2, 0};
    sol_gc_push_frame(&frame);
    Probe probe(RegionOperation::Reserve);
    RuntimeHandle *retained = nullptr;
    std::binary_semaphore collected{0};
    sol_gc_enter_native();
    std::thread allocating([&] {
        sol_gc_attach();
        probe.Thread.store(GetCurrentThreadId(), std::memory_order_release);
        auto *value = sol_gc_alloc(&sol_rt_object_type, 4ULL * 1024 * 1024);
        retained = sol_gc_create_handle(value, false);
        sol_gc_detach();
    });
    const bool arrived = probe.Arrived.try_acquire_for(5s);
    std::thread collecting([&] {
        sol_gc_attach();
        for (unsigned cycle = 0; cycle < 3; ++cycle)
        {
            sol_gc_collect();
        }
        sol_gc_detach();
        collected.release();
    });
    const bool progressed = collected.try_acquire_for(5s);
    // Always unblock before joining, including the counterexample where the heap lock was held.
    probe.Continue.release();
    allocating.join();
    collecting.join();
    sol_gc_leave_native();
    int result = 0;
    if (!arrived || !progressed || !retained || roots[0]->Type != &sol_rt_object_type
        || !sol_gc_resolve_handle(retained, &roots[1]) || !roots[1])
    {
        result = 160;
    }
    sol_gc_collect();
    if (roots[1] && roots[1]->Type != &sol_rt_object_type)
    {
        result = 161;
    }
    sol_gc_release_handle(retained);
    sol_gc_pop_frame(&frame);
    sol_gc_detach();
    sol_rt_shutdown();
    return result;
}

int WeakDuringRelease()
{
    sol_gc_attach();
    auto *value = sol_gc_alloc(&sol_rt_object_type, 4ULL * 1024 * 1024);
    auto *weak = sol_gc_create_handle(value, true);
    Probe probe(RegionOperation::Release);
    std::binary_semaphore resolved{0};
    std::atomic<bool> cleared{false};
    sol_gc_enter_native();
    std::thread collecting([] {
        sol_gc_attach();
        sol_gc_collect();
        sol_gc_detach();
    });
    const bool arrived = probe.Arrived.try_acquire_for(5s);
    std::thread resolving([&] {
        sol_gc_attach();
        Header *target = nullptr;
        RootFrame frame{nullptr, &target, 1, 0};
        sol_gc_push_frame(&frame);
        cleared = sol_gc_resolve_handle(weak, &target) && !target;
        sol_gc_pop_frame(&frame);
        sol_gc_detach();
        resolved.release();
    });
    const bool progressed = resolved.try_acquire_for(5s);
    probe.Continue.release();
    resolving.join();
    collecting.join();
    sol_gc_leave_native();
    sol_gc_release_handle(weak);
    sol_gc_detach();
    sol_rt_shutdown();
    return arrived && progressed && cleared ? 0 : 162;
}
}

int RunRegionTests()
{
    for (auto bytes : {std::uint64_t{64}, SpanBytes})
    {
        if (const auto code = PendingGrowth(bytes))
        {
            return code;
        }
        for (auto failure : {RegionOperation::Reserve, RegionOperation::Commit})
        {
            if (const auto code = FailedGrowth(failure, bytes))
            {
                return code;
            }
        }
    }
    for (bool small : {false, true})
    {
        if (const auto code = PendingRelease(small))
        {
            return code;
        }
    }
    if (const auto code = CollectorDuringGrowth())
    {
        return code;
    }
    if (const auto code = WeakDuringRelease())
    {
        return code;
    }
    std::fprintf(
        stderr,
        "Region protocol: pending budgets, epoch publication, failed commits, unlocked release, GC progress and weak clearing passed\n");
    return 0;
}
