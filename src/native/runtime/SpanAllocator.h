// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include "Gc.h"
#include <array>

namespace OpenDot::Sol::Runtime::Heap
{
inline constexpr std::uint64_t SpanBytes = 65536;
inline constexpr std::array<std::uint32_t, 44> SizeClasses{
    16, 32, 48, 64, 80, 96, 112, 128, 144, 160, 176, 192, 208, 224, 240, 256,
    320, 384, 448, 512, 640, 768, 896, 1024, 1280, 1536, 1792, 2048,
    2560, 3072, 3584, 4096, 5120, 6144, 7168, 8192, 10240, 12288, 14336,
    16384, 20480, 24576, 28672, 32768};

struct Span;

struct Block final
{
    Header *Object = nullptr;
    Span *Owner = nullptr;
    std::uint64_t Capacity = 0;
    std::uint64_t Reserved = 0;
    std::uint32_t Slot = 0;
};

/// All operations require the owning heap mutex. No managed pointers live in free-list payloads.
class SpanAllocator final
{
public:
    SpanAllocator();
    ~SpanAllocator();
    SpanAllocator(const SpanAllocator &) = delete;
    SpanAllocator &operator=(const SpanAllocator &) = delete;
    Block TryAllocate(std::uint64_t bytes, std::uint64_t limit);
    void BeginCycle() noexcept;
    void Sweep(const Block &block, bool dead);
    void Release(const Block &block);
    void Reset();
    AllocatorStats Stats() const noexcept;

private:
    Span *_all = nullptr;
    std::array<Span *, SizeClasses.size()> _available{};
    std::array<Span *, SizeClasses.size()> _empty{};
    std::uint64_t _epoch = 1;
    std::uint64_t _committed = 0, _reserved = 0, _capacity = 0;
    std::uint64_t _spans = 0, _large = 0, _slots = 0, _reused = 0;
    std::uint64_t _pageBytes = 0, _granularity = 0;
    bool Budget(std::uint64_t bytes, std::uint64_t limit);
    void Publish(Span *span) noexcept;
    void UnlinkAvailable(Span *span) noexcept;
    void Destroy(Span *span);
    void Recycle(Span *span);
    void FreeSlot(const Block &block);
};
}
