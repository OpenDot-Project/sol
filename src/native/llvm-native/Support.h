// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include <string>

namespace OpenDot::Sol::LlvmNative
{
void Initialize();
extern thread_local std::string Error;
}
