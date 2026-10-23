// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "SpanAllocator.h"
#include "Core.h"
#include <Windows.h>
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <unordered_set>
#include <vector>

using namespace OpenDot::Sol::Runtime;
using namespace OpenDot::Sol::Runtime::Heap;

namespace
{
constexpr std::uint64_t Limit = 16ULL * 1024 * 1024;

int Classes()
{
    std::mutex mutex;
    std::unique_lock guard(mutex);
    SpanAllocator allocator;
    for (auto size : SizeClasses)
    {
        std::vector<Block> blocks;
        std::unordered_set<Header *> addresses;
        const auto count = static_cast<unsigned>(SpanBytes / size + 3);
        for (unsigned index = 0; index < count; ++index)
        {
            const auto block = allocator.TryAllocate(size, Limit, guard);
            if (!block.Object || !block.Owner || block.Capacity != size || !addresses.insert(block.Object).second
                || reinterpret_cast<std::uintptr_t>(block.Object) % 16)
            {
                return 101;
            }
            std::memset(block.Object, 0x5A, size);
            blocks.push_back(block);
        }
        for (const auto &block : blocks)
        {
            const auto *bytes = reinterpret_cast<const unsigned char *>(block.Object);
            if (bytes[0] != 0x5A || bytes[size - 1] != 0x5A)
            {
                return 102;
            }
            allocator.Release(block, guard);
        }
    }
    auto stats = allocator.Stats();
    if (stats.SmallObjectCount || stats.CapacityBytes || stats.SpanCount != SizeClasses.size()
        || stats.CommittedBytes != SpanBytes * SizeClasses.size())
    {
        return 103;
    }
    // Empty retained spans must stay usable across an epoch even though they have no sweep records.
    allocator.BeginCycle();
    for (auto size : SizeClasses)
    {
        auto block = allocator.TryAllocate(size, Limit, guard);
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
        const auto bytes = static_cast<std::uint64_t>(size - 16);
        const auto expected = *std::lower_bound(SizeClasses.begin(), SizeClasses.end(), bytes);
        auto block = allocator.TryAllocate(bytes, Limit, guard);
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
        freed.insert(blocks[index].Object);
        allocator.Release(blocks[index], guard);
    }
    for (unsigned index = 0; index < blocks.size(); index += 2)
    {
        blocks[index] = allocator.TryAllocate(64, Limit, guard);
        if (!freed.erase(blocks[index].Object))
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
    if (fresh.Owner == old[0].Owner)
    {
        return 120;
    }
    allocator.Sweep(old[0], true, guard);
    allocator.Sweep(old[1], false, guard);
    const auto racing = allocator.TryAllocate(64, Limit, guard);
    if (racing.Owner == old[0].Owner)
    {
        return 121;
    }
    allocator.Sweep(old[2], true, guard);
    allocator.Sweep(old[3], true, guard);
    const auto reused = allocator.TryAllocate(64, Limit, guard);
    if (reused.Owner != old[0].Owner)
    {
        return 122;
    }
    allocator.BeginCycle();
    for (const auto &block : {old[1], fresh, racing, reused})
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
    auto tiny = allocator.TryAllocate(16, Limit, guard);
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
    const auto expectedCommit = (32784ULL + system.dwPageSize - 1) & ~static_cast<std::uint64_t>(system.dwPageSize - 1);
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

int ZeroedReuse()
{
    sol_gc_attach();
    auto *value = sol_gc_alloc(&sol_rt_object_type, 272);
    std::memset(reinterpret_cast<char *>(value) + sizeof(Header), 0x55, 272 - sizeof(Header));
    sol_gc_collect();
    auto *replacement = sol_gc_alloc(&sol_rt_object_type, 272);
    const auto *bytes = reinterpret_cast<const unsigned char *>(replacement);
    for (unsigned index = sizeof(Header); index < 272; ++index)
    {
        if (bytes[index])
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

int RunSpanTests()
{
    for (auto test : {Classes, BoundariesAndReuse, Epochs, LargeAndBudget, ZeroedReuse})
    {
        const auto code = test();
        if (code)
        {
            return code;
        }
    }
    std::fprintf(
        stderr,
        "Span allocator: 44 classes, reuse, sealed epochs, virtual regions, commit limits and zeroing passed\n");
    return 0;
}

int RunSpanPoisonTest(bool freed)
{
    sol_gc_attach();
    auto *object = sol_gc_alloc(&sol_rt_object_type, 272);
    if (freed)
    {
        sol_gc_collect();
    }
    // An ASan child must diagnose both class slack and a reclaimed slot retained in an empty span.
    auto *byte = reinterpret_cast<volatile unsigned char *>(object) + (freed ? 0 : 272);
    *byte = 1;
    return 99;
}
