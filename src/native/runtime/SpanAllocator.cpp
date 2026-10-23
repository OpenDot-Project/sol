// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "SpanAllocator.h"
#include <Windows.h>
#include <algorithm>
#include <bit>
#include <limits>
#include <memory>
#if defined(__SANITIZE_ADDRESS__)
#include <sanitizer/asan_interface.h>
#endif

namespace OpenDot::Sol::Runtime::Heap
{
struct Span final
{
    void *Memory = nullptr;
    Span *Next = nullptr, *Previous = nullptr;
    Span *AvailableNext = nullptr, *AvailablePrevious = nullptr;
    std::array<std::uint64_t, 64> Allocated{}, Touched{};
    std::uint64_t Epoch = 0, AvailableEpoch = 0, SweepEpoch = 0;
    std::uint32_t Class = 0, Count = 0, Used = 0, Remaining = 0;
};

namespace
{
void Poison(void *memory, std::uint64_t bytes, bool inaccessible) noexcept
{
#if defined(__SANITIZE_ADDRESS__)
    if (inaccessible)
    {
        __asan_poison_memory_region(memory, static_cast<std::size_t>(bytes));
    }
    else
    {
        __asan_unpoison_memory_region(memory, static_cast<std::size_t>(bytes));
    }
#else
    (void)memory;
    (void)bytes;
    (void)inaccessible;
#endif
}

std::uint64_t Rounded(std::uint64_t bytes, std::uint64_t alignment) noexcept
{
    if (bytes > (std::numeric_limits<std::uint64_t>::max)() - (alignment - 1))
    {
        return 0;
    }
    return (bytes + alignment - 1) & ~(alignment - 1);
}
}

SpanAllocator::SpanAllocator()
{
    SYSTEM_INFO info{};
    GetSystemInfo(&info);
    _pageBytes = info.dwPageSize;
    _granularity = info.dwAllocationGranularity;
    if (_granularity != SpanBytes || !_pageBytes || (_pageBytes & (_pageBytes - 1)))
    {
        sol_rt_fail(7);
    }
}

SpanAllocator::~SpanAllocator()
{
    Reset();
}

void SpanAllocator::UnlinkAvailable(Span *span) noexcept
{
    if (span->AvailableEpoch != _epoch)
    {
        return;
    }
    if (span->AvailablePrevious)
    {
        span->AvailablePrevious->AvailableNext = span->AvailableNext;
    }
    else
    {
        _available[span->Class] = span->AvailableNext;
    }
    if (span->AvailableNext)
    {
        span->AvailableNext->AvailablePrevious = span->AvailablePrevious;
    }
    span->AvailableEpoch = 0;
}

void SpanAllocator::Publish(Span *span) noexcept
{
    if (span->Used == span->Count || span->AvailableEpoch == _epoch)
    {
        return;
    }
    span->AvailablePrevious = nullptr;
    span->AvailableNext = _available[span->Class];
    if (span->AvailableNext)
    {
        span->AvailableNext->AvailablePrevious = span;
    }
    _available[span->Class] = span;
    span->AvailableEpoch = _epoch;
}

void SpanAllocator::Destroy(Span *span)
{
    if (span->Used)
    {
        sol_rt_fail(7);
    }
    UnlinkAvailable(span);
    if (_empty[span->Class] == span)
    {
        _empty[span->Class] = nullptr;
    }
    if (span->Previous)
    {
        span->Previous->Next = span->Next;
    }
    else
    {
        _all = span->Next;
    }
    if (span->Next)
    {
        span->Next->Previous = span->Previous;
    }
    Poison(span->Memory, SpanBytes, false);
    if (!VirtualFree(span->Memory, 0, MEM_RELEASE))
    {
        sol_rt_fail(7);
    }
    _committed -= SpanBytes;
    _reserved -= SpanBytes;
    --_spans;
    delete span;
}

bool SpanAllocator::Budget(std::uint64_t bytes, std::uint64_t limit)
{
    if (bytes > limit)
    {
        return false;
    }
    if (_committed <= limit - bytes)
    {
        return true;
    }
    // At most one empty region per class; trimming never walks the heap or object graph.
    for (auto *span : _empty)
    {
        if (span)
        {
            Destroy(span);
        }
    }
    return _committed <= limit - bytes;
}

Block SpanAllocator::TryAllocate(std::uint64_t bytes, std::uint64_t limit)
{
    if (bytes < sizeof(Header) || (bytes & 15))
    {
        sol_rt_fail(7);
    }
    if (bytes > SizeClasses.back())
    {
        const auto committed = Rounded(bytes, _pageBytes), reserved = Rounded(bytes, _granularity);
        if (!committed || !reserved || !Budget(committed, limit))
        {
            return {};
        }
        auto *memory = VirtualAlloc(nullptr, static_cast<std::size_t>(reserved), MEM_RESERVE, PAGE_READWRITE);
        if (!memory)
        {
            return {};
        }
        if (!VirtualAlloc(memory, static_cast<std::size_t>(committed), MEM_COMMIT, PAGE_READWRITE))
        {
            if (!VirtualFree(memory, 0, MEM_RELEASE))
            {
                sol_rt_fail(7);
            }
            return {};
        }
        _committed += committed;
        _reserved += reserved;
        _capacity += committed;
        ++_large;
        Poison(memory, committed, true);
        Poison(memory, bytes, false);
        return {static_cast<Header *>(memory), nullptr, committed, reserved, 0};
    }
    const auto index = static_cast<std::size_t>(std::lower_bound(SizeClasses.begin(), SizeClasses.end(), bytes) -
                                                SizeClasses.begin());
    auto *span = _available[index];
    if (!span)
    {
        if (!Budget(SpanBytes, limit))
        {
            return {};
        }
        auto owned = std::make_unique<Span>();
        owned->Memory = VirtualAlloc(nullptr, SpanBytes, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (!owned->Memory)
        {
            return {};
        }
        owned->Class = static_cast<std::uint32_t>(index);
        owned->Count = static_cast<std::uint32_t>(SpanBytes / SizeClasses[index]);
        owned->Epoch = _epoch;
        span = owned.release();
        span->Next = _all;
        if (_all)
        {
            _all->Previous = span;
        }
        _all = span;
        _committed += SpanBytes;
        _reserved += SpanBytes;
        ++_spans;
        Poison(span->Memory, SpanBytes, true);
        Publish(span);
    }
    if (span->Epoch != _epoch)
    {
        sol_rt_fail(7);
    }
    if (_empty[index] == span)
    {
        _empty[index] = nullptr;
    }
    for (std::uint32_t word = 0; word < span->Allocated.size(); ++word)
    {
        auto free = ~span->Allocated[word];
        if (!free)
        {
            continue;
        }
        const auto bit = static_cast<unsigned>(std::countr_zero(free));
        const auto slot = word * 64 + bit;
        if (slot >= span->Count)
        {
            sol_rt_fail(7);
        }
        const auto mask = std::uint64_t{1} << bit;
        span->Allocated[word] |= mask;
        if (span->Touched[word] & mask)
        {
            ++_reused;
        }
        span->Touched[word] |= mask;
        ++span->Used;
        ++_slots;
        _capacity += SizeClasses[index];
        if (span->Used == span->Count)
        {
            UnlinkAvailable(span);
        }
        auto *memory = static_cast<char *>(span->Memory) + slot * SizeClasses[index];
        Poison(memory, bytes, false);
        return {reinterpret_cast<Header *>(memory), span, SizeClasses[index], 0, slot};
    }
    sol_rt_fail(7);
}

void SpanAllocator::BeginCycle() noexcept
{
    // Logical sealing is constant-size: no span/object walk inside the root handshake.
    ++_epoch;
    _available.fill(nullptr);
    for (auto *span : _empty)
    {
        if (span)
        {
            span->Epoch = _epoch;
            Publish(span);
        }
    }
}

void SpanAllocator::FreeSlot(const Block &block)
{
    auto *span = block.Owner;
    const auto word = block.Slot / 64;
    const auto mask = std::uint64_t{1} << (block.Slot % 64);
    if (block.Slot >= span->Count || !(span->Allocated[word] & mask))
    {
        sol_rt_fail(7);
    }
    Poison(block.Object, block.Capacity, true);
    span->Allocated[word] &= ~mask;
    --span->Used;
    --_slots;
    _capacity -= block.Capacity;
}

void SpanAllocator::Recycle(Span *span)
{
    if (!span->Used)
    {
        if (_empty[span->Class] && _empty[span->Class] != span)
        {
            Destroy(span);
            return;
        }
        _empty[span->Class] = span;
    }
    Publish(span);
}

void SpanAllocator::Release(const Block &block)
{
    if (!block.Owner)
    {
        Poison(block.Object, block.Capacity, false);
        if (!VirtualFree(block.Object, 0, MEM_RELEASE))
        {
            sol_rt_fail(7);
        }
        _committed -= block.Capacity;
        _reserved -= block.Reserved;
        _capacity -= block.Capacity;
        --_large;
        return;
    }
    FreeSlot(block);
    if (block.Owner->Epoch == _epoch)
    {
        Recycle(block.Owner);
    }
}

void SpanAllocator::Sweep(const Block &block, bool dead)
{
    auto *span = block.Owner;
    if (!span)
    {
        if (dead)
        {
            Release(block);
        }
        return;
    }
    if (span->Epoch == _epoch)
    {
        if (dead)
        {
            sol_rt_fail(7);
        }
        return;
    }
    if (span->SweepEpoch != _epoch)
    {
        span->SweepEpoch = _epoch;
        span->Remaining = span->Used;
    }
    if (!span->Remaining)
    {
        sol_rt_fail(7);
    }
    --span->Remaining;
    if (dead)
    {
        FreeSlot(block);
    }
    if (!span->Remaining)
    {
        span->Epoch = _epoch;
        Recycle(span);
    }
}

AllocatorStats SpanAllocator::Stats() const noexcept
{
    return {_committed, _reserved, _capacity, _spans, _large, _slots, _reused};
}

void SpanAllocator::Reset()
{
    // Shutdown has already released all registered objects, including dedicated large regions.
    while (_all)
    {
        _all->Used = 0;
        Destroy(_all);
    }
    if (_large)
    {
        sol_rt_fail(7);
    }
    _available.fill(nullptr);
    _empty.fill(nullptr);
    _capacity = 0;
    _slots = 0;
    _reused = 0;
}
}
