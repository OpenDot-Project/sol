// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; all generic vararg inference and lowering are pending.
module examples.semantics.vararg_inference

func <T> collect(prefix: String, vararg values: T, suffix: String): []T {
    return values
}

func <T> collectNamed(vararg values: T): []T {
    return values
}

func inferredMiddle(): []String {
    return collect("start", "alpha", "beta", suffix = "end")
}

func inferredFromSpread(): []String {
    val source: []String = ["first", "second"]
    return collectNamed(*source)
}

func inferredFromNamedArray(): []String {
    val source: []String = ["first", "second"]
    return collectNamed(values = source)
}

func emptyNeedsContext(): []String {
    return collectNamed<String>()
}

func emptyWithoutContext(): () {
    // Expected diagnostic: no element supplies a bound for T.
    val missing = collectNamed()
}

func defaultsDoNotInfer(): () {
    // Empty expansion is not an element. A declaration default is not evaluated during inference.
    val missing = collect("start", suffix = "end")
}

func wrongNamedArray(): () {
    val values: []std.core.Object = []
    // Expected diagnostic: the named array must have the invariant element type selected for T.
    val invalid: []String = collectNamed(values = values)
}

// Mapping associates source positions with formal positions before inference. The middle vararg
// consumes only arguments assigned to its expansion slot; a later named suffix remains a separate
// formal and is not mistaken for another T.
//
// Spread arrays contribute their element type. Named vararg arrays contribute the whole array
// contract and are validated invariantly. Empty expansion contributes no evidence. The compiler
// cannot derive T from the declared return type `[]T` without an expected type or explicit argument.
