// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include "Api.h"

extern "C"
{
struct SolJitSymbol final
{
    const char *Name;
    std::uint64_t Address;
    // Low nibble: result type. Next nibble: argument count. Remaining nibbles: argument types.
    // 0:void, 1:i1, 2:i32, 3:i64, 4:pointer, 5:float, 6:double. UINT64_MAX denotes data.
    std::uint64_t Prototype;
};

struct SolJitOptions final
{
    std::uint64_t RuntimeContract;
    std::uint64_t FailureThunk;
    void (*EnterNative)();
    void (*LeaveNative)();
    std::uint32_t Flags;
    std::uint32_t Size;
};

struct SolJitStats final
{
    std::uint64_t CompiledFunctions;
    std::uint64_t CompileCallbacks;
    std::uint64_t UnwindTables;
    std::uint64_t Failures;
};

SOL_LLVM_API std::uint64_t SolJitProtocol() noexcept;
// The caller is NativeSafe. Success retains the engine and code for the process lifetime.
SOL_LLVM_API bool SolJitOpen(const unsigned char *envelope, std::uint64_t size,
                             const SolJitSymbol *symbols, std::uint64_t symbolCount, const SolJitOptions *options,
                             std::uint64_t *engine, std::uint64_t *entry) noexcept;
SOL_LLVM_API bool SolJitLookup(std::uint64_t engine, const char *name, std::uint64_t *address) noexcept;
SOL_LLVM_API bool SolJitGetStats(std::uint64_t engine, SolJitStats *stats) noexcept;
SOL_LLVM_API bool SolJitCopyError(std::uint64_t engine, char *buffer, std::uint64_t capacity) noexcept;
}

static_assert(sizeof(SolJitSymbol) == 24 && sizeof(SolJitOptions) == 40 && sizeof(SolJitStats) == 32);
