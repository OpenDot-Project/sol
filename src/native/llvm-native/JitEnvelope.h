// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include <cstdint>
#include <span>

namespace OpenDot::Sol::LlvmNative
{
/// Returns a borrowed, verified container payload. This does not verify LLVM IR.
std::span<const unsigned char> ReadJitEnvelope(std::span<const unsigned char> bytes, std::uint32_t flags);
}
