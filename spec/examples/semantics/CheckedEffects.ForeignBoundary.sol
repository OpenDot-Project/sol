// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative foreign-boundary fixture. Native exceptions become explicit Sol results at the bridge.
module spec.effects.foreignboundary

class NativeFailure: Exception {}

extern "c" {
    func native_read(handle: ulong): int
}

func readNative(handle: ulong): int throws NativeFailure {
    val status = native_read(handle)
    if status < 0 {
        throw NativeFailure()
    }
    status
}

func nativeReadCaller(handle: ulong): int throws NativeFailure {
    readNative(handle)
}

func nativeReadMissingBudget(handle: ulong): int {
    // Expected: the managed Sol adapter's own declared effect is checked normally.
    readNative(handle)
}

func callbackToNative(handle: ulong, callback: () -> int throws NativeFailure): int {
    // A C ABI callback cannot unwind a Sol exception into native frames. The bridge catches it
    // and records a status carrier, which this wrapper converts to a declared Sol failure.
    val result = callback()
    native_read(handle) + result
}

func callbackAdapterBudget(handle: ulong, callback: () -> int throws NativeFailure): int
    throws NativeFailure {
    callbackToNative(handle, callback)
}

func foreignExceptionIsNotAThrowable(value: Object): ! {
    // An arbitrary foreign exception object is not directly thrown as Sol Throwable.
    throw value
}

func bridgeTranslationBoundary(status: int): int throws NativeFailure {
    if status < 0 {
        throw NativeFailure()
    }
    status
}

func bridgeStatusCanBeHandled(status: int): int {
    try {
        bridgeTranslationBoundary(status)
    } catch (failure: NativeFailure) {
        0
    }
}

// The extern declaration carries no foreign exception contract. The OpenDot::Sol C++/CLI
// adapter catches foreign exceptions and returns a status/diagnostic carrier. The Sol wrapper
// has an ordinary checked-effect contract. Object references and exception objects do not
// cross the native boundary as raw pointers.
