// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "SpanAllocator.h"
#include <Windows.h>
#include <algorithm>
#include <atomic>
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
#if defined(SOL_RT_TESTS)
std::atomic<RegionHook> Observer{nullptr};
#endif

bool Allow(RegionOperation operation, void *memory, std::uint64_t bytes) noexcept
{
#if defined(SOL_RT_TESTS)
    const auto hook = Observer.load(std::memory_order_acquire);
    return !hook || hook(operation, memory, bytes);
#else
    (void)operation;
    (void)memory;
    (void)bytes;
    return true;
#endif
}

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

void FreeRegion(void *memory, std::uint64_t committed) noexcept
{
    Poison(memory, committed, false);
    if (!Allow(RegionOperation::Release, memory, committed) || !VirtualFree(memory, 0, MEM_RELEASE))
    {
        sol_rt_fail(7);
    }
}

struct Prepared final
{
    std::unique_ptr<Span> Small;
    void *Memory = nullptr;
};

Prepared Prepare(bool small, std::uint64_t bytes, std::uint64_t committed, std::uint64_t reserved)
{
    auto metadata = small ? std::make_unique<Span>() : nullptr;
    if (!Allow(RegionOperation::Reserve, nullptr, reserved))
    {
        return {};
    }
    auto *memory = VirtualAlloc(nullptr, static_cast<std::size_t>(reserved), MEM_RESERVE, PAGE_READWRITE);
    if (!memory)
    {
        return {};
    }
    if (!Allow(RegionOperation::Commit, memory, committed)
        || !VirtualAlloc(memory, static_cast<std::size_t>(committed), MEM_COMMIT, PAGE_READWRITE))
    {
        FreeRegion(memory, 0);
        return {};
    }
    Poison(memory, committed, true);
    if (!small)
    {
        Poison(memory, bytes, false);
    }
    return {std::move(metadata), memory};
}
}

#if defined(SOL_RT_TESTS)
void SetRegionHook(RegionHook hook) noexcept
{
    Observer.store(hook, std::memory_order_release);
}
#endif

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
    // Destruction is exclusive: the owner has joined its collector and detached other mutators.
    if (_large || _pending)
    {
        sol_rt_fail(7);
    }
    while (_all)
    {
        auto *span = _all;
        _all = span->Next;
        FreeRegion(span->Memory, SpanBytes);
        delete span;
    }
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

void SpanAllocator::Destroy(Span *span, std::unique_lock<std::mutex> &guard)
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
    --_spans;
    // Detach before unlocking. Keep commitment charged until the OS release has completed.
    guard.unlock();
    FreeRegion(span->Memory, SpanBytes);
    delete span;
    guard.lock();
    _committed -= SpanBytes;
    _reserved -= SpanBytes;
}

bool SpanAllocator::Budget(std::uint64_t bytes, std::uint64_t limit, std::unique_lock<std::mutex> &guard)
{
    if (bytes > limit)
    {
        return false;
    }
    const auto fits = [&] {
        return _pending <= limit - bytes && _committed <= limit - bytes - _pending;
    };
    if (fits())
    {
        return true;
    }
    // At most one empty region per class; trimming never walks the heap or object graph.
    for (std::size_t index = 0; index < _empty.size(); ++index)
    {
        if (auto *span = _empty[index])
        {
            Destroy(span, guard);
            if (fits())
            {
                return true;
            }
        }
    }
    return fits();
}

Block SpanAllocator::TryAllocate(std::uint64_t bytes, std::uint64_t limit, std::unique_lock<std::mutex> &guard)
{
    if (auto reused = TryReuse(bytes); reused.Object)
    {
        return reused;
    }
    const auto large = PrepareRegion(bytes, limit, guard);
    return large.Object ? large : TryReuse(bytes);
}

bool SpanAllocator::PrepareSmall(std::uint64_t bytes, std::uint64_t limit, std::unique_lock<std::mutex> &guard)
{
    if (bytes < sizeof(Header) || (bytes & 15) || bytes > SizeClasses.back())
    {
        sol_rt_fail(7);
    }
    // PrepareRegion publishes empty storage only. No unregistered slot crosses a native transition.
    (void)PrepareRegion(bytes, limit, guard);
    const auto index = static_cast<std::size_t>(std::lower_bound(SizeClasses.begin(), SizeClasses.end(), bytes)
                                                - SizeClasses.begin());
    return _available[index] != nullptr;
}

Block SpanAllocator::PrepareRegion(std::uint64_t bytes, std::uint64_t limit, std::unique_lock<std::mutex> &guard)
{
    if (!guard.owns_lock())
    {
        sol_rt_fail(7);
    }
    const bool small = bytes <= SizeClasses.back();
    const auto index = small
                           ? static_cast<std::size_t>(std::lower_bound(SizeClasses.begin(), SizeClasses.end(), bytes)
                                                      - SizeClasses.begin())
                           : 0;
    if (small && _available[index])
    {
        return {};
    }
    const auto committed = small ? SpanBytes : Rounded(bytes, _pageBytes);
    const auto reserved = small ? SpanBytes : Rounded(bytes, _granularity);
    if (!committed || !reserved || !Budget(committed, limit, guard))
    {
        return {};
    }
    // Trimming can unlock and allow a sweep/refill to make an existing slot available.
    if (small && _available[index])
    {
        return {};
    }
    _pending += committed;
    guard.unlock();
    Prepared prepared;
    try
    {
        prepared = Prepare(small, bytes, committed, reserved);
    }
    catch (...)
    {
        guard.lock();
        _pending -= committed;
        throw;
    }
    guard.lock();
    _pending -= committed;
    if (!prepared.Memory)
    {
        return {};
    }
    _committed += committed;
    _reserved += reserved;
    if (!small)
    {
        _capacity += committed;
        ++_large;
        return {static_cast<Header *>(prepared.Memory), nullptr, committed, reserved, 0};
    }
    auto *span = prepared.Small.release();
    span->Memory = prepared.Memory;
    span->Class = static_cast<std::uint32_t>(index);
    span->Count = static_cast<std::uint32_t>(SpanBytes / SizeClasses[index]);
    span->Epoch = _epoch;
    span->Next = _all;
    if (_all)
    {
        _all->Previous = span;
    }
    _all = span;
    ++_spans;
    Recycle(span, guard);
    return {};
}

Block SpanAllocator::TryReuse(std::uint64_t bytes)
{
    if (bytes < sizeof(Header) || (bytes & 15))
    {
        sol_rt_fail(7);
    }
    if (bytes > SizeClasses.back())
    {
        return {};
    }
    const auto index = static_cast<std::size_t>(std::lower_bound(SizeClasses.begin(), SizeClasses.end(), bytes)
                                                - SizeClasses.begin());
    auto *span = _available[index];
    if (!span)
    {
        return {};
    }
    return TakeSlot(span, bytes);
}

Block SpanAllocator::TakeSlot(Span *span, std::uint64_t bytes)
{
    const auto index = span->Class;
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
        auto *memory = static_cast<char *>(span->Memory) + static_cast<std::size_t>(slot) * SizeClasses[index];
        Poison(memory, bytes, false);
        return {reinterpret_cast<Header *>(memory), span, SizeClasses[index], 0, slot};
    }
    sol_rt_fail(7);
}

std::uint32_t SpanAllocator::TakeBatch(std::uint64_t bytes, Block *blocks, std::uint32_t capacity)
{
    if (!blocks || !capacity)
    {
        sol_rt_fail(7);
    }
    const auto first = TryReuse(bytes);
    if (!first.Object)
    {
        return 0;
    }
    blocks[0] = first;
    std::uint32_t count = 1;
    while (count < capacity && first.Owner->Used < first.Owner->Count)
    {
        blocks[count++] = TakeSlot(first.Owner, bytes);
    }
    return count;
}

void SpanAllocator::ReturnUnused(const Block &block)
{
    if (!block.Owner || block.Owner->Epoch != _epoch)
    {
        sol_rt_fail(7);
    }
    FreeSlot(block);
    // A batch is consumed once before leaving managed mode; its registered object still owns a slot.
    if (!block.Owner->Used)
    {
        sol_rt_fail(7);
    }
    Publish(block.Owner);
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

void SpanAllocator::Recycle(Span *span, std::unique_lock<std::mutex> &guard)
{
    if (!span->Used)
    {
        if (_empty[span->Class] && _empty[span->Class] != span)
        {
            Destroy(span, guard);
            return;
        }
        _empty[span->Class] = span;
    }
    Publish(span);
}

void SpanAllocator::Release(const Block &block, std::unique_lock<std::mutex> &guard)
{
    if (!block.Owner)
    {
        _capacity -= block.Capacity;
        --_large;
        guard.unlock();
        FreeRegion(block.Object, block.Capacity);
        guard.lock();
        _committed -= block.Capacity;
        _reserved -= block.Reserved;
        return;
    }
    FreeSlot(block);
    if (block.Owner->Epoch == _epoch)
    {
        Recycle(block.Owner, guard);
    }
}

void SpanAllocator::Sweep(const Block &block, bool dead, std::unique_lock<std::mutex> &guard)
{
    auto *span = block.Owner;
    if (!span)
    {
        if (dead)
        {
            Release(block, guard);
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
        Recycle(span, guard);
    }
}

AllocatorStats SpanAllocator::Stats() const noexcept
{
    return {_committed, _reserved, _capacity, _spans, _large, _slots, _reused};
}

void SpanAllocator::Reset(std::unique_lock<std::mutex> &guard)
{
    // Shutdown has already released all registered objects, including dedicated large regions.
    while (_all)
    {
        _all->Used = 0;
        Destroy(_all, guard);
    }
    if (_large || _pending)
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
