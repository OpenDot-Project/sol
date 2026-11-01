// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include <exception>
#include <string_view>

namespace OpenDot::Sol::LlvmNative
{
void Initialize();
void ClearError() noexcept;
void SetError(std::string_view message) noexcept;
void SetError(const std::exception &error) noexcept;
const char *LastError() noexcept;
}
