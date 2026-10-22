// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include <cstdint>
#ifdef SOL_LLVM_NATIVE_BUILD
#define SOL_LLVM_API __declspec(dllexport)
#else
#define SOL_LLVM_API __declspec(dllimport)
#endif

// Opaque C ABI isolates the LLVM SDK's static CRT from the mixed-mode adapter's dynamic CRT.
extern "C"
{
SOL_LLVM_API void *SolLlvmCreate() noexcept;
SOL_LLVM_API void SolLlvmDestroy(void *session) noexcept;
SOL_LLVM_API const char *SolLlvmError() noexcept;
SOL_LLVM_API const char *SolLlvmVersion() noexcept;
SOL_LLVM_API bool SolLlvmApply(void *session, int operation, std::int64_t type,
                               const std::int64_t *operands, std::uint64_t count, std::int64_t data, const char *text,
                               std::int64_t *result) noexcept;
SOL_LLVM_API bool SolLlvmString(void *session, const unsigned char *bytes, std::uint64_t size,
                                std::int64_t *result) noexcept;
SOL_LLVM_API bool SolLlvmVerify(void *session) noexcept;
// 0: LLVM printer inspection, 1: binary bitcode, 2: optimized COFF.
SOL_LLVM_API bool SolLlvmOutput(void *session, int kind, void **bytes, std::uint64_t *size) noexcept;
SOL_LLVM_API void SolLlvmFree(void *bytes) noexcept;
SOL_LLVM_API bool SolLldLink(const char *const *arguments, std::uint64_t count, int *exitCode,
                             void **diagnostics, std::uint64_t *size) noexcept;

struct SolLlvmSymbol
{
    const char *Name;
    std::uint64_t Address;
};

SOL_LLVM_API bool SolLlvmExecute(const unsigned char *bitcode, std::uint64_t size,
                                 const SolLlvmSymbol *symbols, std::uint64_t symbolCount, int argc, wchar_t **argv,
                                 int *exitCode) noexcept;
}
