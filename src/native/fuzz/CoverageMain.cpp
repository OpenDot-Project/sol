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
    std::unique_ptr<Roots> RootSet = std::make_unique<Roots>(Roots::Capacity);

    RuntimeSession() noexcept(false) = default;
    RuntimeSession(const RuntimeSession &) = delete;
    RuntimeSession &operator=(const RuntimeSession &) = delete;
    RuntimeSession(RuntimeSession &&) = delete;
    RuntimeSession &operator=(RuntimeSession &&) = delete;

    // NOLINTNEXTLINE(clang-diagnostic-unneeded-member-function): runtime and GC target builds use this cleanup.
    ~RuntimeSession()
    {
        RootSet.reset();
        sol_rt_shutdown();
    }
};

struct BackendSession final
{
    BackendSession() noexcept(false)
    {
        LoadBackend();
    }

    BackendSession(const BackendSession &) = delete;
    BackendSession &operator=(const BackendSession &) = delete;
    BackendSession(BackendSession &&) = delete;
    BackendSession &operator=(BackendSession &&) = delete;

    ~BackendSession()
    {
        UnloadBackend();
    }
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
    const auto &session = GetSession<FuzzTarget>();
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

Input Normalize(std::span<const std::uint8_t> data) noexcept(false)
{
    const auto size = data.size();
    Input input{};
    std::memcpy(input.data(), "SFZ1", 4);
    CheckedElement(input, 4) = Target;
    CheckedElement(input, 5) = size > 5 ? CheckedElement(data, 5) : size > 0 ? CheckedElement(data, 0) : 0;
    CheckedElement(input, 6) = 1;

    for (std::size_t index = 0; index < size; ++index)
    {
        if (index == 5)
        {
            continue;
        }
        if (index >= 8 && index < input.size())
        {
            CheckedElement(input, index) = CheckedElement(data, index);
        }
        else
        {
            const auto destination = 8 + index % (input.size() - 8);
            CheckedElement(input, destination) = OpenDot::Sol::CheckedInteger<std::uint8_t>(
                CheckedElement(input, destination) ^ CheckedElement(data, index));
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

    Require(data || size == 0, "Missing libFuzzer input storage.");
    const Input input = Normalize(std::span(data, size));
    Execute<Target>(input);
    return 0;
}
