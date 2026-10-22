// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "GcInternal.h"
#include <charconv>
#include <cstdlib>
#include <limits>
#include <string_view>

namespace OpenDot::Sol::Runtime::Heap
{
std::uint64_t ReadHeapLimit()
{
    char *raw = nullptr;
    std::size_t size = 0;
    if (_dupenv_s(&raw, &size, "SOL_GC_HEAP_LIMIT") != 0)
    {
        sol_rt_fail(8);
    }
    const std::unique_ptr<char, decltype(&std::free)> owner(raw, std::free);
    if (!raw)
    {
        return (std::numeric_limits<std::uint64_t>::max)();
    }
    const std::string_view text(raw);
    std::uint64_t value = 0;
    const auto parsed = std::from_chars(text.data(), text.data() + text.size(), value);
    if (parsed.ec != std::errc{})
    {
        sol_rt_fail(8);
    }
    const std::string_view suffix(parsed.ptr, static_cast<std::size_t>(text.data() + text.size() - parsed.ptr));
    const std::uint64_t factor = suffix.empty()
                                     ? 1
                                     : suffix == "KiB"
                                     ? 1024
                                     : suffix == "MiB"
                                     ? 1024 * 1024
                                     : suffix == "GiB"
                                     ? 1024ULL * 1024 * 1024
                                     : 0;
    if (!factor || value > (std::numeric_limits<std::uint64_t>::max)() / factor || value * factor < 16 * 1024 * 1024)
    {
        sol_rt_fail(8);
    }
    return value * factor;
}
}
