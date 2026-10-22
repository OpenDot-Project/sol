// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Core.h"
#include <array>

using namespace OpenDot::Sol::Runtime;

namespace
{
constexpr std::array<std::uint32_t, 11> Widths{1, 1, 1, 2, 4, 4, 8, 8, 4, 8, 8};
const auto Elements = [] {
    std::array<TypeDescriptor, 10> result{};
    for (std::size_t index = 0; index < result.size(); ++index)
    {
        result[index] = {Widths[index], 16, 200 + index, 4, 0, nullptr, nullptr, nullptr, nullptr, nullptr};
    }
    return result;
}();
const auto ArrayTypes = [] {
    std::array<TypeDescriptor, 10> result{};
    for (std::size_t index = 0; index < result.size(); ++index)
    {
        result[index] = {32, 16, 220 + index, 2, 0, &sol_rt_object_type, nullptr, nullptr, nullptr, nullptr};
    }
    return result;
}();
}

extern "C"
{
StringArray *sol_rt_array_new(int length, int elementKind)
{
    if (elementKind < 0 || elementKind >= static_cast<int>(Widths.size()))
    {
        sol_rt_fail(7);
    }
    const auto index = static_cast<std::size_t>(elementKind);
    return sol_rt_array_create(length, Widths[index], index == 10 ? &sol_rt_string_type : &Elements[index],
                               index == 10 ? &sol_rt_string_array_type : &ArrayTypes[index]);
}

void *sol_rt_array_address(StringArray *array, int index, int expectedStride)
{
    if (!array || index < 0 || index >= array->Length)
    {
        sol_rt_fail(4);
    }
    if (expectedStride <= 0 || array->Stride != static_cast<std::uint32_t>(expectedStride))
    {
        sol_rt_fail(7);
    }
    return reinterpret_cast<char *>(array) + sizeof(StringArray) + static_cast<std::size_t>(index) * array->Stride;
}
}
