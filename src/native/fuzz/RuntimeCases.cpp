// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Fuzz.h"
#include <bit>
#include <cstring>
#include <string>
#include <vector>

namespace OpenDot::Sol::Fuzz
{
namespace
{
struct Text final
{
    std::string Bytes;
    std::vector<std::size_t> Offsets{0};
    std::vector<std::uint32_t> Runes;

    void Append(std::string bytes, std::uint32_t rune)
    {
        Bytes += bytes;
        Offsets.push_back(Bytes.size());
        Runes.push_back(rune);
    }
};

Text MakeText(const Input &input, unsigned mode)
{
    Text result;
    const unsigned length = mode == 5 || mode == 6
                                ? 0
                                : mode == 1
                                ? 128 + Choice(input, 0) % 256
                                : Choice(input, 0) % 32;
    for (unsigned index = 0; index < length; ++index)
    {
        const auto byte = Choice(input, index + 1);
        if (mode == 2 && index % 3 == 0)
        {
            result.Append("\xC3\xA9", 0xE9);
        }
        else if (mode == 3 && index % 3 == 0)
        {
            result.Append("\xF0\x9F\x98\x80", 0x1F600);
        }
        else if (mode == 4 && index % 3 == 0)
        {
            result.Append(std::string(1, '\0'), 0);
        }
        else
        {
            const auto value = std::bit_cast<char>(OpenDot::Sol::CheckedInteger<unsigned char>('a' + byte % 26));
            result.Append(std::string(1, value), std::bit_cast<unsigned char>(value));
        }
    }
    return result;
}

void CheckString(String *value, const std::string &bytes, int runes)
{
    Require(
        value && value->ByteLength == OpenDot::Sol::CheckedInteger<int>(bytes.size()) && sol_rt_string_length(value) ==
        runes &&
        std::memcmp(value->Data(), bytes.data(), bytes.size()) == 0,
        "Runtime String differs from byte/rune model.");
}
}

void RuntimeCase(const Input &input, Roots &roots)
{
    const auto mode = input.at(5);
    const auto model = MakeText(input, mode);
    auto *first = sol_rt_string_create(model.Bytes.data(), OpenDot::Sol::CheckedInteger<int>(model.Bytes.size()));
    roots.Set(0, &first->Object);
    CheckString(first, model.Bytes, OpenDot::Sol::CheckedInteger<int>(model.Runes.size()));
    for (std::size_t index = 0; index < model.Runes.size(); ++index)
    {
        Require(sol_rt_string_rune_at(first, OpenDot::Sol::CheckedInteger<int>(index)) == model.Runes.at(index),
                "Runtime rune index mismatch.");
    }
    const std::string tail = mode == 5 || mode == 7 ? "" : std::to_string(Choice(input, 1)) + "tail";
    auto *second = sol_rt_string_create(tail.data(), OpenDot::Sol::CheckedInteger<int>(tail.size()));
    roots.Set(1, &second->Object);
    auto *combined = sol_rt_string_concat(first, second);
    roots.Set(2, &combined->Object);
    CheckString(combined, model.Bytes + tail, OpenDot::Sol::CheckedInteger<int>(model.Runes.size() + tail.size()));
    const auto start = mode == 9 ? 0 : Choice(input, 2) % (model.Runes.size() + 1);
    const auto length = mode == 8
                            ? 0
                            : mode == 9
                            ? model.Runes.size()
                            : Choice(input, 3) % (model.Runes.size() - start + 1);
    auto *slice = sol_rt_string_substring(first, OpenDot::Sol::CheckedInteger<int>(start),
                                          OpenDot::Sol::CheckedInteger<int>(length));
    roots.Set(3, &slice->Object);
    CheckString(slice, model.Bytes.substr(model.Offsets.at(start),
                                          model.Offsets.at(start + length) - model.Offsets.at(start)),
                OpenDot::Sol::CheckedInteger<int>(length));
    auto *copy = sol_rt_string_create(model.Bytes.data(), OpenDot::Sol::CheckedInteger<int>(model.Bytes.size()));
    roots.Set(4, &copy->Object);
    Require(sol_rt_string_compare(first, copy) == 0 && sol_rt_string_hash(first) == sol_rt_string_hash(copy),
            "Runtime String equality/hash contract failed.");
    std::uint32_t hash = 2166136261U;
    for (auto byte : model.Bytes)
    {
        hash = (hash ^ std::bit_cast<unsigned char>(byte)) * 16777619U;
    }
    Require(std::bit_cast<std::uint32_t>(sol_rt_string_hash(first)) == hash, "Runtime hash differs from FNV model.");
    const auto compared = model.Bytes.compare(tail);
    Require(sol_rt_string_compare(first, second) == (compared < 0 ? -1 : compared > 0 ? 1 : 0),
            "Runtime String ordering mismatch.");
    const auto searchStart = mode == 11 && !model.Runes.empty() ? Choice(input, 4) % model.Runes.size() : 0;
    const std::string needleBytes = mode == 12
                                        ? "~missing~"
                                        : model.Bytes.substr(model.Offsets.at(searchStart),
                                                             model.Offsets.at(model.Runes.empty() ? 0 : searchStart + 1)
                                                             -
                                                             model.Offsets.at(searchStart));
    auto *needle = sol_rt_string_create(needleBytes.data(), OpenDot::Sol::CheckedInteger<int>(needleBytes.size()));
    roots.Set(5, &needle->Object);
    Require(sol_rt_string_index_of(first, needle, OpenDot::Sol::CheckedInteger<int>(searchStart)) ==
            (mode == 12 ? -1 : OpenDot::Sol::CheckedInteger<int>(searchStart)),
            "Runtime String search model mismatch.");
    const int count = mode == 14
                          ? 0
                          : mode == 15
                          ? 1
                          : mode == 16
                          ? 2 + OpenDot::Sol::CheckedInteger<int>(Choice(input, 5) % 30)
                          : OpenDot::Sol::CheckedInteger<int>(Choice(input, 5) % 32);
    auto *array = sol_rt_array_create(count, sizeof(String *), &sol_rt_string_type, &sol_rt_string_array_type);
    Require(array != nullptr, "Runtime allocation returned null.");
    roots.Set(6, &array->Object);
    Require(sol_rt_array_length(array) == count, "Runtime array length mismatch.");
    for (int index = 0; index < count; ++index)
    {
        sol_gc_store_ref(
            Detail::StorageCast<Header *>(
                &Detail::CheckedAt(std::span(array->Data(), Detail::Integer<std::size_t>(array->Length)), index)),
            &first->Object);
        Require(sol_rt_array_get(array, index) == first, "Runtime array storage mismatch.");
    }
    if (mode >= 17)
    {
        auto *cause = sol_rt_exception_create(&sol_rt_exception_type, first, nullptr);
        roots.Set(7, &cause->Object);
        auto *error = sol_rt_exception_create(&sol_rt_runtime_exception_type, second, cause);
        Require(error != nullptr, "Runtime allocation returned null.");
        roots.Set(8, &error->Object);
        Require(error->Cause == cause && error->Message == second && sol_rt_is_instance(
                    &error->Object, &sol_rt_throwable_type),
                "Runtime exception identity/cause mismatch.");
        if (mode == 18)
        {
            auto *suppressed = sol_rt_exception_create(&sol_rt_exception_type, second, nullptr);
            roots.Set(9, &suppressed->Object);
            sol_rt_exception_add_suppressed(error, suppressed);
            Require(error->Suppressed && error->Suppressed->Length == 1 &&
                    Detail::StorageCast<Throwable>(sol_rt_array_get(error->Suppressed, 0)) == suppressed,
                    "Suppressed exception mismatch.");
        }
        if (mode == 19)
        {
            try
            {
                sol_rt_throw(error);
            }
            catch (const ExceptionCarrier &carrier)
            {
                roots.Set(8, nullptr);
                sol_gc_collect();
                Require(carrier.Value() == error && carrier.Value()->Message == second,
                        "Exception carrier lost its rooted value.");
            }
        }
    }
    roots.Clear();
    sol_gc_collect();
}
}
