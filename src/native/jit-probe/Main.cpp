// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "../llvm-native/JitApi.h"
#include "../runtime/Core.h"
#include "../runtime/CheckedAccess.h"
#include <array>
#include <atomic>
#include <bit>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <limits>
#include <span>
#include <stdexcept>
#include <thread>
#include <vector>
#include <windows.h>

namespace
{
using namespace OpenDot::Sol::Runtime;
std::atomic<unsigned> Collections{0};
std::atomic<std::uint64_t> CurrentEngine{0};

void EnterCompile()
{
    sol_gc_enter_native();
    std::thread collector([]() noexcept {
        sol_gc_attach();
        sol_gc_collect();
        sol_gc_detach();
        Collections.fetch_add(1, std::memory_order_relaxed);
    });
    collector.join();
}

[[noreturn]] void CompileFailure() noexcept
{
    std::array<char, 1024> error{};
    const auto printed = SolJitCopyError(CurrentEngine.load(std::memory_order_acquire), error.data(), error.size())
                             ? std::fprintf(stderr, "Lazy compilation failed: %s\n", error.data())
                             : std::fputs("Lazy compilation failed.\n", stderr);
    if (printed < 0)
    {
        std::abort();
    }
    sol_rt_fail(9);
}

constexpr std::uint64_t Prototype(unsigned result, std::initializer_list<unsigned> parameters = {})
{
    std::uint64_t value = result | (Detail::Integer<std::uint64_t>(parameters.size()) << 4);
    unsigned shift = 8;
    for (const auto type : parameters)
    {
        value |= Detail::Integer<std::uint64_t>(type) << shift;
        shift += 4;
    }
    return value;
}

template <class T> std::uint64_t Address(T *pointer) noexcept
{
    return std::bit_cast<std::uint64_t>(pointer);
}

std::vector<SolJitSymbol> Symbols()
{
    const auto runtime = GetModuleHandleW(L"vcruntime140.dll");
    const auto personality = runtime ? GetProcAddress(runtime, "__CxxFrameHandler3") : nullptr;
    if (!personality)
    {
        throw std::runtime_error("The qualified Windows C++ personality is missing.");
    }
    return {
        {"__CxxFrameHandler3", Address(personality), Prototype(2)},
        {"sol_gc_attach", Address(&sol_gc_attach), Prototype(0)},
        {"sol_gc_collect", Address(&sol_gc_collect), Prototype(0)},
        {"sol_gc_push_frame", Address(&sol_gc_push_frame), Prototype(0, {4})},
        {"sol_gc_pop_frame", Address(&sol_gc_pop_frame), Prototype(0, {4})},
        {"sol_gc_store_ref", Address(&sol_gc_store_ref), Prototype(0, {4, 4})},
        {"sol_rt_shutdown", Address(&sol_rt_shutdown), Prototype(0)},
        {"sol_rt_exception_create", Address(&sol_rt_exception_create), Prototype(4, {4, 4, 4})},
        {"sol_rt_throw", Address(&sol_rt_throw), Prototype(0, {4})},
        {"sol_rt_current_exception", Address(&sol_rt_current_exception), Prototype(4)},
        {"sol_rt_is_instance", Address(&sol_rt_is_instance), Prototype(1, {4, 4})},
        {"sol_rt_runtime_exception_type", Address(&sol_rt_runtime_exception_type),
         std::numeric_limits<std::uint64_t>::max()},
    };
}
}

int wmain(int argc, wchar_t **argv)
{
    using namespace OpenDot::Sol::Runtime;
    try
    {
        if (argc != 2 || !argv)
        {
            return 2;
        }
        const auto command = std::span{argv, Detail::Integer<std::size_t>(argc)};
        std::ifstream file(std::filesystem::path(Detail::CheckedAt(command, 1)), std::ios::binary | std::ios::ate);
        const auto length = file.tellg();
        if (length < 88 || length > 64LL * 1024 * 1024 + 88)
        {
            throw std::runtime_error("Invalid probe payload size.");
        }
        std::vector<unsigned char> payload(Detail::Integer<std::size_t>(static_cast<std::streamoff>(length)));
        file.seekg(0);
        void *bytes = payload.data();
        if (!file.read(static_cast<char *>(bytes), Detail::Integer<std::streamsize>(payload.size())))
        {
            throw std::runtime_error("Cannot read probe payload.");
        }
        auto symbols = Symbols();
#ifdef SOL_RT_STATIC
        constexpr std::uint32_t flags = 1;
#else
        constexpr std::uint32_t flags = 0;
#endif
        const SolJitOptions options{0x534F4C0000000002ULL, Address(&CompileFailure), EnterCompile, sol_gc_leave_native,
                                    flags, sizeof(SolJitOptions)};
        if (SolJitProtocol() != 0x534F4C4A00000001ULL)
        {
            throw std::runtime_error("Native JIT protocol mismatch.");
        }
        sol_gc_attach();
        sol_gc_enter_native();
        std::uint64_t engine = 0, entry = 0;
        const bool opened = SolJitOpen(payload.data(), payload.size(), symbols.data(), symbols.size(), &options,
                                       &engine, &entry);
        sol_gc_leave_native();
        if (!opened)
        {
            throw std::runtime_error(SolLlvmError());
        }
        CurrentEngine.store(engine, std::memory_order_release);
        SolJitStats before{};
        if (!SolJitGetStats(engine, &before) || before.CompiledFunctions || before.CompileCallbacks)
        {
            throw std::runtime_error("Opening an executable compiled program functions eagerly.");
        }
        const auto main = std::bit_cast<int (*)(int, wchar_t **)>(entry);
        if (!main)
        {
            throw std::runtime_error("The lazy entry address is null.");
        }
        const int result = main(1, argv);
        if (result)
        {
            return result;
        }
        SolJitStats after{};
        if (!SolJitGetStats(engine, &after) || after.CompiledFunctions != 2 || after.CompileCallbacks < 2
            || after.UnwindTables < 2 || after.Failures || Collections.load(std::memory_order_relaxed) < 2)
        {
            throw std::runtime_error("Lazy compilation/unwind/GC counters do not match the executed bodies.");
        }
        std::uint64_t repeated = 0, unused = 0;
        if (!SolJitLookup(engine, "wmain", &repeated) || repeated != entry
            || !SolJitLookup(engine, "sol_probe_unused", &unused) || !unused)
        {
            throw std::runtime_error("Lazy stub identity is unstable.");
        }
        SolJitStats final{};
        if (!SolJitGetStats(engine, &final) || final.CompiledFunctions != after.CompiledFunctions)
        {
            throw std::runtime_error("Looking up an unused function compiled its body.");
        }
        if (std::printf("lazy-functions=%llu callbacks=%llu unwind-tables=%llu\n",
                        after.CompiledFunctions, after.CompileCallbacks, after.UnwindTables) < 0)
        {
            return 1;
        }
        return 0;
    }
    catch (const std::exception &error)
    {
        return std::fprintf(stderr, "%s\n", error.what()) < 0 ? 2 : 1;
    }
}
