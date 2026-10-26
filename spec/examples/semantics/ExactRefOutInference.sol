// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; candidate-local exact ref/out inference and flow are pending.
module examples.semantics.exact_ref_out_inference

func <T> replace(ref target: T, source: T): T {
    target = source
    return target
}

func <T> readInto(out result: T, prototype: T): T {
    result = prototype
    return result
}

func <T> copy(left: T, right: T): T = left

func updateCounter(): int {
    var count: int = 4
    val next = replace(ref count, 9)
    return next
}

func inferOutType(): String {
    val prototype: String = "ready"
    readInto(out var result, prototype)
    return result
}

func mismatchMustReject(): () {
    var small: int = 1
    var wide: long = 2
    // Expected diagnostic: ref inputs require one exact T identity.
    replace(ref small, wide)
}

func mixedInputAndExact(): () {
    var id: int = 1
    // Expected diagnostic: an ordinary String lower bound cannot satisfy exact ref T = int.
    val mismatch: int = replace(ref id, "text")
}

func explicitTypeWithOut(): String {
    readInto<String>(out var value, "initial")
    return value
}

// The ref location is not read to infer a widened common value type. It supplies an exact storage
// identity and must be writable/definitely assigned before the call. The ordinary source argument
// contributes a value bound and is checked after T is solved.
//
// `out var result` cannot choose a candidate by itself. The prototype or an explicit type argument
// determines T, after which the call creates and assigns the local on the normal continuation.
// Failure paths and exceptional definite assignment are handled by the separate flow analysis.
