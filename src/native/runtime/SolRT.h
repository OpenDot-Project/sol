// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include <cstdint>
#include <span>

#if defined(SOL_RT_STATIC)
#define SOL_RT_API
#elif defined(SOL_RT_BUILD)
#define SOL_RT_API __declspec(dllexport)
#else
#define SOL_RT_API __declspec(dllimport)
#endif

namespace OpenDot::Sol::Runtime
{
/// Provisional descriptor prefix; all generated code must match this runtime build.
struct alignas(16) TypeDescriptor final
{
    std::uint64_t Size = 0;
    std::uint64_t Alignment = 0;
    std::uint64_t TypeId = 0;
    std::uint32_t Kind = 0;
    std::uint32_t LayoutVersion = 0;
    const TypeDescriptor *Base = nullptr;
    const void *ScanShape = nullptr;
    const void *MethodTable = nullptr;
    const void *GenericIdentity = nullptr;
    const void *ExceptionMetadata = nullptr;
    std::uint64_t Reserved = 0;
};

/// Common prefix; bit zero flags immutable process-lifetime storage outside the collectible heap.
struct Header
{
    const TypeDescriptor *Type;
    std::uint32_t Flags;
    std::uint32_t Reserved;
};

struct String final
{
    Header Object;
    std::int32_t ByteLength;
    std::int32_t RuneCount;

    char *Data() noexcept
    {
        void *payload = std::span{this, 1}.subspan<1>().data();
        return static_cast<char *>(payload);
    }

    const char *Data() const noexcept
    {
        const void *payload = std::span{this, 1}.subspan<1>().data();
        return static_cast<const char *>(payload);
    }
};

struct StringArray final
{
    Header Object;
    std::int32_t Length;
    std::uint32_t Stride;
    const TypeDescriptor *Element;

    String **Data() noexcept
    {
        void *payload = std::span{this, 1}.subspan<1>().data();
        return static_cast<String **>(payload);
    }

    String *const*Data() const noexcept
    {
        const void *payload = std::span{this, 1}.subspan<1>().data();
        return static_cast<String *const *>(payload);
    }
};

static_assert(sizeof(Header) == 16);
static_assert(sizeof(TypeDescriptor) == 80);
static_assert(sizeof(String) == 24);
static_assert(sizeof(StringArray) == 32);
} // namespace OpenDot::Sol::Runtime

extern "C"
{
SOL_RT_API void sol_rt_check_abi(std::uint64_t contract);
SOL_RT_API extern const OpenDot::Sol::Runtime::TypeDescriptor sol_rt_string_type;
SOL_RT_API extern const OpenDot::Sol::Runtime::TypeDescriptor sol_rt_object_type;
SOL_RT_API extern const OpenDot::Sol::Runtime::TypeDescriptor sol_rt_string_array_type;
SOL_RT_API OpenDot::Sol::Runtime::String *sol_rt_literal(OpenDot::Sol::Runtime::String *value);
SOL_RT_API OpenDot::Sol::Runtime::StringArray *sol_rt_arguments(int argc, const wchar_t *const *argv);
SOL_RT_API void sol_rt_shutdown();
[[noreturn]] SOL_RT_API void sol_rt_fail(int code);
SOL_RT_API void sol_rt_write_string(const OpenDot::Sol::Runtime::String *value, int channel);
SOL_RT_API void sol_rt_write_integer(std::uint64_t value, int kind, int channel);
SOL_RT_API void sol_rt_write_float(double value, int width, int channel);
SOL_RT_API void sol_rt_newline(int channel);
SOL_RT_API int sol_rt_array_length(const OpenDot::Sol::Runtime::StringArray *array);
SOL_RT_API OpenDot::Sol::Runtime::String *sol_rt_array_get(const OpenDot::Sol::Runtime::StringArray *array,
                                                           int index);
}
