// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: native failures enter and leave Sol through explicit carriers.
module spec.cleanup.nativeboundary

class NativeFailure: Exception {}
class CloseFailure: Exception {}

func record(value: int): () {}

// A native adapter translates a status into a declared Sol Throwable before returning.
func readNative(): int throws NativeFailure { 1 }

// Ordinary Sol cleanup runs when the translated error exits its lexical scope.
func cleanupAfterNativeFailure(): int throws NativeFailure {
    defer { record(1) }
    readNative()
}

// A foreign exception is not allowed to unwind directly through the Sol cleanup frame.
func noForeignUnwind(): int throws NativeFailure {
    defer { record(2) }
    readNative()
}

// The adapter's Sol exception is primary if cleanup also fails.
func nativePrimaryCleanupFailure(): int throws NativeFailure, CloseFailure {
    defer { closeNativeResource() }
    readNative()
}

func closeNativeResource(): () throws CloseFailure { throw CloseFailure() }

// A native callback catches Sol exceptions before returning to its C caller.
func invokeCallback(callback: () -> () throws NativeFailure): int {
    // The bridge maps a caught Sol Throwable to its explicit C status carrier.
    callback()
    0
}

func callbackWithCleanup(): () throws NativeFailure {
    defer { record(10) }
    throw NativeFailure()
}

// Callback cleanup completes before the boundary translates the final primary failure.
func callbackBoundaryTranslation(): int {
    invokeCallback(callbackWithCleanup)
}

// If callback cleanup also fails, the callback's primary is retained in the carrier.
func callbackSuppressedFailure(): int {
    invokeCallback(callbackWithCleanupAndCloseFailure)
}

func callbackWithCleanupAndCloseFailure(): () throws NativeFailure {
    defer { closeNativeResource() }
    throw NativeFailure()
}

// The bridge must retain the callback context until all in-flight invocations finish.
class CallbackContext {
    var active: bool = true
    constructor() {}
}

func registerCallback(context: CallbackContext): () {}
func unregisterCallback(context: CallbackContext): () {}

func callbackLifetime(): () {
    val context = CallbackContext()
    defer { unregisterCallback(context) }
    registerCallback(context)
}

// Cleanup unregisters only after registration completed successfully.
func registrationMayFail(): () throws NativeFailure {}

func callbackRegistrationPrefix(): () throws NativeFailure {
    val context = CallbackContext()
    registerCallback(context)
    defer { unregisterCallback(context) }
    registrationMayFail()
}

// If registration throws before defer is reached, there is no active unregister action.
func noUnregisterBeforeRegistration(): () throws NativeFailure {
    val context = CallbackContext()
    registrationMayFail()
    defer { unregisterCallback(context) }
}

// A callback whose registration succeeded is unregistered on return and exception exits.
func unregisterOnBothExits(fail: bool): () throws NativeFailure {
    val context = CallbackContext()
    registerCallback(context)
    defer { unregisterCallback(context) }
    if fail { throw NativeFailure() }
}

// An exception thrown by foreign code is converted at the boundary, then follows ordinary catch.
func catchTranslatedNativeFailure(): () {
    try {
        readNative()
    } catch (error: NativeFailure) {
        record(20)
    }
}

// Cleanup cannot make a foreign exception safe by changing its type name.
func explicitStatusIsRequired(): int throws NativeFailure {
    defer { record(21) }
    readNative()
}

// Native buffers are released through lexical cleanup after the wrapper is acquired.
class NativeBuffer {
    constructor() {}
    func close(): () throws CloseFailure {}
}

func acquireBuffer(): NativeBuffer throws NativeFailure { NativeBuffer() }
func consumeBuffer(buffer: NativeBuffer): int throws NativeFailure { 1 }

func releaseBufferAfterUse(): int throws NativeFailure, CloseFailure {
    val buffer = acquireBuffer()
    defer { buffer.close() }
    consumeBuffer(buffer)
}

// A failed acquisition does not register a release for an object that was never returned.
func acquireFailureHasNoBufferCleanup(): int throws NativeFailure, CloseFailure {
    val buffer = acquireBuffer()
    defer { buffer.close() }
    consumeBuffer(buffer)
}

// If use fails, close runs before the primary Sol exception crosses the function boundary.
func useFailureStillCloses(): int throws NativeFailure, CloseFailure {
    val buffer = acquireBuffer()
    defer { buffer.close() }
    failUse()
}

func failUse(): int throws NativeFailure { throw NativeFailure() }

// The primary Sol failure survives a close failure and retains its identity.
func useAndCloseBothFail(): int throws NativeFailure, CloseFailure {
    val buffer = acquireBuffer()
    defer { buffer.close() }
    failUse()
}

// Foreign status payload is copied/validated before it becomes a Sol exception message.
func boundedFailureMessage(): int throws NativeFailure {
    readNative()
}

// Raw pointers do not replace the owner/root retained by a deferred resource wrapper.
func pointerLifetimeWrapper(): int throws NativeFailure, CloseFailure {
    val buffer = acquireBuffer()
    defer { buffer.close() }
    consumeBuffer(buffer)
}

// Cancellation and process abort behavior require separate contracts; this fixture covers ordinary
// synchronous return and checked exception edges only.
func ordinaryBoundaryExit(): () {
    defer { record(30) }
}

// A C++ exception, CLR exception, Ruby exception, and Sol Throwable are distinct identities.
func exceptionDomainsAreDistinct(): () throws NativeFailure {
    readNative()
}

// A C++/CLI bridge must translate before control returns to this source-level call site.
func cppCliCarrierBoundary(): int throws NativeFailure {
    readNative()
}

// The process entry adapter is the final unhandled Sol exception boundary.
object Main {
    func main(args: []String): () throws NativeFailure {
        readNative()
    }
}

// Specifications for external bridges must name their status layout and message bounds.
func statusLayoutContract(): () {
    defer { record(40) }
}

// A callback's checked effect cannot disappear because an unmanaged caller invokes it later.
func callbackEffectMetadata(callback: () -> () throws NativeFailure): () throws NativeFailure {
    callback()
}

// A callback with no checked budget cannot be passed to an adapter that may invoke it unchecked.
func callbackBudgetMismatch(callback: () -> () throws NativeFailure): () {
    invokeCallback(callback)
}

// Cleanup emitted for a native call must retain its landing-pad and root metadata.
func nativeLandingPadContract(): int throws NativeFailure {
    defer { record(50) }
    readNative()
}

// Source-only acceptance of a defer does not demonstrate native unwind support.
func notAResultForNativeCodegen(): int throws NativeFailure {
    defer { record(51) }
    readNative()
}

// Specification note: raw foreign unwinding through Sol frames is outside the contract.
// Specification note: every bridge maps foreign failure to a declared Throwable or status carrier.
// Specification note: callback cleanup finishes before Sol failure translation to foreign code.
// Specification note: native resource release actions are registered only after acquisition.
// Specification note: LLVM landing pads and GC roots must preserve the source cleanup protocol.
