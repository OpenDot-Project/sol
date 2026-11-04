// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#pragma warning(push)
#pragma warning(disable: 4244 4702)
#include <llvm/ExecutionEngine/Orc/ObjectLinkingLayer.h>
#pragma warning(pop)
#include <atomic>
// NOLINTNEXTLINE(CppUnusedIncludeDirective): Direct contract for the fixed-width lifetime counters.
#include <cstdint>
#include <memory>

namespace OpenDot::Sol::LlvmNative
{
struct JitCodeLifetime final
{
    std::atomic<std::uint64_t> Published{0};
    std::atomic<std::uint64_t> UnwindTables{0};
};

/// COFF image-relative fixups and native unwind registration, before symbol emission completes.
std::shared_ptr<llvm::orc::LinkGraphLinkingLayer::Plugin> CreateWindowsUnwindPlugin(
    std::shared_ptr<JitCodeLifetime> lifetime);
}
