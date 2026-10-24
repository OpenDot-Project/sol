// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include "../runtime/Core.h"
#include <array>
#include <cstdint>
#include <stdexcept>

namespace OpenDot::Sol::Fuzz
{
using namespace Runtime;
using Input = std::array<unsigned char, 64>;

inline std::uint32_t Choice(const Input &input, std::uint32_t salt) noexcept
{
    std::uint32_t value = 2166136261U ^ (salt * 16777619U);
    for (std::size_t i = 8; i < input.size(); ++i)
    {
        value = (value ^ input[i]) * 16777619U;
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
    std::array<Header *, 128> _slots{};
    RootFrame _frame{nullptr, _slots.data(), static_cast<std::uint32_t>(_slots.size()), 0};

public:
    Roots()
    {
        sol_gc_attach();
        sol_gc_push_frame(&_frame);
    }

    Roots(const Roots &) = delete;
    Roots &operator=(const Roots &) = delete;

    ~Roots()
    {
        sol_gc_pop_frame(&_frame);
        sol_gc_detach();
    }

    Header *&operator[](std::size_t index)
    {
        return _slots.at(index);
    }

    void Set(std::size_t index, Header *value)
    {
        sol_gc_store_ref(&_slots.at(index), value);
    }

    void Clear()
    {
        for (auto &slot : _slots)
        {
            sol_gc_store_ref(&slot, nullptr);
        }
    }
};

void RuntimeCase(const Input &input, Roots &roots);
void GcCase(const Input &input, Roots &roots);
void LoadBackend(const wchar_t *path);
void UnloadBackend() noexcept;
void BackendCase(const Input &input);
}
