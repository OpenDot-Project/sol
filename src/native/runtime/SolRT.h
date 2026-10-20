// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include <cstddef>
#include <cstdint>

#if defined(SOL_RT_STATIC)
#define SOL_RT_API
#else
#define SOL_RT_API __declspec(dllexport)
#endif

namespace OpenDot::Sol::Runtime
{
struct alignas(16) TypeDescriptor final
{
    std::uint64_t Size;
    std::uint64_t Alignment;
    std::uint64_t TypeId;
    std::uint32_t Kind;
    std::uint32_t LayoutVersion;
    const TypeDescriptor* Base;
    const void* ScanShape;
    const void* MethodTable;
    const void* GenericIdentity;
    const void* ExceptionMetadata;
    std::uint64_t Reserved = 0;
};
struct Header
{
    const TypeDescriptor* Type;
    std::uint32_t Flags;
    std::uint32_t Reserved;
};
struct String final
{
    Header Object;
    std::int32_t ByteLength;
    std::int32_t RuneCount;
    char* Data()
    {
        return reinterpret_cast<char*>(this) + sizeof(String);
    }
    const char* Data() const
    {
        return reinterpret_cast<const char*>(this) + sizeof(String);
    }
};
struct StringArray final
{
    Header Object;
    std::int32_t Length;
    std::uint32_t Stride;
    const TypeDescriptor* Element;
    String** Data()
    {
        return reinterpret_cast<String**>(reinterpret_cast<char*>(this) + sizeof(StringArray));
    }
    String* const* Data() const
    {
        return reinterpret_cast<String* const*>(reinterpret_cast<const char*>(this) + sizeof(StringArray));
    }
};
static_assert(sizeof(Header) == 16);
static_assert(sizeof(TypeDescriptor) == 80);
static_assert(sizeof(String) == 24);
static_assert(sizeof(StringArray) == 32);
} // namespace OpenDot::Sol::Runtime

extern "C"
{
    SOL_RT_API extern const OpenDot::Sol::Runtime::TypeDescriptor sol_rt_string_type;
    SOL_RT_API OpenDot::Sol::Runtime::String* sol_rt_literal(OpenDot::Sol::Runtime::String* value);
    SOL_RT_API OpenDot::Sol::Runtime::StringArray* sol_rt_arguments(int argc, wchar_t** argv);
    SOL_RT_API void sol_rt_shutdown();
    [[noreturn]] SOL_RT_API void sol_rt_fail(int code);
    SOL_RT_API void sol_rt_write_string(const OpenDot::Sol::Runtime::String* value, int channel);
    SOL_RT_API void sol_rt_write_integer(std::uint64_t value, int kind, int channel);
    SOL_RT_API void sol_rt_write_float(double value, int width, int channel);
    SOL_RT_API void sol_rt_newline(int channel);
    SOL_RT_API int sol_rt_array_length(const OpenDot::Sol::Runtime::StringArray* array);
    SOL_RT_API OpenDot::Sol::Runtime::String* sol_rt_array_get(const OpenDot::Sol::Runtime::StringArray* array,
                                                               int index);
}
