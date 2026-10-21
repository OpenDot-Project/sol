// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include <string>
#include <vector>

namespace OpenDot::Sol::LlvmBridge
{
/// Returns the loaded LLVM library version, independently of the SDK path spelling.
std::string NativeVersion();
/// Verifies, optimizes, verifies again, and emits an AMD64 COFF object using session-owned LLVM data.
std::vector<unsigned char> CompileNative(const char *ir, std::size_t size);
} // namespace OpenDot::Sol::LlvmBridge