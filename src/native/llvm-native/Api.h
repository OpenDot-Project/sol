// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
// NOLINTNEXTLINE(CppUnusedIncludeDirective): Direct contract for the fixed-width C ABI types.
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
// Independent of the LLVM release: changes when the immediate-operation wire contract changes.
SOL_LLVM_API std::uint64_t SolLlvmProtocol() noexcept;
SOL_LLVM_API bool SolLlvmApply(void *session, int operation, std::int64_t type,
                               const std::int64_t *operands, std::uint64_t count, std::int64_t data, const char *text,
                               std::int64_t *result) noexcept;
SOL_LLVM_API bool SolLlvmString(void *session, const unsigned char *bytes, std::uint64_t size,
                                std::int64_t *result) noexcept;
SOL_LLVM_API bool SolLlvmVerify(void *session) noexcept;
SOL_LLVM_API bool SolLlvmData(void *session, const unsigned char *bytes, std::uint64_t size,
                              const char *name, std::int64_t *result) noexcept;
// 0: LLVM printer inspection, 1: binary bitcode, 2: optimized COFF.
SOL_LLVM_API bool SolLlvmOutput(void *session, int kind, void **bytes, std::uint64_t *size) noexcept;
SOL_LLVM_API void SolLlvmFree(void *bytes) noexcept;
SOL_LLVM_API bool SolLldLink(const char *const *arguments, std::uint64_t count, int *exitCode,
                             void **diagnostics, std::uint64_t *size) noexcept;

}
