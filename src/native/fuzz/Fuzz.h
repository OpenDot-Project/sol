// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include "../runtime/Core.h"
#include "../runtime/CheckedAccess.h"
#include <array>
#include <cstdint>
#include <stdexcept>

namespace OpenDot::Sol::Fuzz
{
using namespace Runtime;
using Input = std::array<unsigned char, 64>;

// Oracle/input checks must also work in the backend-only process, which does not link SolRT.
template <class Element, std::size_t Extent>
constexpr Element &CheckedElement(std::span<Element, Extent> values, std::size_t index)
{
    if (index >= values.size())
    {
        throw std::out_of_range("Fuzz input index is outside its storage.");
    }
    return values.subspan(index, 1).front();
}

template <class Element, std::size_t Size>
constexpr Element &CheckedElement(std::array<Element, Size> &values, std::size_t index)
{
    return values.at(index);
}

inline std::uint32_t Choice(const Input &input, std::uint32_t salt) noexcept(false)
{
    std::uint32_t value = 2166136261U ^ (salt * 16777619U);
    for (std::size_t i = 8; i < input.size(); ++i)
    {
        value = (value ^ input.at(i)) * 16777619U;
    }
    return value;
}

inline void Require(bool condition, const char *message)
{
    if (!condition)
    {
        throw std::runtime_error(message);
    }
}

class Roots final
{
public:
    static constexpr std::uint32_t Capacity = 128;

private:
    std::array<Header *, Capacity> _slots{};
    RootFrame _frame{nullptr, _slots.data(), 0, 0};

public:
    explicit Roots(std::uint32_t slots)
    {
        if (slots > Capacity)
        {
            throw std::out_of_range("Fuzz root budget exceeds its storage.");
        }
        _frame.Count = slots;
        sol_gc_attach();
        sol_gc_push_frame(&_frame);
    }

    Roots(const Roots &) = delete;
    Roots &operator=(const Roots &) = delete;
    Roots(Roots &&) = delete;
    Roots &operator=(Roots &&) = delete;

    ~Roots()
    {
        sol_gc_pop_frame(&_frame);
        sol_gc_detach();
    }

    Header *&At(std::size_t index) noexcept
    {
        return Detail::CheckedAt(std::span(_slots.data(), _frame.Count), index);
    }

    void Set(std::size_t index, Header *value) noexcept(false)
    {
        sol_gc_store_ref(&At(index), value);
    }

    void Clear() noexcept(false)
    {
        for (auto &slot : std::span(_slots.data(), _frame.Count))
        {
            sol_gc_store_ref(&slot, nullptr);
        }
    }
};

void RuntimeCase(const Input &input, Roots &roots);
void GcCase(const Input &input, Roots &roots);
void LoadBackend();
void UnloadBackend() noexcept;
void BackendCase(const Input &input);
int ExecuteConstantCoff(std::span<const unsigned char> object);
}
