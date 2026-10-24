// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Fuzz.h"
// NOLINTNEXTLINE(CppUnusedIncludeDirective): std::size_t is used directly below.
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>

#ifndef SOL_FUZZ_TARGET
#error SOL_FUZZ_TARGET must select runtime, backend, or gc.
#endif

namespace OpenDot::Sol::Fuzz
{
namespace
{
constexpr std::size_t MaximumInputBytes = 8192;
static_assert(SOL_FUZZ_TARGET == 1 || SOL_FUZZ_TARGET == 2 || SOL_FUZZ_TARGET == 3);
constexpr std::uint8_t Target = SOL_FUZZ_TARGET;

struct RuntimeSession final
{
    std::unique_ptr<Roots> RootSet = std::make_unique<Roots>();

    RuntimeSession() = default;
    RuntimeSession(const RuntimeSession &) = delete;
    RuntimeSession &operator=(const RuntimeSession &) = delete;

    // NOLINTNEXTLINE(clang-diagnostic-unneeded-member-function): runtime and GC target builds use this cleanup.
    ~RuntimeSession()
    {
        RootSet.reset();
        sol_rt_shutdown();
    }
};

struct BackendSession final
{
    BackendSession() { LoadBackend(); }
    BackendSession(const BackendSession &) = delete;
    BackendSession &operator=(const BackendSession &) = delete;
    ~BackendSession() { UnloadBackend(); }
};

template <std::uint8_t FuzzTarget>
auto &GetSession()
{
    if constexpr (FuzzTarget == 2)
    {
        static BackendSession session;
        return session;
    }
    else
    {
        static RuntimeSession session;
        return session;
    }
}

template <std::uint8_t FuzzTarget>
void Execute(const Input &input)
{
    auto &session = GetSession<FuzzTarget>();
    if constexpr (FuzzTarget == 1)
    {
        RuntimeCase(input, *session.RootSet);
    }
    else if constexpr (FuzzTarget == 2)
    {
        BackendCase(input);
    }
    else
    {
        GcCase(input, *session.RootSet);
    }
}

Input Normalize(const std::uint8_t *data, std::size_t size)
{
    Input input{};
    std::memcpy(input.data(), "SFZ1", 4);
    input[4] = Target;
    input[5] = size > 5 ? data[5] : size > 0 ? data[0] : 0;
    input[6] = 1;

    for (std::size_t index = 0; index < size; ++index)
    {
        if (index == 5)
        {
            continue;
        }
        if (index >= 8 && index < input.size())
        {
            input[index] = data[index];
        }
        else
        {
            const auto destination = 8 + index % (input.size() - 8);
            input[destination] = static_cast<std::uint8_t>(input[destination] ^ data[index]);
        }
    }
    return input;
}
}
}

extern "C" int LLVMFuzzerTestOneInput(const std::uint8_t *data, std::size_t size)
{
    using namespace OpenDot::Sol::Fuzz;
    if (size > MaximumInputBytes)
    {
        return 0;
    }

    const Input input = Normalize(data, size);
    Execute<Target>(input);
    return 0;
}
