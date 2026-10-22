// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include "Gc.h"

namespace OpenDot::Sol::Runtime
{
struct Throwable final
{
    Header Object;
    String *Message;
    Throwable *Cause;
    StringArray *Suppressed;
};

/// A native EH carrier owns a strong runtime root across copies, catches, and rethrows.
class SOL_RT_API ExceptionCarrier final
{
public:
    explicit ExceptionCarrier(Throwable *value);
    ExceptionCarrier(const ExceptionCarrier &other) noexcept;
    ExceptionCarrier &operator=(const ExceptionCarrier &) = delete;
    ~ExceptionCarrier();
    Throwable *Value() const noexcept;

private:
    struct RootControl;
    RootControl *_root;
    Throwable *_value;
};

static_assert(sizeof(Throwable) == 40);
}

extern "C"
{
SOL_RT_API extern const OpenDot::Sol::Runtime::TypeDescriptor sol_rt_throwable_type;
SOL_RT_API extern const OpenDot::Sol::Runtime::TypeDescriptor sol_rt_exception_type;
SOL_RT_API extern const OpenDot::Sol::Runtime::TypeDescriptor sol_rt_runtime_exception_type;
SOL_RT_API OpenDot::Sol::Runtime::String *sol_rt_string_create(const char *bytes, int length);
SOL_RT_API int sol_rt_string_length(OpenDot::Sol::Runtime::String *value);
SOL_RT_API int sol_rt_string_compare(const OpenDot::Sol::Runtime::String *left,
                                     const OpenDot::Sol::Runtime::String *right);
SOL_RT_API OpenDot::Sol::Runtime::String *sol_rt_string_concat(OpenDot::Sol::Runtime::String *left,
                                                               OpenDot::Sol::Runtime::String *right);
SOL_RT_API OpenDot::Sol::Runtime::String *sol_rt_string_substring(OpenDot::Sol::Runtime::String *value, int start,
                                                                  int length);
SOL_RT_API int sol_rt_string_index_of(OpenDot::Sol::Runtime::String *value, const OpenDot::Sol::Runtime::String *needle,
                                      int start);
SOL_RT_API std::uint32_t sol_rt_string_rune_at(OpenDot::Sol::Runtime::String *value, int index);
SOL_RT_API int sol_rt_string_hash(const OpenDot::Sol::Runtime::String *value);
SOL_RT_API OpenDot::Sol::Runtime::StringArray *sol_rt_array_create(int length, std::uint32_t stride,
                                                                   const OpenDot::Sol::Runtime::TypeDescriptor *element,
                                                                   const OpenDot::Sol::Runtime::TypeDescriptor *
                                                                   arrayType);
SOL_RT_API OpenDot::Sol::Runtime::StringArray *sol_rt_array_new(int length, int elementKind);
SOL_RT_API void *sol_rt_array_address(OpenDot::Sol::Runtime::StringArray *array, int index, int expectedStride);
SOL_RT_API OpenDot::Sol::Runtime::Throwable *sol_rt_exception_create(const OpenDot::Sol::Runtime::TypeDescriptor *type,
                                                                     OpenDot::Sol::Runtime::String *message,
                                                                     OpenDot::Sol::Runtime::Throwable *cause);
SOL_RT_API bool sol_rt_is_instance(const OpenDot::Sol::Runtime::Header *value,
                                   const OpenDot::Sol::Runtime::TypeDescriptor *type);
SOL_RT_API void sol_rt_exception_add_suppressed(OpenDot::Sol::Runtime::Throwable *primary,
                                                OpenDot::Sol::Runtime::Throwable *secondary);
[[noreturn]] SOL_RT_API void sol_rt_throw(OpenDot::Sol::Runtime::Throwable *value) noexcept(false);
}
