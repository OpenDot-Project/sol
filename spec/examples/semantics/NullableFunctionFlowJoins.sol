// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: parser-valid source-flow acceptance and rejection cases; nullable closure lowering is not implied.
module examples.semantics.nullable_function_flow_joins

open class Throwable {}
open class Exception: Throwable {}
open class RuntimeException: Exception {}
open class IOException: Exception {}

func invokeAfterProof(callback: (() -> int)?): int {
    if callback == null { return 0 }
    callback()
}
// expect: Source: Accepted; FIR: Ready for the exact code-pointer ABI after the guard.

func invokeAfterOptionalLoop(enabled: bool): int {
    var callback: (() -> int)? = null
    for enabled {
        callback = func() { 41 }
        break
    }
    callback()
}
// expect: Source: Rejected(SOL2243); a loop can complete without executing its body.

func invokeAfterCaughtNullPath(callback: (() -> int)?, failure: IOException): int {
    try {
        if callback == null { throw failure }
    } catch (error: IOException) {}
    callback()
}
// expect: Source: Rejected(SOL2243); the catch edge observes the nullable path.

func invokeAfterFinallyWrite(callback: (() -> int)?): int {
    var current = callback
    try {
        if current == null { return 0 }
    } finally {
        current = null
    }
    current()
}
// expect: Source: Rejected(SOL2243); finally writes invalidate earlier facts on normal exit.
