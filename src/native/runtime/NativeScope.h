// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include "Gc.h"

namespace OpenDot::Sol::Runtime
{
/// Immutable/native byte operations may run while the collector uses already published roots.
class NativeScope final
{
public:
    explicit NativeScope(bool active) noexcept : _active(active)
    {
        if (_active)
        {
            sol_gc_enter_native();
        }
    }

    ~NativeScope()
    {
        if (_active)
        {
            sol_gc_leave_native();
        }
    }

    NativeScope(const NativeScope &) = delete;
    NativeScope &operator=(const NativeScope &) = delete;
    NativeScope(NativeScope &&) = delete;
    NativeScope &operator=(NativeScope &&) = delete;

private:
    bool _active;
};

/// Protects a newly allocated result before a native-safe copy or another allocating helper.
class TemporaryRoot final
{
public:
    explicit TemporaryRoot(Header *value) noexcept : _value(value), _frame{nullptr, &_value, 1, 0}
    {
        sol_gc_push_frame(&_frame);
    }

    ~TemporaryRoot()
    {
        sol_gc_pop_frame(&_frame);
    }

    TemporaryRoot(const TemporaryRoot &) = delete;
    TemporaryRoot &operator=(const TemporaryRoot &) = delete;
    TemporaryRoot(TemporaryRoot &&) = delete;
    TemporaryRoot &operator=(TemporaryRoot &&) = delete;

private:
    Header *_value;
    RootFrame _frame;
};
}
