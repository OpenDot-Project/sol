// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: the compiler-known @escaping callable-parameter contract is parsed, retained in AST/FIR
// source metadata, checked at declaration/call/override boundaries, and covered by body analysis.
// General user annotation schemas, targets, and compile-time constant evaluation remain planned.
module source.annotations.rules

annotation class Route(val path: String)
annotation class Retry(val attempts: int = 3)

@Route("/health")
@Retry(attempts = 2)
func health(): String = "ok"

@deprecated("Use health instead")
func oldHealth(): String = health()

@test
func healthContract(): () { assert(health() == "ok", "Unexpected health response") }

// Callable-parameter lifetime contract
// ====================================
//
// `@escaping` is a compiler-known annotation on a parameter group whose declared type is a
// function type, including a nullable function type. Put it before the parameter name:
// `func schedule(@escaping callback: () -> int)`. A grouped parameter declaration applies the
// annotation to every name in that group. Annotation arguments are not accepted. Using the
// annotation on a non-function parameter is an error.
//
// A by-value function-valued parameter without `@escaping` has a nonescaping contract. This also
// applies to nullable function values; ref/out and vararg parameters cannot carry this annotation.
// Its body may invoke the value and keep local aliases while the current invocation is active, but it must not return
// it, publish it in object/array/field storage, capture it in a callable that may escape, or forward
// it to an `@escaping` parameter. Source analysis must check that contract. An external declaration
// without a body states the same contract for its implementation; its implementation must honor it.
//
// `@escaping` allows a body to retain, return, or forward the function value. It does not require
// the body to do so. The caller treats the value as potentially escaping even when the current
// implementation happens to invoke it immediately. Passing a closure that captures caller-owned
// ref/out storage or a ref-like value to such a parameter is rejected. Passing that closure to a
// nonescaping parameter is permitted only when the selected declaration's contract is known.
//
// The annotation is declaration metadata, not part of the function type, overload identity, or
// calling convention. It does not add an environment representation, make closure conversion
// available, or authorize a backend to emit a closure that otherwise fails its ABI checks.

func invokeNow(callback: () -> int): int = callback()

func returnCallback(@escaping callback: () -> int): () -> int {
    return callback
}

func invalidReturnFromNonescaping(callback: () -> int): () -> int {
    return callback
    // expect: compiler diagnostic; a nonescaping parameter cannot be returned.
}

func localBorrowThroughNonescapingParameter(ref value: int): int {
    val callback = { -> value }
    invokeNow(callback)
}

func rejectBorrowThroughEscapingParameter(ref value: int): () {
    returnCallback({ -> value })
    // expect: SOL2262; the parameter contract allows the callback to outlive this activation.
}

// User annotations have typed constant arguments and declared target/repeatability rules.
// Primitive/bool/String/Rune/enum/type metadata and fixed const arrays are permitted.
// Arbitrary objects, closures, ref/out values and filesystem/network execution are not.
// An annotation schema does not run an arbitrary constructor at compile time.
// Compiler-known names remain snake_case: test, repr, link, allow, warn, deny, forbid,
// must_use, deprecated and escaping. File-target annotations precede the module declaration.
// Attribute spelling alone is not proof that schema/effect/constant validation exists.
