// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include "../llvm-native/JitApi.h"
#include "../runtime/Core.h"
#include <vector>
#include <windows.h>

namespace OpenDot::Sol::OrcBootstrap
{
struct RuntimeFunctions final
{
    decltype(&sol_gc_attach) Attach;
    decltype(&sol_gc_enter_native) EnterNative;
    decltype(&sol_gc_leave_native) LeaveNative;
    decltype(&sol_gc_push_frame) PushFrame;
    decltype(&sol_gc_pop_frame) PopFrame;
    decltype(&sol_rt_string_create) StringCreate;
    decltype(&sol_rt_exception_create) ExceptionCreate;
    decltype(&sol_rt_throw) Throw;
    decltype(&sol_rt_fail) Fail;
    const Runtime::TypeDescriptor *RuntimeExceptionType;
};

extern RuntimeFunctions RuntimeApi;
std::vector<SolJitSymbol> Bindings(HMODULE runtime);
}
