// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include <string>
#include <vector>
namespace OpenDot::Sol::LlvmBridge
{
std::string NativeVersion();
std::vector<unsigned char> CompileNative(const char* ir, std::size_t size);
} // namespace OpenDot::Sol::LlvmBridge
