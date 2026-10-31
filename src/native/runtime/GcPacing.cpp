// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "GcPacing.h"
#include <algorithm>

namespace OpenDot::Sol::Runtime::Heap
{
bool PacingPolicy::UnderPressure(const PressureSnapshot &snapshot) noexcept
{
    if (snapshot.Threshold && snapshot.RegisteredBytes >= snapshot.Threshold)
    {
        return true;
    }
    if (!snapshot.Limit)
    {
        return false;
    }
    const auto target = snapshot.Limit - snapshot.Limit / 4;
    return snapshot.CommittedBytes >= target
           || snapshot.PendingBytes >= target - (std::min)(snapshot.CommittedBytes, target);
}

bool PacingPolicy::ShouldWait(const PressureSnapshot &snapshot) noexcept
{
    return snapshot.RegisteredBytes >= snapshot.Threshold
           && snapshot.RegisteredBytes - snapshot.Threshold >= ReserveBytes;
}

void AllocationDebt::Charge(std::uint64_t epoch, std::uint64_t bytes) noexcept
{
    if (_epoch != epoch)
    {
        _epoch = epoch;
        _bytes = 0;
    }
    const auto charge = (std::min)(bytes, PacingPolicy::MaximumDebt);
    _bytes = (std::min)(PacingPolicy::MaximumDebt, _bytes + charge);
}

bool AllocationDebt::ShouldAssist(std::uint64_t epoch, bool pressure) noexcept
{
    if (_epoch != epoch)
    {
        _epoch = epoch;
        _bytes = 0;
    }
    return pressure || _bytes >= 4096;
}

std::uint64_t AllocationDebt::Pay(const TraceResult &result) noexcept
{
    // Descriptor work and completed scalar objects both repay debt; unscanned objects do not.
    const auto references = result.ReferenceSlots > UINT64_MAX / sizeof(void *)
                                ? UINT64_MAX
                                : result.ReferenceSlots * sizeof(void *);
    const auto credit = (std::max)(references, result.CompletedBytes);
    const auto paid = (std::min)(_bytes, credit);
    _bytes -= paid;
    return paid;
}

std::uint64_t AllocationDebt::Bytes() const noexcept
{
    return _bytes;
}
}
