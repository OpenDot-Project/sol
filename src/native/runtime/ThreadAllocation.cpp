// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "GcInternal.h"

namespace OpenDot::Sol::Runtime::Heap
{
Block ThreadAllocationCache::TryTake(std::uint64_t bytes, std::uint64_t epoch, bool hit)
{
    const auto count = _count.load(std::memory_order_relaxed);
    if (!count || _bytes != bytes)
    {
        return {};
    }
    if (_epoch != epoch)
    {
        sol_rt_fail(7);
    }
    const auto result = _blocks[count - 1];
    _count.store(count - 1, std::memory_order_relaxed);
    if (hit)
    {
        _hits.fetch_add(1, std::memory_order_relaxed);
    }
    return result;
}

bool ThreadAllocationCache::Fill(SpanAllocator &allocator, std::uint64_t bytes)
{
    if (_count.load(std::memory_order_relaxed))
    {
        sol_rt_fail(7);
    }
    const auto count = allocator.TakeBatch(bytes, _blocks.data(), static_cast<std::uint32_t>(_blocks.size()));
    if (!count)
    {
        return false;
    }
    _bytes = bytes;
    _epoch = allocator.Epoch();
    _count.store(count, std::memory_order_relaxed);
    _refills.fetch_add(1, std::memory_order_relaxed);
    return true;
}

void ThreadAllocationCache::Flush(SpanAllocator &allocator)
{
    const auto count = _count.load(std::memory_order_relaxed);
    for (std::uint32_t index = 0; index < count; ++index)
    {
        allocator.ReturnUnused(_blocks[index]);
    }
    _count.store(0, std::memory_order_relaxed);
    _bytes = 0;
    _epoch = 0;
    if (count)
    {
        _returned.fetch_add(count, std::memory_order_relaxed);
        _flushes.fetch_add(1, std::memory_order_relaxed);
    }
}

ThreadAllocationStats ThreadAllocationCache::Stats() const noexcept
{
    return {_hits.load(std::memory_order_relaxed), _refills.load(std::memory_order_relaxed),
            _count.load(std::memory_order_relaxed), _returned.load(std::memory_order_relaxed),
            _flushes.load(std::memory_order_relaxed), _direct.load(std::memory_order_relaxed)};
}

void AddThreadAllocationStats(ThreadAllocationStats &total, const ThreadAllocationStats &value) noexcept
{
    total.CacheHits += value.CacheHits;
    total.BatchRefills += value.BatchRefills;
    total.CachedSlots += value.CachedSlots;
    total.ReturnedSlots += value.ReturnedSlots;
    total.CacheFlushes += value.CacheFlushes;
    total.DirectAllocations += value.DirectAllocations;
}
}

extern "C" OpenDot::Sol::Runtime::ThreadAllocationStats sol_gc_thread_allocation_stats()
{
    auto &state = OpenDot::Sol::Runtime::Heap::Get();
    const std::lock_guard guard(state.Mutex);
    auto total = state.RetiredThreadAllocation;
    for (const auto &thread : state.Threads)
    {
        OpenDot::Sol::Runtime::Heap::AddThreadAllocationStats(total, thread->AllocationCache.Stats());
    }
    return total;
}
