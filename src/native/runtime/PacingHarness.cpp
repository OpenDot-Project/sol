// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "GcPacing.h"
#include <tuple>
#include <cstdio>

using namespace OpenDot::Sol::Runtime::Heap;

namespace
{
constexpr std::uint64_t MiB = 1024ULL * 1024;

bool PhysicalPressure() noexcept
{
    for (const auto limit : {16ULL * MiB, 64ULL * MiB, 128ULL * MiB, UINT64_MAX})
    {
        const auto target = limit - limit / 4;
        PressureSnapshot snapshot{0, 16 * MiB, target - 1, 0, limit};
        if (PacingPolicy::UnderPressure(snapshot))
        {
            return false;
        }
        snapshot.PendingBytes = 1;
        if (!PacingPolicy::UnderPressure(snapshot))
        {
            return false;
        }
        snapshot.PendingBytes = 0;
        snapshot.CommittedBytes = target;
        if (!PacingPolicy::UnderPressure(snapshot))
        {
            return false;
        }
        snapshot.CommittedBytes = UINT64_MAX;
        snapshot.PendingBytes = UINT64_MAX;
        if (!PacingPolicy::UnderPressure(snapshot))
        {
            return false;
        }
    }
    return !PacingPolicy::UnderPressure({0, 0, UINT64_MAX, UINT64_MAX, 0});
}

bool RegisteredPressure() noexcept
{
    PressureSnapshot snapshot{16 * MiB - 1, 16 * MiB, 0, 0, 64 * MiB};
    if (PacingPolicy::UnderPressure(snapshot) || PacingPolicy::ShouldWait(snapshot))
    {
        return false;
    }
    ++snapshot.RegisteredBytes;
    if (!PacingPolicy::UnderPressure(snapshot) || PacingPolicy::ShouldWait(snapshot))
    {
        return false;
    }
    snapshot.RegisteredBytes += PacingPolicy::ReserveBytes - 1;
    if (PacingPolicy::ShouldWait(snapshot))
    {
        return false;
    }
    ++snapshot.RegisteredBytes;
    if (!PacingPolicy::ShouldWait(snapshot))
    {
        return false;
    }
    snapshot = {UINT64_MAX, UINT64_MAX - PacingPolicy::ReserveBytes, 0, 0, 0};
    if (!PacingPolicy::ShouldWait(snapshot))
    {
        return false;
    }
    ++snapshot.Threshold;
    return !PacingPolicy::ShouldWait(snapshot);
}

bool ThresholdGrowth() noexcept
{
    static_assert(PacingPolicy::NextThreshold(0, 0) == 16 * MiB);
    static_assert(PacingPolicy::NextThreshold(16 * MiB, 0) == 32 * MiB);
    static_assert(PacingPolicy::NextThreshold(64 * MiB, 64 * MiB) == 56 * MiB);
    static_assert(PacingPolicy::NextThreshold(UINT64_MAX, 0) == UINT64_MAX);
    static_assert(PacingPolicy::NextThreshold(UINT64_MAX - 1, UINT64_MAX) == UINT64_MAX - UINT64_MAX / 8);
    for (const auto limit : {0ULL, 16ULL * MiB, 64ULL * MiB, 1024ULL * MiB})
    {
        std::uint64_t previous = 0;
        for (std::uint64_t live = 0; live <= 512 * MiB; live += 65537)
        {
            const auto threshold = PacingPolicy::NextThreshold(live, limit);
            if (threshold < 16 * MiB || threshold < previous
                || (limit && threshold > limit - limit / 8 && threshold > 16 * MiB))
            {
                return false;
            }
            previous = threshold;
        }
    }
    return true;
}

bool DebtEpochs() noexcept
{
    AllocationDebt debt;
    debt.Charge(1, 4095);
    if (debt.ShouldAssist(1, false) || debt.Bytes() != 4095)
    {
        return false;
    }
    debt.Charge(1, 1);
    if (!debt.ShouldAssist(1, false))
    {
        return false;
    }
    if (debt.ShouldAssist(2, false) || debt.Bytes() != 0)
    {
        return false;
    }
    if (!debt.ShouldAssist(2, true))
    {
        return false;
    }
    debt.Charge(2, UINT64_MAX);
    if (debt.Bytes() != PacingPolicy::MaximumDebt)
    {
        return false;
    }
    debt.Charge(2, UINT64_MAX);
    if (debt.Bytes() != PacingPolicy::MaximumDebt)
    {
        return false;
    }
    debt.Charge(3, 25);
    return debt.Bytes() == 25 && !debt.ShouldAssist(3, false);
}

bool DebtRepayment() noexcept
{
    AllocationDebt debt;
    debt.Charge(1, 8192);
    if (debt.Pay({16, 0, 0}) != 128 || debt.Bytes() != 8064)
    {
        return false;
    }
    if (debt.Pay({0, 8, 512}) != 512 || debt.Bytes() != 7552)
    {
        return false;
    }
    if (debt.Pay({1, 1, 64}) != 64 || debt.Bytes() != 7488)
    {
        return false;
    }
    if (debt.Pay({}) != 0 || debt.Bytes() != 7488)
    {
        return false;
    }
    if (debt.Pay({UINT64_MAX, 0, 0}) != 7488 || debt.Bytes())
    {
        return false;
    }
    debt.Charge(1, 8192);
    return debt.Pay({0, 0, UINT64_MAX}) == 8192 && debt.Bytes() == 0;
}

constexpr auto NormalBudget = PacingPolicy::AssistBudget(false);
constexpr auto PressureBudget = PacingPolicy::AssistBudget(true);
static_assert(NormalBudget.ReferenceSlots == 16 && NormalBudget.Objects == 8);
static_assert(PressureBudget.ReferenceSlots == 64 && PressureBudget.Objects == 32);
static_assert(
    NormalBudget.ReferenceSlots < PressureBudget.ReferenceSlots && NormalBudget.Objects < PressureBudget.Objects);
}

int RunPacingTests() noexcept
{
    const auto tests = {PhysicalPressure, RegisteredPressure, ThresholdGrowth, DebtEpochs, DebtRepayment};
    unsigned index = 0;
    for (auto test : tests)
    {
        if (!test || !test())
        {
            std::ignore = std::fprintf(stderr, "Pacing fixture failed: %u\n", index);
            return 231;
        }
        ++index;
    }
    std::ignore = std::fprintf(
        stderr, "Pacing: physical headroom, pending commitments, saturation, epoch debt, bounded work passed.\n");
    return 0;
}
