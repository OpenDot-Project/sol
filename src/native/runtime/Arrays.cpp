// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Core.h"
#include "CheckedAccess.h"
#include <array>

using namespace OpenDot::Sol::Runtime;

namespace
{
// Element kind 11 is reserved for managed-reference arrays; kind 12 stores raw function pointers.
constexpr std::array<std::uint32_t, 13> Widths{1, 1, 1, 2, 4, 4, 8, 8, 4, 8, 8, 8, 8};
constexpr auto Elements = []() noexcept {
    std::array<TypeDescriptor, 13> result{};
    for (std::size_t index = 0; index < result.size(); ++index)
    {
        Detail::CheckedAt(result, index) = {Detail::CheckedAt(Widths, index), 16, 200 + index, 4, 0, nullptr, nullptr,
                                            nullptr, nullptr, nullptr};
    }
    return result;
}();
constexpr auto ArrayTypes = []() noexcept {
    std::array<TypeDescriptor, 13> result{};
    for (std::size_t index = 0; index < result.size(); ++index)
    {
        Detail::CheckedAt(result, index) = {32, 16, 220 + index, 2, 0, &sol_rt_object_type, nullptr, nullptr, nullptr,
                                            nullptr};
    }
    return result;
}();
constexpr std::uint32_t ReferenceOffset = 0;
constexpr ScanShape ReferenceElements{&ReferenceOffset, 1};
}

extern "C"
{
SOL_RT_API const TypeDescriptor sol_rt_reference_array_type{sizeof(StringArray), 16, 230, 2, 0,
                                                            &sol_rt_object_type, &ReferenceElements, nullptr,
                                                            nullptr, nullptr};

StringArray *sol_rt_array_new(int length, int elementKind)
{
    if (elementKind == 11)
    {
        return sol_rt_array_create(length, sizeof(Header *), &sol_rt_object_type,
                                   &sol_rt_reference_array_type);
    }
    if (elementKind < 0 || elementKind >= Detail::Integer<int>(Widths.size()))
    {
        sol_rt_fail(7);
    }
    const auto index = Detail::Integer<std::size_t>(elementKind);
    return sol_rt_array_create(length, Detail::CheckedAt(Widths, index),
                               index == 10 ? &sol_rt_string_type : &Detail::CheckedAt(Elements, index),
                               index == 10 ? &sol_rt_string_array_type : &Detail::CheckedAt(ArrayTypes, index));
}

void *sol_rt_array_address(StringArray *array, int index, int expectedStride)
{
    if (!array || index < 0 || index >= array->Length)
    {
        sol_rt_fail(4);
    }
    if (expectedStride <= 0 || array->Stride != Detail::Integer<std::uint32_t>(expectedStride))
    {
        sol_rt_fail(7);
    }
    const std::span bytes(Detail::StorageCast<std::byte>(array->Data()),
                          Detail::Integer<std::size_t>(array->Length) * array->Stride);
    return bytes.subspan(Detail::Integer<std::size_t>(index) * array->Stride, array->Stride).data();
}
}
