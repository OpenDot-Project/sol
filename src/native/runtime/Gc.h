// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include "SolRT.h"

namespace OpenDot::Sol::Runtime
{
/// Immutable scan program. Offsets are from the object or array element start.
struct ScanShape final
{
    const std::uint32_t *Offsets;
    std::uint32_t Count;
    std::uint32_t Reserved = 0;
};

/// Compiler-owned, zero-initialized root storage, live until exactly one pop.
struct RootFrame final
{
    RootFrame *Previous;
    Header **Slots;
    std::uint32_t Count;
    std::uint32_t Reserved;
};

struct RuntimeHandle;

struct GcStats final
{
    std::uint64_t AllocatedBytes;
    std::uint64_t LiveBytes;
    std::uint64_t HeapBytes;
    std::uint64_t ObjectCount;
    std::uint64_t CollectionCount;
    std::uint64_t Epoch;
};

/// Internal lifecycle; shutdown requires all other runtime threads to detach.
void ShutdownHeap();
bool IsInstance(const Header *object, const TypeDescriptor *type) noexcept;
static_assert(sizeof(RootFrame) == 24);
}

extern "C"
{
SOL_RT_API void sol_gc_attach();
SOL_RT_API void sol_gc_detach();
SOL_RT_API void sol_gc_push_frame(OpenDot::Sol::Runtime::RootFrame *frame);
SOL_RT_API void sol_gc_pop_frame(OpenDot::Sol::Runtime::RootFrame *frame);
SOL_RT_API void sol_gc_poll();
SOL_RT_API void sol_gc_enter_native();
SOL_RT_API void sol_gc_leave_native();
SOL_RT_API int sol_gc_enter_export();
SOL_RT_API void sol_gc_leave_export(int transition);
SOL_RT_API OpenDot::Sol::Runtime::Header *sol_gc_alloc(const OpenDot::Sol::Runtime::TypeDescriptor *type,
                                                       std::uint64_t totalBytes);
SOL_RT_API void sol_gc_store_ref(OpenDot::Sol::Runtime::Header **destination, OpenDot::Sol::Runtime::Header *value);
SOL_RT_API std::uint64_t sol_gc_request_collection();
SOL_RT_API void sol_gc_wait(std::uint64_t epoch);
SOL_RT_API void sol_gc_collect();
SOL_RT_API OpenDot::Sol::Runtime::GcStats sol_gc_stats();
SOL_RT_API OpenDot::Sol::Runtime::RuntimeHandle *sol_gc_create_handle(OpenDot::Sol::Runtime::Header *value, bool weak);
SOL_RT_API bool sol_gc_resolve_handle(OpenDot::Sol::Runtime::RuntimeHandle *handle,
                                      OpenDot::Sol::Runtime::Header **root);
SOL_RT_API void sol_gc_release_handle(OpenDot::Sol::Runtime::RuntimeHandle *handle);
}