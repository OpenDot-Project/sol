// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Core.h"
#include "CheckedAccess.h"
#include "NativeScope.h"
#include <atomic>
#include <array>
#include <algorithm>
#include <bit>
#include <cstring>
#include <limits>
#include <mutex>
#include <thread>
#include <vector>

using namespace OpenDot::Sol::Runtime;

namespace
{
const std::uint32_t ThrowableOffsets[]{offsetof(Throwable, Message), offsetof(Throwable, Cause),
                                       offsetof(Throwable, Suppressed)};
const ScanShape ThrowableShape{ThrowableOffsets, 3};
constexpr std::uint32_t ReferenceOffset = 0;
const ScanShape ThrowableElements{&ReferenceOffset, 1};
const TypeDescriptor ThrowableArrayType{32, 16, 6, 2, 0, &sol_rt_object_type, &ThrowableElements, nullptr, nullptr,
                                        nullptr};
std::mutex SuppressedLock;

// Returns the next scalar and advances a byte cursor; rejects overlong, surrogate and out-of-range encodings.
std::uint32_t Decode(const char *bytes, int length, int &cursor) noexcept(false)
{
    // A valid scalar advances at most four bytes, so this window cannot be skipped by UTF-8 steps.
    if (cursor >= 4096 && (cursor & 4095) < 4)
    {
        sol_gc_poll();
    }
    if (!bytes || cursor < 0 || cursor >= length)
    {
        sol_rt_fail(7);
    }
    const std::span input(bytes, length);
    const auto first = std::bit_cast<unsigned char>(Detail::CheckedAt(input, cursor++));
    if (first < 0x80)
    {
        return first;
    }
    const int trailing = first >= 0xF0 && first <= 0xF4
                             ? 3
                             : first >= 0xE0 && first <= 0xEF
                             ? 2
                             : first >= 0xC2 && first <= 0xDF
                             ? 1
                             : 0;
    if (trailing == 0 || length - cursor < trailing)
    {
        sol_rt_fail(7);
    }
    std::uint32_t value = first & (0x7FU >> (trailing + 1));
    for (int index = 0; index < trailing; ++index)
    {
        const auto part = std::bit_cast<unsigned char>(Detail::CheckedAt(input, cursor++));
        if ((part & 0xC0) != 0x80)
        {
            sol_rt_fail(7);
        }
        value = (value << 6) | (part & 0x3F);
    }
    constexpr std::array<std::uint32_t, 4> minimum{0, 0x80, 0x800, 0x10000};
    if (value < minimum.at(trailing) || value > 0x10FFFF || (value >= 0xD800 && value <= 0xDFFF))
    {
        sol_rt_fail(7);
    }
    return value;
}

int Offset(String *value, int scalar)
{
    if (!value || scalar < 0)
    {
        sol_rt_fail(4);
    }
    int cursor = 0;
    for (int index = 0; index < scalar; ++index)
    {
        if (cursor == value->ByteLength)
        {
            sol_rt_fail(4);
        }
        Decode(value->Data(), value->ByteLength, cursor);
    }
    return cursor;
}

void RequireString(const String *value) noexcept
{
    if (!value || value->Object.Type != &sol_rt_string_type || value->ByteLength < 0)
    {
        sol_rt_fail(7);
    }
}
}

extern "C"
{
const TypeDescriptor sol_rt_throwable_type{40, 16, 3, 3, 0, &sol_rt_object_type, &ThrowableShape, nullptr, nullptr,
                                           nullptr};
const TypeDescriptor sol_rt_exception_type{40, 16, 4, 3, 0, &sol_rt_throwable_type, &ThrowableShape, nullptr, nullptr,
                                           nullptr};
const TypeDescriptor sol_rt_runtime_exception_type{40, 16, 5, 3, 0, &sol_rt_exception_type, &ThrowableShape, nullptr,
                                                   nullptr, nullptr};

String *sol_rt_string_create(const char *bytes, int length)
{
    if (length < 0 || (length && !bytes))
    {
        sol_rt_fail(7);
    }
    int cursor = 0, runes = 0;
    while (cursor < length)
    {
        Decode(bytes, length, cursor);
        ++runes;
    }
    auto *result = Detail::StorageCast<String>(sol_gc_alloc(&sol_rt_string_type,
                                                            sizeof(String) + Detail::Integer<std::uint64_t>(length)));
    result->ByteLength = length;
    result->RuneCount = runes;
    const TemporaryRoot root(&result->Object);
    const NativeScope transition(length >= 65536);
    if (length)
    {
        std::memcpy(result->Data(), bytes, Detail::Integer<std::size_t>(length));
    }
    return result;
}

int sol_rt_string_length(String *value)
{
    RequireString(value);
    const auto cache = std::atomic_ref(value->RuneCount);
    const auto cached = cache.load(std::memory_order_acquire);
    if (cached >= 0)
    {
        return cached;
    }
    int cursor = 0, count = 0;
    while (cursor < value->ByteLength)
    {
        Decode(value->Data(), value->ByteLength, cursor);
        ++count;
    }
    cache.store(count, std::memory_order_release);
    return count;
}

int sol_rt_string_compare(const String *left, const String *right)
{
    RequireString(left);
    RequireString(right);
    const auto common = (left->ByteLength < right->ByteLength ? left->ByteLength : right->ByteLength);
    int compared;
    {
        const NativeScope transition(common >= 4096);
        compared = std::memcmp(left->Data(), right->Data(), Detail::Integer<std::size_t>(common));
    }
    return compared < 0
               ? -1
               : compared > 0
               ? 1
               : left->ByteLength < right->ByteLength
               ? -1
               : left->ByteLength > right->ByteLength
               ? 1
               : 0;
}

String *sol_rt_string_concat(String *left, String *right)
{
    RequireString(left);
    RequireString(right);
    if (right->ByteLength > (std::numeric_limits<int>::max)() - left->ByteLength)
    {
        sol_rt_fail(6);
    }
    const int length = left->ByteLength + right->ByteLength;
    auto *result = Detail::StorageCast<String>(sol_gc_alloc(&sol_rt_string_type,
                                                            sizeof(String) + Detail::Integer<std::uint64_t>(length)));
    result->ByteLength = length;
    result->RuneCount = -1;
    const TemporaryRoot root(&result->Object);
    const NativeScope transition(length >= 65536);
    std::memcpy(result->Data(), left->Data(), Detail::Integer<std::size_t>(left->ByteLength));
    std::memcpy(result->Data() + left->ByteLength, right->Data(), Detail::Integer<std::size_t>(right->ByteLength));
    return result;
}

String *sol_rt_string_substring(String *value, int start, int length)
{
    RequireString(value);
    if (length < 0 || start < 0 || start > sol_rt_string_length(value) || length > sol_rt_string_length(value) - start)
    {
        sol_rt_fail(4);
    }
    const auto begin = Offset(value, start), end = Offset(value, start + length);
    return sol_rt_string_create(value->Data() + begin, end - begin);
}

int sol_rt_string_index_of(String *value, const String *needle, int start)
{
    RequireString(value);
    RequireString(needle);
    int cursor = Offset(value, start), scalar = start;
    while (needle->ByteLength <= value->ByteLength - cursor)
    {
        int comparison;
        {
            const NativeScope transition(needle->ByteLength >= 4096);
            comparison = std::memcmp(value->Data() + cursor, needle->Data(),
                                     Detail::Integer<std::size_t>(needle->ByteLength));
        }
        if (comparison == 0)
        {
            return scalar;
        }
        if (cursor == value->ByteLength)
        {
            break;
        }
        Decode(value->Data(), value->ByteLength, cursor);
        ++scalar;
    }
    return -1;
}

std::uint32_t sol_rt_string_rune_at(String *value, int index)
{
    RequireString(value);
    int cursor = Offset(value, index);
    if (cursor == value->ByteLength)
    {
        sol_rt_fail(4);
    }
    return Decode(value->Data(), value->ByteLength, cursor);
}

int sol_rt_string_hash(const String *value)
{
    RequireString(value);
    std::uint32_t hash = 2166136261U;
    for (int index = 0; index < value->ByteLength; ++index)
    {
        if (index >= 4096 && (index & 4095) == 0)
        {
            sol_gc_poll();
        }
        hash = (hash ^ std::bit_cast<unsigned char>(Detail::CheckedAt(
                    std::span(value->Data(), Detail::Integer<std::size_t>(value->ByteLength)), index))) * 16777619U;
    }
    return std::bit_cast<std::int32_t>(hash);
}

StringArray *sol_rt_array_create(int length, std::uint32_t stride, const TypeDescriptor *element,
                                 const TypeDescriptor *arrayType)
{
    if (length < 0 || !stride || !element || !arrayType || arrayType->Kind != 2 || arrayType->Size != sizeof(
            StringArray))
    {
        sol_rt_fail(7);
    }
    const auto bytes = sizeof(StringArray) + Detail::Integer<std::uint64_t>(length) * stride;
    auto *array = Detail::StorageCast<StringArray>(sol_gc_alloc(arrayType, bytes));
    array->Length = length;
    array->Stride = stride;
    array->Element = element;
    return array;
}

Throwable *sol_rt_exception_create(const TypeDescriptor *type, String *message, Throwable *cause)
{
    const auto *base = type;
    while (base && base != &sol_rt_throwable_type)
    {
        base = base->Base;
    }
    if (!base)
    {
        sol_rt_fail(7);
    }
    auto *error = Detail::StorageCast<Throwable>(sol_gc_alloc(type, sizeof(Throwable)));
    sol_gc_store_ref(Detail::StorageCast<Header *>(&error->Message), Detail::StorageCast<Header>(message));
    sol_gc_store_ref(Detail::StorageCast<Header *>(&error->Cause), Detail::StorageCast<Header>(cause));
    return error;
}

bool sol_rt_is_instance(const Header *value, const TypeDescriptor *type)
{
    return IsInstance(value, type);
}

void sol_rt_exception_add_suppressed(Throwable *primary, Throwable *secondary)
{
    if (!IsInstance(Detail::StorageCast<Header>(primary), &sol_rt_throwable_type)
        || !IsInstance(Detail::StorageCast<Header>(secondary), &sol_rt_throwable_type) || primary == secondary)
    {
        sol_rt_fail(9);
    }
    while (!SuppressedLock.try_lock())
    {
        sol_gc_poll();
        std::this_thread::yield();
    }
    const std::lock_guard guard(SuppressedLock, std::adopt_lock);
    try
    {
        std::vector<Throwable *> pending{secondary}, visited;
        while (!pending.empty())
        {
            auto *value = pending.back();
            pending.pop_back();
            if (value == primary)
            {
                sol_rt_fail(9);
            }
            if (std::find(visited.begin(), visited.end(), value) != visited.end())
            {
                continue;
            }
            if (visited.size() >= 1024)
            {
                sol_rt_fail(9);
            }
            visited.push_back(value);
            if (value->Cause)
            {
                pending.push_back(value->Cause);
            }
            auto *suppressed = std::atomic_ref(value->Suppressed).load(std::memory_order_acquire);
            if (suppressed)
            {
                for (int index = 0; index < suppressed->Length; ++index)
                {
                    pending.push_back(Detail::StorageCast<Throwable>(suppressed->Data()[index]));
                }
            }
        }
        auto *previous = std::atomic_ref(primary->Suppressed).load(std::memory_order_acquire);
        const int length = previous ? previous->Length : 0;
        if (length >= 64)
        {
            return;
        } // Preserve the primary error when the bounded suppression list is full.
        auto *array = sol_rt_array_create(length + 1, sizeof(Header *), &sol_rt_throwable_type, &ThrowableArrayType);
        for (int index = 0; index < length; ++index)
        {
            sol_gc_store_ref(Detail::StorageCast<Header *>(&array->Data()[index]),
                             Detail::StorageCast<Header>(previous->Data()[index]));
        }
        sol_gc_store_ref(Detail::StorageCast<Header *>(&array->Data()[length]), &secondary->Object);
        sol_gc_store_ref(Detail::StorageCast<Header *>(&primary->Suppressed), &array->Object);
    }
    catch (...)
    {
        sol_rt_fail(6);
    }
}

[[noreturn]] void sol_rt_throw(Throwable *value) noexcept(false)
{
    throw ExceptionCarrier(value);
}
}

namespace OpenDot::Sol::Runtime
{
struct ExceptionCarrier::RootControl final
{
    std::atomic<unsigned> Owners{1};
    // NOLINTNEXTLINE(clang-diagnostic-padded): Preserve natural ABI and compiler capture alignment.
    RuntimeHandle *Handle;

    explicit RootControl(Throwable &value) noexcept(false)
        : Handle(sol_gc_create_handle(&value.Object, false))
    {
    }
};

ExceptionCarrier::ExceptionCarrier(Throwable *value) : _value(value)
{
    if (!value || !IsInstance(&value->Object, &sol_rt_throwable_type))
    {
        sol_rt_fail(7);
    }
    try
    {
        _root = std::make_unique<RootControl>(*value).release();
    }
    catch (...)
    {
        sol_rt_fail(6);
    }
}

ExceptionCarrier::ExceptionCarrier(const ExceptionCarrier &other) noexcept : _root(other._root), _value(other._value)
{
    _root->Owners.fetch_add(1, std::memory_order_relaxed);
}

ExceptionCarrier::~ExceptionCarrier()
{
    if (_root->Owners.fetch_sub(1, std::memory_order_acq_rel) == 1)
    {
        const std::unique_ptr<RootControl> lastOwner{_root};
        sol_gc_release_handle(lastOwner->Handle);
    }
}

Throwable *ExceptionCarrier::Value() const noexcept
{
    return _value;
}
}
