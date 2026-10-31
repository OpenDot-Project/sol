// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include "SolRT.h"
#include "../Checked.h"
#include <array>
#include <concepts>
#include <bit>
#include <span>
#include <type_traits>
#include <utility>

namespace OpenDot::Sol::Runtime::Detail
{
/// Runtime invariants use the fatal path rather than allowing a C++ exception through GC work.
template <std::integral Destination, std::integral Source>
constexpr Destination Integer(Source value) noexcept
{
    Destination result{};
    if (!TryInteger(value, result))
    {
        sol_rt_fail(7);
    }
    return result;
}

/// Views ABI storage whose object lifetime and extent have already been established by the caller.
/// Only standard-layout objects are accepted; const cannot be removed and alignment is checked.
template <class Destination, class Source>
Destination *StorageCast(Source *address) noexcept
{
    static_assert(std::is_standard_layout_v<Destination> && std::is_trivially_copyable_v<Destination>);
    if (!address)
    {
        return nullptr;
    }
    if (std::bit_cast<std::uintptr_t>(address) % alignof(Destination) != 0)
    {
        sol_rt_fail(7);
    }
    using Storage = std::conditional_t<std::is_const_v<Source>, const void, void>;
    Storage *storage = address;
    return static_cast<Destination *>(storage);
}

/// Validates both the byte extent and the alignment before obtaining a typed ABI slot.
template <class Destination, class Byte, std::size_t Extent>
Destination *StorageSlot(std::span<Byte, Extent> bytes, std::size_t offset) noexcept
{
    static_assert(sizeof(Byte) == 1);
    if (offset > bytes.size() || sizeof(Destination) > bytes.size() - offset)
    {
        sol_rt_fail(7);
    }
    return StorageCast<Destination>(bytes.subspan(offset, sizeof(Destination)).data());
}

/// Internal invariant failure follows the runtime fatal-error path, including inside noexcept GC work.
template <class Element, std::size_t Extent, std::integral Index>
constexpr Element &CheckedAt(std::span<Element, Extent> values, Index index) noexcept
{
    if (std::cmp_less(index, 0) || std::cmp_greater_equal(index, values.size()))
    {
        sol_rt_fail(7);
    }
    return values.subspan(index, 1).front();
}

template <class Element, std::size_t Size, std::integral Index>
constexpr Element &CheckedAt(std::array<Element, Size> &values, Index index) noexcept
{
    return CheckedAt(std::span{values}, index);
}

template <class Element, std::size_t Size, std::integral Index>
constexpr const Element &CheckedAt(const std::array<Element, Size> &values, Index index) noexcept
{
    return CheckedAt(std::span{values}, index);
}
}
