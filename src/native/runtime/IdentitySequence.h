// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include <cstdint>
#include <limits>

namespace OpenDot::Sol::Runtime::Heap
{
// Called only with the heap mutex held. Zero is never an allocation identity.
class IdentitySequence final
{
    std::uint64_t _last;

public:
    explicit IdentitySequence(std::uint64_t last = 0) noexcept : _last(last)
    {
    }

    bool TryNext(std::uint64_t &identity) noexcept
    {
        if (_last == (std::numeric_limits<std::uint64_t>::max)())
        {
            identity = 0;
            return false;
        }
        identity = ++_last;
        return true;
    }
};
}
