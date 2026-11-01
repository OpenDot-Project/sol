// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include <concepts>
#include <stdexcept>
#include <utility>

namespace OpenDot::Sol
{
/// Converts integer values only when the destination can represent them exactly.
/// This is an implementation utility, not part of the runtime C ABI.
template <std::integral Destination, std::integral Source>
constexpr bool TryInteger(Source value, Destination &result) noexcept
{
    if (!std::in_range<Destination>(value))
    {
        return false;
    }
#if defined(_MSC_VER) && !defined(__clang__)
    [[gsl::suppress("type.1", justification: "std::in_range proves this conversion is lossless.")]]
#endif
        result = static_cast<Destination>(value);
    return true;
}

template <std::integral Destination, std::integral Source>
constexpr Destination CheckedInteger(Source value)
{
    Destination result{};
    if (!TryInteger(value, result))
    {
        throw std::out_of_range("Native integer conversion is outside the destination range.");
    }
    return result;
}
}
