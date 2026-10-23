// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include "SpanAllocator.h"
#include <atomic>

namespace OpenDot::Sol::Runtime::Heap
{
#if defined(SOL_RT_TESTS)
using PreparedAllocationHook = void (*)() noexcept;
/// Harness-only pause after small region preparation, outside the heap mutex and before managed reentry.
void SetPreparedAllocationHook(PreparedAllocationHook hook) noexcept;
#endif
/// One thread owns cache access while managed. The collector may flush only after parking all mutators.
class ThreadAllocationCache final
{
    std::array<Block, 8> _blocks{};
    std::atomic<std::uint32_t> _count{0};
    std::uint64_t _bytes = 0, _epoch = 0;
    std::atomic<std::uint64_t> _hits{0}, _refills{0}, _returned{0}, _flushes{0}, _direct{0};

public:
    Block TryTake(std::uint64_t bytes, std::uint64_t epoch, bool hit = true);
    bool Fill(SpanAllocator &allocator, std::uint64_t bytes);
    void Flush(SpanAllocator &allocator);

    void Direct() noexcept
    {
        _direct.fetch_add(1, std::memory_order_relaxed);
    }

    ThreadAllocationStats Stats() const noexcept;
};

void AddThreadAllocationStats(ThreadAllocationStats &total, const ThreadAllocationStats &value) noexcept;
}
