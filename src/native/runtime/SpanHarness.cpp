// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "SpanAllocator.h"
#include <tuple>
#include <bit>
#include "CheckedAccess.h"
#include "Core.h"
#include <Windows.h>
#include <algorithm>
#include <cstdio>
// NOLINTNEXTLINE(CppUnusedIncludeDirective): Direct standard-library contract, including MSVC intrinsics.
#include <cstring>
#include <unordered_set>
#include <vector>

using namespace OpenDot::Sol::Runtime;
using namespace OpenDot::Sol::Runtime::Heap;

namespace
{
constexpr std::uint64_t Limit = 16ULL * 1024ULL * 1024;

int Classes()
{
    std::mutex mutex;
    std::unique_lock guard(mutex);
    SpanAllocator allocator;
    for (auto size : SizeClasses)
    {
        std::vector<Block> blocks;
        std::unordered_set<Header *> addresses;
        const auto count = Detail::Integer<unsigned>(SpanBytes / size + 3);
        for (unsigned index = 0; index < count; ++index)
        {
            const auto block = allocator.TryAllocate(size, Limit, guard);
            if (!block.Object || !block.Owner || block.Capacity != size || !addresses.insert(block.Object).second
                || std::bit_cast<std::uintptr_t>(block.Object) % 16)
            {
                return 101;
            }
            std::memset(block.Object, 0x5A, size);
            blocks.push_back(block);
        }
        for (const auto &block : blocks)
        {
            const std::span bytes(Detail::StorageCast<const unsigned char>(block.Object), size);
            if (bytes.front() != 0x5A || bytes.back() != 0x5A)
            {
                return 102;
            }
            allocator.Release(block, guard);
        }
    }
    const auto stats = allocator.Stats();
    if (stats.SmallObjectCount || stats.CapacityBytes || stats.SpanCount != SizeClasses.size()
        || stats.CommittedBytes != SpanBytes * SizeClasses.size())
    {
        return 103;
    }
    // Empty retained spans must stay usable across an epoch even though they have no sweep records.
    allocator.BeginCycle();
    for (auto size : SizeClasses)
    {
        const auto block = allocator.TryAllocate(size, Limit, guard);
        if (!block.Object || allocator.Stats().SpanCount != SizeClasses.size())
        {
            return 104;
        }
        allocator.Release(block, guard);
    }
    allocator.Reset(guard);
    if (allocator.Stats().CommittedBytes || allocator.Stats().ReservedBytes)
    {
        return 105;
    }
    return 0;
}

int BoundariesAndReuse()
{
    std::mutex mutex;
    std::unique_lock guard(mutex);
    SpanAllocator allocator;
    for (auto size : SizeClasses)
    {
        if (size <= 16)
        {
            continue;
        }
        const auto bytes = Detail::Integer<std::uint64_t>(size - 16);
        const auto expected = *std::lower_bound(SizeClasses.begin(), SizeClasses.end(), bytes);
        const auto block = allocator.TryAllocate(bytes, Limit, guard);
        if (!block.Object || block.Capacity != expected)
        {
            return 110;
        }
        allocator.Release(block, guard);
    }
    allocator.Reset(guard);
    std::vector<Block> blocks;
    for (unsigned index = 0; index < 4096; ++index)
    {
        blocks.push_back(allocator.TryAllocate(64, Limit, guard));
    }
    if (allocator.Stats().SpanCount != 4 || allocator.Stats().SmallObjectCount != 4096)
    {
        return 111;
    }
    std::unordered_set<Header *> freed;
    for (unsigned index = 0; index < blocks.size(); index += 2)
    {
        freed.insert(blocks.at(index).Object);
        allocator.Release(blocks.at(index), guard);
    }
    for (unsigned index = 0; index < blocks.size(); index += 2)
    {
        blocks.at(index) = allocator.TryAllocate(64, Limit, guard);
        if (!freed.erase(blocks.at(index).Object))
        {
            return 112;
        }
    }
    if (!freed.empty() || allocator.Stats().ReusedSlots != 2048 || allocator.Stats().SpanCount != 4)
    {
        return 113;
    }
    for (const auto &block : blocks)
    {
        allocator.Release(block, guard);
    }
    if (allocator.Stats().SpanCount != 1 || allocator.Stats().SmallObjectCount)
    {
        return 114;
    }
    return 0;
}

int Epochs()
{
    std::mutex mutex;
    std::unique_lock guard(mutex);
    SpanAllocator allocator;
    std::vector<Block> old;
    for (unsigned index = 0; index < 4; ++index)
    {
        old.push_back(allocator.TryAllocate(64, Limit, guard));
    }
    allocator.BeginCycle();
    const auto fresh = allocator.TryAllocate(64, Limit, guard);
    if (fresh.Owner == old.at(0).Owner)
    {
        return 120;
    }
    allocator.Sweep(old.at(0), true, guard);
    allocator.Sweep(old.at(1), false, guard);
    const auto racing = allocator.TryAllocate(64, Limit, guard);
    if (racing.Owner == old.at(0).Owner)
    {
        return 121;
    }
    allocator.Sweep(old.at(2), true, guard);
    allocator.Sweep(old.at(3), true, guard);
    const auto reused = allocator.TryAllocate(64, Limit, guard);
    if (reused.Owner != old.at(0).Owner)
    {
        return 122;
    }
    allocator.BeginCycle();
    for (const auto &block : {old.at(1), fresh, racing, reused})
    {
        allocator.Sweep(block, true, guard);
    }
    if (allocator.Stats().SmallObjectCount || allocator.Stats().SpanCount != 1)
    {
        return 123;
    }
    return 0;
}

int LargeAndBudget()
{
    std::mutex mutex;
    std::unique_lock guard(mutex);
    SpanAllocator allocator;
    const auto tiny = allocator.TryAllocate(16, Limit, guard);
    allocator.Release(tiny, guard);
    // A dedicated region can reclaim the bounded empty-span reserve to fit its commit budget.
    auto large = allocator.TryAllocate(65536, 65536, guard);
    if (!large.Object || large.Owner || large.Capacity != 65536 || large.Reserved != 65536
        || allocator.Stats().SpanCount || allocator.Stats().LargeObjectCount != 1)
    {
        return 130;
    }
    if (allocator.TryAllocate(16, 65536, guard).Object)
    {
        return 131;
    }
    allocator.Release(large, guard);
    large = allocator.TryAllocate(32784, Limit, guard);
    SYSTEM_INFO system{};
    GetSystemInfo(&system);
    const auto expectedCommit = (32784ULL + system.dwPageSize - 1) & ~Detail::Integer<std::uint64_t>(
                                    system.dwPageSize - 1);
    MEMORY_BASIC_INFORMATION info{};
    if (!large.Object || large.Owner || large.Capacity != expectedCommit || large.Reserved != 65536
        || !VirtualQuery(large.Object, &info, sizeof(info)) || info.State != MEM_COMMIT
        || info.RegionSize != large.Capacity)
    {
        return 132;
    }
    allocator.Release(large, guard);
    if (allocator.Stats().CommittedBytes || allocator.Stats().ReservedBytes || allocator.Stats().LargeObjectCount)
    {
        return 133;
    }
    if (allocator.TryAllocate(16, SpanBytes - 1, guard).Object || allocator.TryAllocate(32784, 32784, guard).Object)
    {
        return 134;
    }
    return 0;
}

int ZeroedReuse() noexcept(false)
{
    sol_gc_attach();
    auto *value = sol_gc_alloc(&sol_rt_object_type, 272);
    const auto payload = std::span(Detail::StorageCast<char>(value), 272).subspan(sizeof(Header));
    std::memset(payload.data(), 0x55, payload.size());
    sol_gc_collect();
    const auto *replacement = sol_gc_alloc(&sol_rt_object_type, 272);
    const std::span bytes(Detail::StorageCast<const unsigned char>(replacement), 272);
    for (unsigned index = sizeof(Header); index < 272; ++index)
    {
        if (Detail::CheckedAt(bytes, index))
        {
            return 140;
        }
    }
    if (sol_gc_allocator_stats().ReusedSlots == 0 || replacement->Type != &sol_rt_object_type)
    {
        return 141;
    }
    sol_rt_shutdown();
    if (sol_gc_allocator_stats().CommittedBytes || sol_gc_allocator_stats().SmallObjectCount)
    {
        return 142;
    }
    return 0;
}
}

int RunSpanTests() noexcept(false)
{
    for (auto test : {Classes, BoundariesAndReuse, Epochs, LargeAndBudget, ZeroedReuse})
    {
        const auto code = test ? test() : 7;
        if (code)
        {
            return code;
        }
    }
    std::ignore = std::fprintf(
        stderr,
        "Span allocator: 44 classes, reuse, sealed epochs, virtual regions, commit limits and zeroing passed\n");
    return 0;
}

int RunSpanPoisonTest(bool freed) noexcept(false)
{
    sol_gc_attach();
    auto *object = sol_gc_alloc(&sol_rt_object_type, 272);
    if (freed)
    {
        sol_gc_collect();
    }
    // An ASan child must diagnose both class slack and a reclaimed slot retained in an empty span.
#if defined(_MSC_VER) && !defined(__clang__)
    [[gsl::suppress("bounds.1", justification:
            "Intentional ASan poisoned-slot probe; the harness requires this process to fail.")]]
#endif
        auto *byte = Detail::StorageCast<volatile unsigned char>(object) + (freed ? 0 : 272);
    if (!byte)
    {
        return 99;
    }
    *byte = 1;
    return 99;
}
