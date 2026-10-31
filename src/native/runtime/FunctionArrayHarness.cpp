// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Core.h"
#include <cstdint>
#include <cstring>

using namespace OpenDot::Sol::Runtime;

namespace
{
using Callback = std::int32_t (*)(std::int32_t);

std::int32_t Increment(std::int32_t value) noexcept
{
    return value + 1;
}

class Frame final
{
    Header *slots_[1]{};
    RootFrame frame_{nullptr, slots_, 1, 0};

public:
    Frame() { sol_gc_push_frame(&frame_); }
    ~Frame() { sol_gc_pop_frame(&frame_); }
    Frame(const Frame &) = delete;
    Frame &operator=(const Frame &) = delete;

    void Store(Header *value) noexcept { sol_gc_store_ref(&slots_[0], value); }
};
}

int RunFunctionArrayTests()
{
    static_assert(sizeof(Callback) == sizeof(std::uintptr_t));
    sol_gc_attach();
    int result = 0;
    {
        Frame roots;
        auto *callbacks = sol_rt_array_new(1, 12);
        roots.Store(&callbacks->Object);
        if (callbacks->Stride != sizeof(Callback))
        {
            result = 1;
        }
        else if (callbacks->Element->Size != sizeof(Callback)) { result = 2; }
        else if (callbacks->Element->ScanShape != nullptr) { result = 3; }
        else if (callbacks->Object.Type->ScanShape != nullptr) { result = 4; }
        else if (callbacks->Object.Type->TypeId != 232) { result = 5; }

        if (result == 0)
        {
            auto *decoy = sol_gc_alloc(&sol_rt_object_type, sizeof(Header));
            const auto decoyBits = reinterpret_cast<std::uintptr_t>(decoy);
            std::memcpy(sol_rt_array_address(callbacks, 0, sizeof(Callback)), &decoyBits, sizeof(decoyBits));
            sol_gc_collect();
            if (sol_gc_stats().ObjectCount != 1)
            {
                result = 6; // Callable code-pointer storage is scalar and must not retain a heap object.
            }
        }

        if (result == 0)
        {
            const Callback expected = &Increment;
            std::memcpy(sol_rt_array_address(callbacks, 0, sizeof(Callback)), &expected, sizeof(expected));
            Callback actual = nullptr;
            std::memcpy(&actual, sol_rt_array_address(callbacks, 0, sizeof(Callback)), sizeof(actual));
            if (actual != expected || actual(41) != 42)
            {
                result = 7;
            }
        }
        roots.Store(nullptr);
        sol_gc_collect();
        if (result == 0 && sol_gc_stats().ObjectCount != 0)
        {
            result = 8;
        }
    }
    sol_rt_shutdown();
    return result;
}
