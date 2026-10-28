// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: source typing, FIR verification and exact-ABI nullable function pointers are implemented.
// Nullable closures, ABI adapters and native !! failure lowering remain outside the current subset.
module source.nullable_function_values.rules

import std.stdio

// NULLABLE FUNCTION TYPES
// =======================
//
// `?` applies to the complete type expression immediately before it. Parenthesize a function type
// when the nullable marker applies to the callable value:
//
//     (() -> int)?                 nullable callable; result is non-null int
//     () -> int?                   non-null callable; result is nullable int
//     ((String) -> Object)?        nullable callable with the written parameter/result contract
//     ((() -> int)?) -> int        non-null callable taking a nullable callable
//     []((() -> int)?)             non-null array with nullable callable elements
//     ([](() -> int))?             nullable array whose elements are non-null callables
//
// These types have distinct resolved shapes. Nullability on a result or a parameter does not add
// nullability to the containing function. Nullability on an array element does not make the array
// itself nullable. A repeated nullable wrapper is normalized by the type system to one layer.
// Type aliases, generic substitutions, overload selection and function-value conversion preserve
// the same owner-resolved payload identity; source spelling alone is not a type identity.

func returnNullableCallback(): (() -> int)? = null
func callbackReturningNullable(): () -> int? = func() { null }
func acceptNullableCallback(callback: (() -> int)?): int {
    if callback == null { return 0 }
    callback()
}

// A function value and a null value can share a nullable function slot. The source function type
// must still be known from a declaration, annotation, generic solution or contextual function
// type. `null` by itself supplies no parameter list, result type, effects or passing modes.
func initializeCallbackSlots(): int {
    var callback: (() -> int)? = null
    if callback == null { callback = func() { 41 } }
    if callback != null { return callback() + 1 }
    0
}

func nullableCallbackArray(): int {
    val callbacks: []((() -> int)?) = [null, func() { 42 }]
    val first = callbacks[0]
    if first == null { return 0 }
    first()
}

func nullableLambdaContext(): int {
    val callback: (() -> int)? = { 42 }
    if callback == null { return 0 }
    callback()
}

func nullableLambdaArrayContext(): int {
    val callbacks: []((() -> int)?) = [{ 42 }, null]
    val first = callbacks[0]
    if first == null { return 0 }
    first()
}

// Body analysis accepts both lambda contexts above: contextual parameter/result analysis produces
// a non-null function value, then ordinary exact-signature nullable widening applies. The current
// FIR/native nullable-lift gate still rejects these lambda-origin values with SOL2400. This split
// is intentional and tested; successful source typing does not imply native representation.

// NULL VALUE AND CONVERSIONS
// ==========================
//
// `null` has a nullable bottom type until context supplies a nullable target. It can initialize a
// local, return from a function, fill an array element, or be passed to a parameter only when that
// target is a nullable function type with a fully known signature. A null-only bottom type cannot
// become a local declaration, function parameter/result, array element type, or callable signature.
// This prevents an untyped zero pointer from being mistaken for a function pointer of arbitrary
// signature.
//
// A non-null function value can be widened to a nullable function value when the exact resolved
// function signature and ABI are identical. This operation changes only the possibility of the
// null state; it does not adapt parameters, return values, receiver binding, effects or captures.
// A function variance conversion that needs a thunk or wrapper remains a distinct conversion and
// cannot be emitted by reinterpreting the original code address.

func passNullToCallback(callback: (() -> int)?): int {
    if callback == null { return 0 }
    callback()
}

func passValueToNullable(): int {
    val callback: (() -> int)? = func() { 42 }
    if callback != null { return callback() }
    0
}

func passNullArrayElement(): int {
    val callbacks: []((() -> int)?) = [null, func() { 42 }]
    val absent = callbacks[0]
    if absent == null { return 0 }
    absent()
}

// `(() -> int)?` is not interchangeable with `() -> int?`. A nullable result remains an ordinary
// function result and is checked after the call; a nullable callable must be checked before the
// indirect call. Assignment and generic inference do not move the nullable layer between these
// positions.
func nullableResultIsSeparate(callback: () -> int?): int {
    val result = callback()
    if result == null { return 0 }
    result
}

// CALLING A NULLABLE VALUE
// ========================
//
// An indirect call requires a function type. A nullable function value is not callable while its
// expression has a nullable type. It becomes callable after a source proof establishes the
// non-null payload type, or after an explicit non-null assertion. The assertion evaluates the
// operand exactly once. On a non-null path it produces the payload type; on a null path it raises
// the language's null-assertion runtime error and does not enter the target function.
//
// The native pointer subset accepts flow-proven invocation. It does not currently lower `!!` for
// nullable function values, so a source-valid assertion can be rejected at FIR admission. Source
// checking and native lowering report separate outcomes; native rejection does not make the source
// expression semantically invalid.

func branchBeforeInvocation(callback: (() -> int)?): int {
    if callback == null { return 0 }
    callback()
}

func assertBeforeInvocation(callback: (() -> int)?): int = callback!!()
// expect: Source: Bound; FIR: Rejected(SOL2400) until native null-assertion lowering is qualified.

// Equality and inequality against null may be written in either operand order for a nullable value
// and for a statically non-null reference value. The latter comparison is permitted and has a
// constant semantic result; it does not make a non-null value nullable. Only direct null predicates
// establish presence for a nullable local. Comparing two function values for equality is governed
// by the language's function-value equality rule; it is not approximated by checking that both are
// non-null or by comparing arbitrary ABI pointers in a context that needs an adapter.

func eitherNullOrder(callback: (() -> int)?): int {
    if null == callback { return 0 }
    if null != callback { return callback() }
    0
}

func reversedGuard(callback: (() -> int)?): int {
    if null != callback { return callback() }
    0
}

// FLOW REFINEMENT
// ===============
//
// Null refinement applies to the exact local storage identity. A successful `callback != null`
// test refines the true edge to the function payload; `callback == null` refines the false edge.
// The reverse operand order has the same rule. An early return or throw removes that edge from the
// following join, so a surviving continuation can be non-null without a second test.
//
// Refinement is an expression view only. It does not rewrite the declaration's type, choose a
// different overload, change a generic argument, alter a function's checked effects, or replace
// the slot's pointer representation. At joins, a nullable fact survives only if every reachable
// predecessor proves the same payload type for the same stable local.

func returnOnNull(callback: (() -> int)?): int {
    if callback == null { return 0 }
    callback()
}

func bothBranchesAssign(flag: bool): (() -> int)? {
    var callback: (() -> int)? = null
    if flag {
        callback = func() { 41 }
    } else {
        callback = func() { 42 }
    }
    callback
}

func oneBranchAssign(flag: bool): int {
    var callback: (() -> int)? = null
    if flag { callback = func() { 42 } }
    if callback != null { return callback() }
    0
}

// The right operand of `&&` is evaluated only when the left operand is true. A non-null check in
// that left operand is available to the right operand. For `||`, the right operand runs only when
// the left operand is false, so an equality-to-null test can establish the non-null payload there.
// Those facts do not escape the Boolean expression unless subsequent control-flow paths prove the
// same fact at their join.

func conjunction(callback: (() -> int)?): bool =
    callback != null && callback() == 42

func disjunction(callback: (() -> int)?): bool =
    callback == null || callback() == 42

func negatedNullCheck(callback: (() -> int)?): int {
    if !(callback == null) { return callback() }
    0
}

func branchJoinDoesNotKeepOneSidedFact(flag: bool, callback: (() -> int)?): int {
    if flag {
        if callback == null { return 0 }
    }
    // The false `flag` edge reaches this call without proving presence.
    callback()
}
// expect: Source: Rejected(SOL2243); the join includes an incoming path without a presence proof.

func nullableCallAfterOptionalLoop(enabled: bool): int {
    var callback: (() -> int)? = null
    for enabled { callback = func() { 42 }; break }
    // The loop can execute zero times, so its body cannot prove presence afterward.
    callback()
}
// expect: Source: Rejected(SOL2243); the loop join retains the nullable incoming edge.

func nullableCallAfterCatch(callback: (() -> int)?, failure: IOException): int {
    try { if callback == null { throw failure } }
    catch (error: IOException) {}
    // The catch path is reached while callback is null; the normal path is non-null.
    callback()
}
// expect: Source: Rejected(SOL2243); the try/catch join cannot retain a one-sided proof.

func nullableCallAfterFinally(callback: (() -> int)?): int {
    var current = callback
    try { if current == null { return 0 } }
    finally { current = null }
    // Finally runs on the normal path and replaces the checked value.
    current()
}
// expect: Source: Rejected(SOL2243); the finally assignment invalidates the earlier proof.

// A write replaces the old value and invalidates the previous refinement before the next read.
// The same is true after ref/out access, assignment through an alias, or another language-defined
// mutation path. An out call establishes definite assignment only on a normal-returning edge; it
// does not establish a non-null value unless its declared type or a subsequent check proves one.

func clear(ref callback: (() -> int)?): () { callback = null }
func write(out callback: (() -> int)?): () { callback = func() { 42 } }

func refInvalidates(callback: (() -> int)?): int {
    if callback != null {
        clear(ref callback)
        // The callback might now be null.
        return callback()
    }
    0
}
// expect: Source: Rejected(SOL2243); the ref call invalidates the local refinement.

func outRechecks(callback: (() -> int)?): int {
    var current = callback
    write(out current)
    if current != null { return current() }
    0
}

// Function invocation may mutate captured mutable cells through an environment closure. A local
// mutable value that is captured or passed through a writing ref/out path is not treated as stable
// across that operation. Noncapturing function pointers have no closure cell, but their declared
// ref/out arguments still invalidate the caller's written storage.

// Parameter and call evaluation rules remain unchanged by nullability. A nullable function value
// with an explicit matching signature is one argument value; `ref` and `out` require exact
// writable storage, and a vararg parameter packs nullable function values into one array argument.
// A default `null` is evaluated at the call site under the target parameter's declared nullable
// function type. Named/default/vararg mapping is completed before the callee is entered.

func optionalDefault(callback: (() -> int)? = null): int {
    if callback != null { return callback() }
    0
}

func callbackVararg(vararg callbacks: (() -> int)?): int {
    val callback = callbacks[0]
    if callback != null { return callback() }
    0
}

func defaultAndVararg(): int = callbackVararg(null, func() { optionalDefault() + 42 })

// FUNCTION EFFECTS AND PARAMETER MODES
// ====================================
//
// Nullability never erases the function's declared checked-effect set. The nullable wrapper
// answers whether a value is present; the payload function type still owns its parameter modes,
// result and checked effects. After a non-null proof, an indirect call contributes the payload
// effects to the caller exactly as any other indirect call would.
//
// `ref` and `out` are part of a function value's exact ABI. Ref/out modes cannot be adapted by
// wrapping the function pointer. For a nullable callback parameter type, the pointer itself may be
// null, but each ref/out argument still refers to caller storage of the exact payload parameter
// type. Out definite-assignment is recorded only on the normal call edge.

open class IOException: Exception {}

func throwsOnCall(): int throws IOException { throw IOException() }

func optionalCheckedEffect(callback: (() -> int throws IOException)?): int
    throws IOException {
    if callback == null { return 0 }
    callback()
}

func refOutCallback(callback: ((ref int, out int) -> ())?): () {
    if callback == null { return () }
    var input = 40
    var output: int
    callback(ref input, out output)
    val answer = output
}

// ARRAY STORAGE AND YELLOW TEA
// ============================
//
// A nullable function element uses the same pointer-width scalar layout as a non-null function
// element. The null value is zero; a concrete noncapturing value is a code address. Yellow Tea
// traces the containing managed array while it is live, but the scalar function element itself is
// not treated as a managed object header. The collector must never interpret code as a heap object.
// A nested array traces each inner array reference and retains the no-scan function layout at the
// leaf.
//
// This representation does not establish closure support. A capturing lambda requires a distinct
// environment reference with object identity, lifetime, transfer and GC scanning semantics. It
// cannot be put into a raw-code-pointer slot. Host GC and Guest GC remain isolated by process
// boundaries; no function pointer or heap object is shared between those processes as a managed
// reference.

func nullableCallbackArraySurvivesCollection(): int {
    val callbacks: []((() -> int)?) = [null, func() { 42 }]
    Gc.collect()
    val callback = callbacks[1]
    if callback == null { return 0 }
    callback()
}

func nestedNullableCallbackArray(): int {
    val callbacks: [][]((() -> int)?) = [[null], [func() { 42 }]]
    val callback = callbacks[1][0]
    if callback != null { return callback() }
    0
}

func nullOnlyCollectionIsNotTyped(): () {
    // Negative: `val callbacks = [null]` has no function signature and is not an array of callables.
    val callbacks: []((() -> int)?) = [null]
}

// SOURCE IDENTITY, ALIASES AND CAPTURES
// =====================================
//
// Refinements attach to resolved local identities, not to names. A same-spelled declaration in a
// nested scope is independent. An immutable alias initialized from a nullable value keeps its own
// declared nullability unless its initializer and the current path prove otherwise. Reassigning
// either variable does not transfer an old proof to the other.
//
// Reading an array element or property twice does not prove the second read equals the first.
// Snapshot the nullable function into a stable local, then refine that local. A custom getter may
// return a different value on every access; an open property may be overridden. A null check on a
// property is not permission to issue a later unchecked call through a fresh property read.

class CallbackSource(open val callback: (() -> int)?)

func propertyReadIsNotStable(source: CallbackSource): int {
    if source.callback != null {
        return source.callback()
    }
    0
}
// expect: A complete flow verifier must reject the second property read as an unproven callable.

func snapshotProperty(source: CallbackSource): int {
    val callback = source.callback
    if callback != null { return callback() }
    0
}

func aliasIsIndependent(original: (() -> int)?): int {
    val alias = original
    if original != null { return alias() }
    0
}
// expect: The original's refinement does not silently rewrite the separately declared alias type.

// A closure that captures a nullable value also captures the rules governing that captured slot.
// Capturing val copies the value at closure creation; capturing var shares a mutable cell. The
// nested function must not reuse a presence proof from before the capture or call. Ref/out,
// ref-like and borrowed captures require their own lifetime proof and are not authorized by this
// nullable function-value contract.

func rejectedCapture(callback: (() -> int)?): () {
    val nested = func(): int {
        if callback == null { return 0 }
        callback()
    }
}
// expect: Source capture inventory is available; native FIR rejects the environment closure.

// EVALUATION ORDER AND CALL TRANSACTIONS
// ======================================
//
// The callable expression is evaluated exactly once before its arguments. Each argument is then
// evaluated exactly once from left to right, with ref/out storage addresses preserved at their
// source position. The compiler cannot evaluate a function pointer twice to perform a check and
// then call a newly loaded pointer: the check and invocation must refer to the same value under the
// chosen flow/storage model. A call that may mutate its source local through an alias invalidates
// any fact that depends on that local.
//
// When a nullable local is checked, the guard loads the exact slot value that dominates the
// indirect call. If a concurrent mutation model is later introduced for shared cells, the
// language's memory model must define whether this local proof is still valid; a plain code pointer
// alone does not provide synchronization.

func selectOnce(factory: () -> (() -> int)?): int {
    val callback = factory()
    if callback == null { return 0 }
    callback()
}

func argumentRunsOnlyAfterPresence(factory: () -> (() -> int)?, value: () -> int): int {
    val callback = factory()
    if callback == null { return 0 }
    callback() + value()
}

// NATIVE ADMISSION AND DIAGNOSTICS
// ================================
//
// Verified native admission is narrower than the source language. It currently supports a
// nullable wrapper whose payload is a closed function signature supported by the pointer ABI.
// Local storage, argument/return slots, arrays, direct null equality/inequality, ref/out forwarding,
// supported defaults/varargs, and source-proven branch calls have AOT and ORC execution coverage.
// A non-null function value may enter the nullable slot only without an ABI adapter.
//
// The verifier rejects a null-only sentinel in a slot or a callable signature; it rejects nullable
// non-function values whose object/boxing layout is not implemented; it rejects a narrowed read
// unless source analysis binds that exact read to the payload type on its control-flow path. The
// LLVM bridge only needs pointer `eq`/`ne` for the null predicate. Ordered pointer comparisons,
// integer arithmetic on a function address, and pointer-to-object reinterpretation are not part of
// this feature.
//
// SOL2221 is a source-level no-callable-signature diagnostic. SOL2400 says a source shape is outside
// the native subset. SOL2402 indicates an independently verified FIR invariant failure. A failed
// verifier check must not be hidden by code generation, and a native boundary rejection must not be
// presented as a source-language type error.

func noAdapterForVariance(callback: ((Object) -> Object)?): int = 0
// expect: Source may retain the language conversion; exact native pointer ABI rejects an adapter.

func notNullableObject(value: String?): int = 0
// expect: FIR: Rejected(SOL2400); this contract does not implement nullable managed references.

// CONFIRMATION CASES
// ==================
// These functions are intentionally parser-valid examples. Their status comments identify source
// and native outcomes separately and are not substitutes for the release CI evidence.

func nullFromFactory(enabled: bool): (() -> int)? {
    if enabled { return func() { 42 } }
    null
}

func callFromFactory(enabled: bool): int {
    val callback = nullFromFactory(enabled)
    if callback == null { return 0 }
    callback()
}

func callThroughArray(enabled: bool): int {
    val callbacks: []((() -> int)?) = [null, func() { 42 }]
    var index = 0
    if enabled { index = 1 }
    val callback = callbacks[index]
    if callback != null { return callback() }
    0
}

func callWithExplicitNull(callback: (() -> int)?): int = acceptNullableCallback(callback)

func callbackDefaultAndExplicit(callback: (() -> int)? = null): int {
    if callback == null { return 0 }
    callback()
}

func effectBudgetIsRetained(callback: (() -> int throws IOException)?): int
    throws IOException {
    if callback == null { return 0 }
    callback()
}

func exactRefOut(callback: ((ref int, out int) -> ())?): () {
    if callback == null { return () }
    var source = 1
    var target: int
    callback(ref source, out target)
}

// The source and test contracts are intentionally layered: parser acceptance, source binding,
// flow facts, FIR verification, LLVM construction, AOT execution, and ORC execution each have an
// independent result. A passing parser case does not imply native support for captures, assertions,
// adapters or nullable object references.
