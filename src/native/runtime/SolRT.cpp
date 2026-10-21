// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "SolRT.h"
#include <windows.h>
#include <charconv>
#include <atomic>
#include <bit>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <mutex>
#include <string_view>
#include <vector>

namespace OpenDot::Sol::Runtime
{
namespace
{
// Bootstrap lifetime: all argument storage stays owned until program shutdown. No GC is implemented here.
std::vector<void *> Arguments;
std::mutex OutputLock;
const TypeDescriptor ArrayType{32, 16, 2, 2, 0, nullptr, nullptr, nullptr, nullptr, nullptr};

void *Allocate(std::size_t size)
{
    void *data = _aligned_malloc(size, 16);
    if (!data)
    {
        sol_rt_fail(6);
    }
    std::memset(data, 0, size);
    try
    {
        Arguments.push_back(data);
    }
    catch (...)
    {
        _aligned_free(data);
        sol_rt_fail(6);
    }
    return data;
}

void Write(const char *bytes, std::size_t length, int channel)
{
    const auto handle = GetStdHandle(channel == 0 ? STD_OUTPUT_HANDLE : STD_ERROR_HANDLE);
    while (length != 0)
    {
        const auto chunk =
            static_cast<DWORD>((std::min)(length, static_cast<std::size_t>(std::numeric_limits<DWORD>::max())));
        DWORD written = 0;
        if (!WriteFile(handle, bytes, chunk, &written, nullptr) || written == 0)
        {
            sol_rt_fail(5);
        }
        bytes += written;
        length -= written;
    }
}
} // namespace
} // namespace OpenDot::Sol::Runtime

using namespace OpenDot::Sol::Runtime;

extern "C"
{
SOL_RT_API const TypeDescriptor sol_rt_string_type{24, 16, 1, 1, 0, nullptr, nullptr, nullptr, nullptr, nullptr};

SOL_RT_API String *sol_rt_literal(String *value)
{
    if (!value || value->ByteLength < 0)
    {
        sol_rt_fail(7);
    }
    const std::atomic_ref type(value->Object.Type);
    const TypeDescriptor *expected = nullptr;
    type.compare_exchange_strong(expected, &sol_rt_string_type, std::memory_order_release,
                                 std::memory_order_acquire);
    return value;
}

SOL_RT_API StringArray *sol_rt_arguments(int argc, wchar_t **argv)
{
    if (argc < 1 || !argv || !Arguments.empty())
    {
        sol_rt_fail(7);
    }
    const auto count = static_cast<std::size_t>(argc) - 1;
    if (count > (std::numeric_limits<std::size_t>::max() - 32) / sizeof(String *))
    {
        sol_rt_fail(6);
    }
    auto array = static_cast<StringArray *>(Allocate(32 + count * sizeof(String *)));
    array->Object.Type = &ArrayType;
    array->Length = argc - 1;
    array->Stride = sizeof(String *);
    array->Element = &sol_rt_string_type;
    for (int index = 1; index < argc; ++index)
    {
        const auto units = std::wcslen(argv[index]);
        if (units > static_cast<std::size_t>(std::numeric_limits<int>::max()))
        {
            sol_rt_fail(6);
        }
        const int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, argv[index], static_cast<int>(units),
                                               nullptr, 0, nullptr, nullptr);
        if (units != 0 && length == 0)
        {
            sol_rt_fail(7);
        }
        auto value = static_cast<String *>(Allocate(24 + static_cast<std::size_t>(length)));
        value->Object.Type = &sol_rt_string_type;
        value->ByteLength = length;
        value->RuneCount = -1;
        if (length != 0 && WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, argv[index], static_cast<int>(units),
                                               value->Data(), length, nullptr, nullptr) != length)
        {
            sol_rt_fail(7);
        }
        array->Data()[index - 1] = value;
    }
    return array;
}

SOL_RT_API void sol_rt_shutdown()
{
    for (void *allocation : Arguments)
    {
        _aligned_free(allocation);
    }
    Arguments.clear();
}

[[noreturn]] SOL_RT_API void sol_rt_fail(int code)
{
    const char *message = code == 1
                              ? "Unhandled std.core.ArithmeticException: integer overflow\n"
                              : code == 2
                              ? "Unhandled std.core.ArithmeticException: division by zero\n"
                              : code == 3
                              ? "Unhandled std.core.ArithmeticException: invalid shift count\n"
                              : code == 4
                              ? "Unhandled std.core.IndexOutOfBoundsException\n"
                              : code == 5
                              ? "Unhandled std.stdio.IOException: output failed\n"
                              : code == 6
                              ? "Unhandled std.core.OutOfMemoryError\n"
                              : "SolRT bootstrap invariant failed\n";
    DWORD ignored = 0;
    WriteFile(GetStdHandle(STD_ERROR_HANDLE), message, static_cast<DWORD>(std::strlen(message)), &ignored, nullptr);
    std::exit(1);
}

SOL_RT_API void sol_rt_write_string(const String *value, int channel)
{
    if (!value || value->ByteLength < 0)
    {
        sol_rt_fail(7);
    }
    const std::lock_guard lock(OutputLock);
    Write(value->Data(), value->ByteLength, channel);
}

SOL_RT_API void sol_rt_write_integer(std::uint64_t value, int kind, int channel)
{
    const std::lock_guard lock(OutputLock);
    if (kind == 2)
    {
        Write(value == 0 ? "false" : "true", value == 0 ? 5 : 4, channel);
        return;
    }
    char buffer[32]{};
    const auto result = kind == 0
                            ? std::to_chars(buffer, buffer + sizeof(buffer), std::bit_cast<std::int64_t>(value))
                            : std::to_chars(buffer, buffer + sizeof(buffer), value);
    if (result.ec != std::errc{})
    {
        sol_rt_fail(7);
    }
    Write(buffer, static_cast<std::size_t>(result.ptr - buffer), channel);
}

SOL_RT_API void sol_rt_write_float(double value, int width, int channel)
{
    const std::lock_guard lock(OutputLock);
    if (std::isnan(value))
    {
        Write("nan", 3, channel);
        return;
    }
    if (std::isinf(value))
    {
        Write(std::signbit(value) ? "-inf" : "inf", std::signbit(value) ? 4 : 3, channel);
        return;
    }
    char buffer[64]{};
    const auto result = width == 32
                            ? std::to_chars(buffer, buffer + sizeof(buffer), static_cast<float>(value))
                            : std::to_chars(buffer, buffer + sizeof(buffer), value);
    if (result.ec != std::errc{})
    {
        sol_rt_fail(7);
    }
    Write(buffer, static_cast<std::size_t>(result.ptr - buffer), channel);
}

SOL_RT_API void sol_rt_newline(int channel)
{
    const std::lock_guard lock(OutputLock);
    Write("\n", 1, channel);
}

SOL_RT_API int sol_rt_array_length(const StringArray *array)
{
    if (!array)
    {
        sol_rt_fail(7);
    }
    return array->Length;
}

SOL_RT_API String *sol_rt_array_get(const StringArray *array, int index)
{
    if (!array || index < 0 || index >= array->Length)
    {
        sol_rt_fail(4);
    }
    return array->Data()[index];
}
}