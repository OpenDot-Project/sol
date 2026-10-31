// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; bounded unique top-level callback effect inference is implemented.
module examples.semantics.function_effect_inference

class ReadFailure(message: String): Exception(message)

func <T> apply(value: T, transform: (T) -> String): String = transform(value)

func <T> applyChecked(value: T, transform: (T) -> String throws ReadFailure): String
    throws ReadFailure = transform(value)

func noEffect(): String = "ok"

func mayFail(): String throws ReadFailure {
    throw ReadFailure("unavailable")
}

func callbacks(): () throws ReadFailure {
    val clean: (String) -> String = { value: String -> value }
    val wide: (String) -> String throws ReadFailure = { value: String -> mayFail() }
    val first = apply("input", clean)
    val second = applyChecked("input", wide)
}

func <E: Throwable> effectOnlyTypeParameter(callback: () -> String throws E): () throws E {
    callback()
}

// Type parameters in function argument/result positions can be structurally matched. A generic
// parameter that occurs only in a top-level callback effect set can be inferred when bounded
// unordered matching proves one complete assignment. Nested function-type sets and ambiguous
// mappings remain deferred. Effects are not sorted into positional pairs to force a match.
//
// Checked effect widening is explicit in the function type. A callback with a wider checked budget
// cannot be passed to the no-throws apply method. The candidate cannot erase ReadFailure to make
// the assignment appear safe.
