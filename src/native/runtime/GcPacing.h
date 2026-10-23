// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include <cstdint>

namespace OpenDot::Sol::Runtime::Heap
{
struct TraceBudget final
{
    std::uint32_t ReferenceSlots = 64;
    std::uint32_t Objects = 32;
};

struct TraceResult final
{
    std::uint64_t ReferenceSlots = 0;
    std::uint64_t Objects = 0;
    std::uint64_t CompletedBytes = 0;
};

struct PressureSnapshot final
{
    std::uint64_t RegisteredBytes = 0;
    std::uint64_t Threshold = 0;
    std::uint64_t CommittedBytes = 0;
    std::uint64_t PendingBytes = 0;
    std::uint64_t Limit = 0;
};

/// Work targets, not wall-clock deadlines. Policy arithmetic is shared by the runtime and fixtures.
class PacingPolicy final
{
public:
    static constexpr std::uint64_t MinimumHeap = 16ULL * 1024 * 1024;
    static constexpr std::uint64_t ReserveBytes = 8ULL * 1024 * 1024;
    static constexpr std::uint64_t MaximumDebt = 1024 * 1024;
    static bool UnderPressure(const PressureSnapshot &snapshot) noexcept;
    static bool ShouldWait(const PressureSnapshot &snapshot) noexcept;
    static std::uint64_t NextThreshold(std::uint64_t live, std::uint64_t limit) noexcept;
    static TraceBudget AssistBudget(bool pressure) noexcept;
};

class AllocationDebt final
{
    std::uint64_t _epoch = 0;
    std::uint64_t _bytes = 0;

public:
    void Charge(std::uint64_t epoch, std::uint64_t bytes) noexcept;
    bool ShouldAssist(std::uint64_t epoch, bool pressure) noexcept;
    std::uint64_t Pay(const TraceResult &result) noexcept;
    std::uint64_t Bytes() const noexcept;
};
}
