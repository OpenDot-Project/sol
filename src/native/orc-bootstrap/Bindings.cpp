// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Bindings.h"
#include "../runtime/Core.h"
#include <bit>
#include <cmath>
// NOLINTNEXTLINE(CppUnusedIncludeDirective): Direct declaration of std::strcmp used by the binding lookup.
#include <cstring>
#include <limits>
#include <stdexcept>
#include <type_traits>
#include <tuple>
#include <utility>
#include <windows.h>

// NOLINTNEXTLINE(bugprone-reserved-identifier, clang-diagnostic-reserved-identifier): Windows stack-probe ABI.
extern "C" void __chkstk();

namespace OpenDot::Sol::OrcBootstrap
{
RuntimeFunctions RuntimeApi{};

namespace
{
#ifndef SOL_RT_STATIC
std::uint64_t RuntimeAddress(HMODULE runtime, const char *name)
{
    const auto address = runtime ? GetProcAddress(runtime, name) : nullptr;
    if (!address)
    {
        throw std::runtime_error("SolRT.dll is missing a required runtime export.");
    }
    return std::bit_cast<std::uint64_t>(address);
}
#endif

#ifdef SOL_RT_STATIC
// /EHc treats an extern-C zero-argument function as nonthrowing during pointer deduction.
// Keep the runtime's explicit throwing declaration and bind this equivalent C++ ABI thunk.
[[noreturn]] void Rethrow()
{
    sol_rt_rethrow();
}
#endif

template <class T> consteval unsigned Type() noexcept
{
    if constexpr (std::is_void_v<T>)
    {
        return 0;
    }
    else if constexpr (std::is_pointer_v<T>)
    {
        return 4;
    }
    else if constexpr (std::is_same_v<T, bool>)
    {
        return 1;
    }
    else if constexpr (std::is_same_v<T, float>)
    {
        return 5;
    }
    else if constexpr (std::is_same_v<T, double>)
    {
        return 6;
    }
    else if constexpr (std::is_integral_v<T> && sizeof(T) == 4)
    {
        return 2;
    }
    else
    {
        static_assert(std::is_integral_v<T> && sizeof(T) == 8);
        return 3;
    }
}

template <class T> struct Signature;

template <class R, class... A> struct Signature<R(A...)>
{
    template <std::size_t... I> static consteval std::uint64_t Encode(std::index_sequence<I...>) noexcept
    {
        return Type<R>() | (sizeof...(A) << 4)
               | ((static_cast<std::uint64_t>(Type<A>()) << (8 + I * 4)) | ... | std::uint64_t{0});
    }

    static consteval std::uint64_t Prototype() noexcept
    {
        static_assert(sizeof...(A) <= 14);
        return Encode(std::index_sequence_for<A...>{});
    }
};

template <class R, class... A> struct Signature<R(A...) noexcept> : Signature<R(A...)>
{
};

}

std::vector<SolJitSymbol> Bindings(HMODULE runtime)
{
    const auto crt = GetModuleHandleW(L"vcruntime140.dll");
    const auto personality = crt ? GetProcAddress(crt, "__CxxFrameHandler3") : nullptr;
    if (!personality)
    {
        throw std::runtime_error("The qualified Windows EH personality is unavailable.");
    }
    std::vector<SolJitSymbol> result{
        {"__CxxFrameHandler3", std::bit_cast<std::uint64_t>(personality), 2},
    };
#ifdef SOL_RT_STATIC
    std::ignore = runtime;
#define SOL_BIND(name) result.push_back({#name, std::bit_cast<std::uint64_t>(&(name)), Signature<decltype(name)>::Prototype()})
#else
#define SOL_BIND(name) result.push_back({#name, RuntimeAddress(runtime, #name), Signature<decltype(name)>::Prototype()})
#endif
    SOL_BIND(sol_rt_literal);
    SOL_BIND(sol_rt_arguments);
    SOL_BIND(sol_rt_shutdown);
    SOL_BIND(sol_rt_fail);
    SOL_BIND(sol_rt_check_abi);
    SOL_BIND(sol_rt_write_string);
    SOL_BIND(sol_rt_write_integer);
    SOL_BIND(sol_rt_write_float);
    SOL_BIND(sol_rt_newline);
    SOL_BIND(sol_rt_array_length);
    SOL_BIND(sol_rt_string_create);
    SOL_BIND(sol_rt_string_concat);
    SOL_BIND(sol_rt_string_compare);
    SOL_BIND(sol_rt_string_length);
    SOL_BIND(sol_rt_string_substring);
    SOL_BIND(sol_rt_string_index_of);
    SOL_BIND(sol_rt_string_hash);
    SOL_BIND(sol_gc_attach);
    SOL_BIND(sol_gc_enter_native);
    SOL_BIND(sol_gc_leave_native);
    SOL_BIND(sol_gc_enter_export);
    SOL_BIND(sol_gc_leave_export);
    SOL_BIND(sol_gc_push_frame);
    SOL_BIND(sol_gc_pop_frame);
    SOL_BIND(sol_gc_store_ref);
    SOL_BIND(sol_gc_poll);
    SOL_BIND(sol_gc_collect);
    SOL_BIND(sol_rt_array_new);
    SOL_BIND(sol_rt_array_address);
    SOL_BIND(sol_rt_exception_create);
    SOL_BIND(sol_rt_exception_add_suppressed);
    SOL_BIND(sol_rt_is_instance);
    SOL_BIND(sol_rt_throw);
    SOL_BIND(sol_rt_current_exception);
#ifdef SOL_RT_STATIC
    result.push_back({"sol_rt_rethrow", std::bit_cast<std::uint64_t>(&Rethrow), 0});
#else
    SOL_BIND(sol_rt_rethrow);
#endif
#undef SOL_BIND
    result.push_back({"memcpy", std::bit_cast<std::uint64_t>(&memcpy), Signature<decltype(memcpy)>::Prototype()});
    result.push_back({"memset", std::bit_cast<std::uint64_t>(&memset), Signature<decltype(memset)>::Prototype()});
    result.push_back({"__chkstk", std::bit_cast<std::uint64_t>(&__chkstk), Signature<decltype(__chkstk)>::Prototype()});
    double (*const remainder)(double, double) = &std::fmod;
    result.push_back({"fmod", std::bit_cast<std::uint64_t>(remainder), Signature<double(double, double)>::Prototype()});
    float (*const remainderSingle)(float, float) = &std::fmod;
    result.push_back({"fmodf", std::bit_cast<std::uint64_t>(remainderSingle),
                      Signature<float(float, float)>::Prototype()});
#ifdef SOL_RT_STATIC
#define SOL_DATA(name) result.push_back({#name, std::bit_cast<std::uint64_t>(&(name)), std::numeric_limits<std::uint64_t>::max()})
#else
#define SOL_DATA(name) result.push_back({#name, RuntimeAddress(runtime, #name), std::numeric_limits<std::uint64_t>::max()})
#endif
    SOL_DATA(sol_rt_object_type);
    SOL_DATA(sol_rt_string_type);
    SOL_DATA(sol_rt_string_array_type);
    SOL_DATA(sol_rt_reference_array_type);
    SOL_DATA(sol_rt_throwable_type);
    SOL_DATA(sol_rt_exception_type);
    SOL_DATA(sol_rt_runtime_exception_type);
#undef SOL_DATA
    const auto find = [&](const char *name) {
        for (const auto &symbol : result)
        {
            if (std::strcmp(symbol.Name, name) == 0)
            {
                return symbol.Address;
            }
        }
        throw std::runtime_error("Missing bootstrap runtime binding.");
    };
    RuntimeApi = {
        std::bit_cast<decltype(&sol_gc_attach)>(find("sol_gc_attach")),
        std::bit_cast<decltype(&sol_gc_enter_native)>(find("sol_gc_enter_native")),
        std::bit_cast<decltype(&sol_gc_leave_native)>(find("sol_gc_leave_native")),
        std::bit_cast<decltype(&sol_gc_push_frame)>(find("sol_gc_push_frame")),
        std::bit_cast<decltype(&sol_gc_pop_frame)>(find("sol_gc_pop_frame")),
        std::bit_cast<decltype(&sol_rt_string_create)>(find("sol_rt_string_create")),
        std::bit_cast<decltype(&sol_rt_exception_create)>(find("sol_rt_exception_create")),
        std::bit_cast<decltype(&sol_rt_throw)>(find("sol_rt_throw")),
        std::bit_cast<decltype(&sol_rt_fail)>(find("sol_rt_fail")),
        std::bit_cast<const Runtime::TypeDescriptor *>(find("sol_rt_runtime_exception_type")),
    };
    return result;
}
}
