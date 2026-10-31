// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
module examples.anonymous_function_values

func apply(value: int, transform: (int) -> int): int = transform(value)

func demonstrate(): () {
    val blockBody = func(value: int): int { value + 1 }
    val expressionBody = func(value: int): int = value + 2
    val contextualResult: (int) -> int = func(value: int) { value + 3 }

    val result = apply(40, blockBody)
    val next = expressionBody(result)
    val finalValue = apply(next, contextualResult)
}

// expect: Source analysis resolves all three anonymous function signatures, block tails, and the
// fixed-arity indirect call. The first native FIR slice emits them only when they do not capture
// locals/receivers and require no ABI adapter. AOT and ORC must preserve the same typed code-pointer
// semantics.

func outWrite(): () {
    val write = func(out destination: int): () { destination = 42 }
    var value: int
    write(out value)
}

// expect: The anonymous function's out slot is writable and assigned on its normal exit; the
// indirect call initializes value only after the call's successful normal edge.

func capturedValue(seed: int): () -> int {
    val callback = func(): int { seed }
    callback
}

// expect: Local capture and return escape appear in independent inventories. Source analysis can
// represent the requirement, but the current native function-pointer ABI has no closure environment
// and FIR rejects this value.

func nestedCapture(seed: int): () -> () -> int {
    val outer = func(): () -> int {
        val inner = func(): int = seed
        inner
    }
    outer
}

// expect: Both anonymous-function identities are present. The inner environment requirement
// propagates through the outer function. Neither closure may be emitted as a plain code pointer.

func nestedNoncapturing(value: int): int {
    val outer = func(input: int): int {
        val increment = func(next: int): int = next + 1
        increment(input)
    }
    outer(value)
}

// expect: The inner function has no outer-local or receiver capture. FIR creates two independently
// verified code-pointer targets, and AOT/ORC execute the same value through the enclosing body.

func lambdaInsideAnonymous(value: int): int {
    val outer = func(input: int): int {
        val increment: (int) -> int = { next -> next + 1 }
        increment(input)
    }
    outer(value)
}

// expect: The lambda and anonymous function have separate source identities and synthetic targets.
// Both are noncapturing, so nesting them does not require an environment pointer.
