// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "SolRT.h"
#include "Gc.h"
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

namespace OpenDot::Sol::Runtime
{
namespace
{
RuntimeHandle *Arguments = nullptr;
std::mutex OutputLock;

class NativeIo final
{
public:
    NativeIo()
    {
        sol_gc_enter_native();
    }

    ~NativeIo()
    {
        sol_gc_leave_native();
    }
};

const std::uint32_t ReferenceOffset = 0;
const ScanShape ReferenceElements{&ReferenceOffset, 1};

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
SOL_RT_API void sol_rt_check_abi(std::uint64_t contract)
{
    if (contract != 0x534F4C0000000001ULL)
    {
        sol_rt_fail(10);
    }
}

SOL_RT_API const TypeDescriptor sol_rt_object_type{16, 16, 0, 0, 0, nullptr, nullptr, nullptr, nullptr, nullptr};
SOL_RT_API const TypeDescriptor sol_rt_string_type{24, 16, 1, 1, 0, &sol_rt_object_type, nullptr, nullptr, nullptr,
                                                   nullptr};
SOL_RT_API const TypeDescriptor sol_rt_string_array_type{32, 16, 2, 2, 0, &sol_rt_object_type, &ReferenceElements,
                                                         nullptr, nullptr, nullptr};

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
    std::atomic_ref(value->Object.Flags).fetch_or(1U, std::memory_order_release);
    return value;
}

SOL_RT_API StringArray *sol_rt_arguments(int argc, wchar_t **argv)
{
    sol_gc_attach();
    if (argc < 1 || !argv || Arguments)
    {
        sol_rt_fail(7);
    }
    const auto count = static_cast<std::size_t>(argc) - 1;
    if (count > (std::numeric_limits<std::size_t>::max() - 32) / sizeof(String *))
    {
        sol_rt_fail(6);
    }
    auto array = reinterpret_cast<StringArray *>(
        sol_gc_alloc(&sol_rt_string_array_type, 32 + count * sizeof(String *)));
    Arguments = sol_gc_create_handle(&array->Object, false);
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
        auto value = reinterpret_cast<String *>(
            sol_gc_alloc(&sol_rt_string_type, 24 + static_cast<std::size_t>(length)));
        value->ByteLength = length;
        value->RuneCount = -1;
        if (length != 0 && WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, argv[index], static_cast<int>(units),
                                               value->Data(), length, nullptr, nullptr) != length)
        {
            sol_rt_fail(7);
        }
        sol_gc_store_ref(reinterpret_cast<Header **>(&array->Data()[index - 1]), &value->Object);
    }
    return array;
}

SOL_RT_API void sol_rt_shutdown()
{
    sol_gc_release_handle(Arguments);
    Arguments = nullptr;
    ShutdownHeap();
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
                              : code == 8
                              ? "SolRT configuration error: invalid SOL_GC_HEAP_LIMIT\n"
                              : code == 9
                              ? "Unhandled std.core.ArgumentException\n"
                              : code == 10
                              ? "SolRT runtime contract mismatch\n"
                              : "SolRT bootstrap invariant failed\n";
    DWORD ignored = 0;
    WriteFile(GetStdHandle(STD_ERROR_HANDLE), message, static_cast<DWORD>(std::strlen(message)), &ignored, nullptr);
    // An invariant/OOM may be reported while holding a runtime lock. Static destruction would
    // reacquire that lock and deadlock; fail-stop paths deliberately skip language cleanup.
    std::_Exit(1);
}

SOL_RT_API void sol_rt_write_string(const String *value, int channel)
{
    if (!value || value->ByteLength < 0)
    {
        sol_rt_fail(7);
    }
    const NativeIo transition;
    const std::lock_guard lock(OutputLock);
    Write(value->Data(), value->ByteLength, channel);
}

SOL_RT_API void sol_rt_write_integer(std::uint64_t value, int kind, int channel)
{
    const NativeIo transition;
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
    const NativeIo transition;
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
    const NativeIo transition;
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