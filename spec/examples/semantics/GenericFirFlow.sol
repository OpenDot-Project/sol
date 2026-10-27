// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative flow matrix; current FIR proves closed CFG slots, while full instance rebinding remains planned.
module examples.semantics.generic_fir_flow

// Generic parameters through source flow and specialized FIR
// =========================================================
//
// These scenarios define expected relationships between source analysis, the verifier's per-instance
// CFG, and eventual runtime behavior. They are normative examples, not a claim that every scenario
// currently compiles to native code. `SourceBound` means the generic source declaration and call
// are valid under the language contract. `NativeAdmitted` marks shapes inside the present bounded
// implementation. `NativeRejected` means source binding may succeed but the native subset rejects
// the body. `PlannedRebind` means correctness depends on per-instance semantic rebinding that is
// not yet implemented. `SourceRejected` marks an intentional negative case.
//
// All instance labels use declaration identity plus ordered, closed type arguments. Source type
// parameter spellings are not identity. IDs printed by compiler inspection tools are local to one
// compilation and are deliberately omitted from the oracle strings below.

// Shared functions used to make flow edges explicit
// --------------------------------------------------

func <T> identity(value: T): T = value
func <T> selectFirst(left: T, right: T): T = left
func <T> selectSecond(left: T, right: T): T = right
func <U> forward(value: U): U = identity(value)
func <T> relay(value: T): T = identity(value)
func <T> copyOut(out target: T, source: T): () { target = source }
func <T> replace(ref target: T, source: T): () { target = source }
func <T> writeThenReturn(out target: T, source: T): T { target = source; source }
func <T> branchWrite(flag: bool, out target: T, first: T, second: T): () {
    if flag { target = first } else { target = second }
}
func <T> arrayCopy(values: []T): []T = values
func <T> firstOrDefault(values: []T, fallback: T): T {
    for val item in values { return item }
    fallback
}
func mark(value: int): int = value

// 1. Pass-through parameters and local slots
// ------------------------------------------

// expect: passthrough.local.val | SourceBound | NativeAdmitted<int> | return=argument
func <T> passThroughVal(value: T): T {
    val saved: T = value
    saved
}

// expect: passthrough.local.var | SourceBound | NativeAdmitted<String> | local-write=one | return=argument
func <T> passThroughVar(value: T): T {
    var saved: T = value
    saved = value
    saved
}

// The source slot is typed with the declaration's T. A closed instance substitutes both the slot
// and every read/write expression. The body doesn't acquire a new inference rule after selection.

// expect: passthrough.local.uninitialized-then-write | SourceBound | NativeAdmitted<int> | read-after-write
func <T> writeThenRead(value: T): T {
    var saved: T
    saved = value
    saved
}

// expect: passthrough.local.read-before-write | SourceRejected | diagnostic=SOL2240 | fir=absent
func <T> readBeforeWrite(value: T): T {
    var saved: T
    val before = saved
    saved = value
    before
}

// expect: passthrough.local.val-double-write | SourceRejected | diagnostic=write-once | fir=absent
func <T> writeValTwice(value: T): T {
    val saved: T
    saved = value
    saved = value
    saved
}

// Mutually exclusive writes can satisfy a `val` only when the source single-assignment rules prove
// each path writes at most once. This is separate from the must-assigned property after the join.

// expect: passthrough.local.val-branch-join | SourceBound | NativeAdmitted<int> | both-arms-write
func <T> valWritesInBothArms(flag: bool, left: T, right: T): T {
    val selected: T
    if flag { selected = left } else { selected = right }
    selected
}

// expect: passthrough.local.val-missing-arm | SourceRejected | diagnostic=SOL2240 | false-edge-unassigned
func <T> valWritesOneArm(flag: bool, value: T): T {
    val selected: T
    if flag { selected = value }
    selected
}

// expect: passthrough.local.var-one-arm | SourceRejected | diagnostic=SOL2240 | false-edge-unassigned
func <T> varWritesOneArm(flag: bool, value: T): T {
    var selected: T
    if flag { selected = value }
    selected
}

// 2. Returns and normal-exit completeness
// ---------------------------------------

// expect: return.explicit | SourceBound | NativeAdmitted<int> | all-normal-returns=T
func <T> explicitReturn(flag: bool, left: T, right: T): T {
    if flag { return left }
    return right
}

// expect: return.expression-body | SourceBound | NativeAdmitted<String> | result=T
func <T> expressionReturn(value: T): T = value

// expect: return.missing-else | SourceRejected | diagnostic=missing-return | false-edge-falls-through
func <T> missingElseReturn(flag: bool, value: T): T {
    if flag { return value }
}

// expect: return.unreachable-tail | SourceBound | NativeAdmitted<int> | unreachable-tail-not-a-value
func <T> returnBeforeTail(value: T): T {
    return value
    val unreachable = value
}

// expect: return.nested-generic | SourceBound | NativeAdmitted<int> | calls=[relay<int>,identity<int>]
func <T> returnFromNestedGeneric(value: T): T {
    return relay(value)
}

// expect: return.arm-fallthrough-with-value | SourceBound | NativeAdmitted<String> | last-expression=T
func <T> oneArmReturns(flag: bool, value: T, other: T): T {
    if flag { return value }
    other
}

// A source return proof is independent of concrete runtime path selection. Both edges are in the
// CFG even if a particular execution always supplies `flag = true`.

// 3. If joins and branch-local call effects
// -----------------------------------------

// expect: branch.out.both-arms | SourceBound | NativeAdmitted<int> | out-assigned-after-if
func <T> outOnBothArms(flag: bool, left: T, right: T, out result: T): () {
    if flag { result = left } else { result = right }
}

// expect: branch.out.one-arm | SourceRejected | diagnostic=SOL2242 | out-not-assigned-on-false
func <T> outOnOneArm(flag: bool, value: T, out result: T): () {
    if flag { result = value }
}

// expect: branch.return-removes-predecessor | SourceBound | NativeAdmitted<int> | normal-return-arm-writes
func <T> returnOnMissingArm(flag: bool, value: T, out result: T): () {
    if flag { result = value } else { return }
}

// expect: branch.throw-not-normal-write | SourceRejected | diagnostic=SOL2242 | exceptional-arm-does-not-satisfy-out
func <T> throwOnMissingArm(flag: bool, value: T, out result: T): () throws Failure {
    if flag { result = value } else { throw Failure("not assigned") }
}

class Failure: Exception {}

// expect: branch.out-call-only-true | SourceBound | NativeAdmitted<int> | caller-slot-assigned-after-if=false
func <T> conditionalOutCall(flag: bool, value: T): T {
    var result: T
    if flag { copyOut(out result, value) } else { result = value }
    result
}

// expect: branch.nongeneric-call-on-T | SourceBound | NativeRejected | diagnostic=SOL2400 | open-argument-to-int
func <T> rejectedNongenericCall(value: T): int {
    // A concrete `T=int` instance cannot retroactively bind this source call.
    return mark(value)
}

// 4. Loops, zero-trip edges, break, and continue
// ---------------------------------------------

// expect: loop.preset-local | SourceBound | NativeAdmitted<int> | zero-trip-safe
func <T> presetBeforeLoop(values: []T, fallback: T): T {
    var result: T = fallback
    for val item in values { result = item }
    result
}

// expect: loop.body-only-write | SourceRejected | diagnostic=SOL2240 | zero-trip-edge-unassigned
func <T> bodyOnlyWrite(values: []T): T {
    var result: T
    for val item in values { result = item }
    result
}

// expect: loop.both-branches-write | SourceRejected | diagnostic=SOL2240 | zero-trip-edge-unassigned
func <T> branchesInsideDoNotRemoveZeroTrip(values: []T, flag: bool, a: T, b: T): T {
    var result: T
    for val item in values {
        if flag { result = a } else { result = b }
    }
    result
}

// expect: loop.break-write-plus-exhaustion | SourceBound | NativeAdmitted<String> | preset-covers-exhaustion
func <T> breakAfterWrite(values: []T, fallback: T): T {
    var result: T = fallback
    for val item in values {
        result = item
        break
    }
    result
}

// expect: loop.break-arm-write-no-preset | SourceRejected | diagnostic=SOL2240 | exhaustion-unassigned
func <T> breakArmOnly(values: []T, stop: bool): T {
    var result: T
    for val item in values {
        if stop { result = item; break }
    }
    result
}

// expect: loop.continue-before-write | SourceRejected | diagnostic=SOL2240 | continue-edge-skips-write
func <T> continueBeforeWrite(values: []T): T {
    var result: T
    for val item in values {
        if shouldSkip() { continue }
        result = item
    }
    result
}

func shouldSkip(): bool = false

// expect: loop.write-before-continue | SourceBound | NativeAdmitted<int> | all-body-continues-write
func <T> writeBeforeContinue(values: []T, skip: bool, fallback: T): T {
    var result: T = fallback
    for val item in values {
        result = item
        if skip { continue }
    }
    result
}

// expect: loop.return-inside-body | SourceBound | NativeAdmitted<int> | post-loop-path-uses-fallback
func <T> firstOrFallback(values: []T, fallback: T): T {
    for val item in values { return item }
    fallback
}

// expect: loop.infinite-no-fallthrough | SourceBound | NativeAdmitted<int> | termination-proof-independent-of-T
func <T> loopWithExplicitExit(value: T): T {
    while true {
        if shouldStop() { break }
        continue
    }
    value
}

func shouldStop(): bool = true

// The example records control-flow edges, not a promise that the source analyzer proves arbitrary
// loops terminate. Reachability and termination are different analyses.

// 5. Nested loops preserve loop ownership
// ---------------------------------------

// expect: nested.loop.outer-zero-trip | SourceRejected | diagnostic=SOL2240 | inner-write-not-proof-outer-entered
func <T> innerLoopWrites(values: [][]T): T {
    var result: T
    for val row in values {
        for val item in row { result = item }
    }
    result
}

// expect: nested.loop.break-is-inner | SourceBound | NativeRejected | inner-break-does-not-exit-outer
func <T> innerBreak(values: [][]T, fallback: T): T {
    var result: T = fallback
    for val row in values {
        for val item in row { result = item; break }
    }
    result
}

// `[][]T` is not admitted by the current native array ABI even where source flow is valid. These
// cases illustrate source CFG rules and must not be counted as native feature coverage.

// 6. Exact ref flow
// -----------------

// expect: ref.exact-instance | SourceBound | NativeAdmitted<int> | type-arguments=[int] | assigned-at-entry=true
func <T> readRef(ref value: T): T = value

// expect: ref.pass-through-local | SourceBound | NativeAdmitted<String> | exact-ref-slot
func <T> refThroughLocal(ref value: T): T {
    val copy: T = value
    copy
}

// expect: ref.write-through | SourceBound | NativeAdmitted<int> | caller-refinement-invalidated
func <T> refReplace(ref value: T, replacement: T): T {
    value = replacement
    value
}

func refCalls(): int {
    var location: int = 4
    val before = readRef(ref location)
    refReplace(ref location, before)
}

// expect: ref.unassigned-actual | SourceRejected | diagnostic=SOL2240 | call-not-created
func <T> invalidRefActual(): T {
    var location: T
    readRef(ref location)
}

// expect: ref.non-exact | SourceRejected | diagnostic=exact-ref-type | no-conversion
func invalidRefWidening(value: byte): int {
    var slot: int = 0
    modifyByte(ref slot)
    slot
}

func modifyByte(ref value: byte): () { value = 1 }

// `ref` locations are not accepted by equal LLVM register class. The formal and location source
// types, mutability, owner identity, and actual slot must match exactly.

// 7. Out flow in the generic callee
// ---------------------------------

// expect: out.all-normal-arms | SourceBound | NativeAdmitted<int> | out-assigned-on-return=true
func <T> assignOnEitherPath(flag: bool, left: T, right: T, out result: T): () {
    if flag { result = left } else { result = right }
}

// expect: out.no-write | SourceRejected | diagnostic=SOL2242 | normal-return-unassigned
func <T> missingOutWrite(flag: bool, value: T, out result: T): () {
    if flag { result = value }
}

// expect: out.return-path | SourceBound | NativeAdmitted<String> | return-edge-is-normal-exit
func <T> writeThenReturn(flag: bool, value: T, out result: T): () {
    result = value
    if flag { return }
}

// expect: out.throwing-path | SourceBound | NativeRejected | exception-edge-not-normal-out-obligation
func <T> writeOrThrow(flag: bool, value: T, out result: T): () throws Failure {
    if flag { result = value } else { throw Failure("no normal return") }
}

// expect: out.read-before-write | SourceRejected | diagnostic=SOL2240 | out-starts-unassigned
func <T> invalidReadOut(out result: T): T {
    val before = result
    result = before
    before
}

// The source-level out contract is about normal callee completion. FIR must preserve separate
// normal and exceptional successors so caller DA does not treat every call exit as assignment.

// 8. Out calls and `out var`
// -------------------------

// expect: out.call-existing | SourceBound | NativeAdmitted<int> | before=unassigned | normal-after=assigned
func <T> callOutExisting(value: T): T {
    var result: T
    copyOut(out result, value)
    result
}

// expect: out.call-new-local | SourceBound | NativeAdmitted<String> | created-after-selection | assigned-after-normal-return
func callOutVar(): String {
    copyOut(out var produced, "value")
    produced
}

func <T> outWithOtherEvidence(prototype: T): T {
    copyOut(out var produced, prototype)
    produced
}

// expect: out.var-infers-from-value | SourceBound | NativeAdmitted<int> | type-arguments=[int]
func outVarFromOtherActual(): int {
    copyOut(out var produced, 5)
    produced
}

// expect: out.var-no-evidence | SourceRejected | diagnostic=CannotInferTypeArgument | no-out-var-type
func outVarAloneCannotInfer(): () {
    copyOut(out var produced, [])
}

// The no-evidence case is intended to demonstrate that the out-var itself contributes no T. If
// another argument, explicit type argument, or permitted expected result closes T, the candidate
// can be considered. It is not legal to materialize one speculative local per overload candidate.

// expect: out.var-overload-ambiguous | SourceRejected | diagnostic=AmbiguousCall | no-local-committed
func ambiguousOutVar(): () {
    writeEither(out var result)
}

func writeEither(out result: int): () { result = 1 }
func writeEither(out result: String): () { result = "x" }

// expect: out.var-short-circuit | SourceRejected | diagnostic=SOL2240 | invocation-is-conditional
func outVarShortCircuit(flag: bool): int {
    flag && writeBool(out var result)
    result
}

func writeBool(out result: int): bool { result = 1; true }

// The syntax form `out var result` introduces its name in the call's defined lexical scope only
// after unique selection. Reads after the call are permitted on the normal successor, not on paths
// where a conditional operator skips invocation or an exception leaves the call.

// 9. Exact ref/out under generic substitution
// -------------------------------------------

// expect: exact.ref-inferred | SourceBound | NativeAdmitted<int> | formal=T | actual=int
func <T> forwardRef(ref source: T): T {
    readRef<T>(ref source)
}

// expect: exact.out-inferred | SourceBound | NativeAdmitted<String> | formal=T | actual=String
func <T> forwardOut(source: T, out target: T): () {
    copyOut<T>(out target, source)
}

// expect: exact.ref-and-out | SourceBound | NativeAdmitted<int> | order=[ref,out] | exact=true
func <T> swapLike(ref left: T, out right: T): () {
    right = left
}

func closedRefRoot(value: int): int {
    var copy = value
    forwardRef(ref copy)
}

func closedOutRoot(value: String): String {
    var copy: String
    forwardOut(value, out copy)
    copy
}

// Each wrapper retains a declaration-owned parameter until its own instance is closed. The inner
// callee's T is distinct from the wrapper's T. The selected call edge maps one type identity to the
// other; a spelling-based same-T shortcut is not a verifier rule.

// expect: nested.outvar-through-wrapper | SourceBound | NativeAdmitted<String> | call-graph=[forwardOut<String>,copyOut<String>]
func nestedOutVarRoot(value: String): String {
    forwardOut(value, out var copy)
    copy
}

// expect: nested.ref-mismatch | SourceRejected | diagnostic=exact-ref-type | no-instance
func nestedRefMismatch(): byte {
    var widened: int = 0
    forwardRef<byte>(ref widened)
}

// The call's inferred type in the mismatch example is anchored by the exact actual location, not
// by the wrapper result annotation or backend representation.

// 10. Nested generic value flow and joins
// ---------------------------------------

// expect: nested.local-call | SourceBound | NativeAdmitted<int> | calls=[forward<int>,identity<int>]
func <T> nestedLocal(value: T): T {
    val copied: T = forward(value)
    copied
}

// expect: nested.joined-calls | SourceBound | NativeAdmitted<String> | both-call-results-assigned
func <T> chooseForward(flag: bool, left: T, right: T): T {
    val result: T
    if flag { result = forward(left) } else { result = relay(right) }
    result
}

// expect: nested.call-result-branch | SourceBound | NativeAdmitted<int> | result-type=bool
func <T> selectByExplicitFlag(flag: bool, first: T, second: T): T {
    if flag { selectFirst(first, second) } else { selectSecond(first, second) }
}

// expect: nested.expected-result | SourceBound | NativeAdmitted<String> | result-evidence=outer-context
func <T> expectedReturn(): T = identity<T>(identityPlaceholder<T>())

// This declaration uses a declared helper result as source evidence. Specialization must compose
// substitutions through each selected nested call and preserve each target's own tuple ordering.

// expect: nested.instance-sharing | SourceBound | NativeAdmitted<int> | target=identity<int> | two-origins
func <T> twoCallSites(value: T): T {
    val first = identity(value)
    val second = identity(first)
    second
}

// Sharing the body key does not merge the two call sites, discard an evaluation, or move a call
// across a branch. Every expression retains its span, call mapping, effect, and data dependency.

// 11. Arrays, indexing, and iteration
// -----------------------------------

// expect: array.pass-through | SourceBound | NativeAdmitted<[]int> | element=int
func <T> passArray(values: []T): []T = values

// expect: array.index-copy | SourceBound | NativeAdmitted<[]String> | element-slot=String
func <T> copySelected(values: []T, index: int): T {
    val selected: T = values[index]
    selected
}

// expect: array.index-assignment | SourceBound | NativeAdmitted<[]String> | index-read-normal-edge-initializes-slot
func <T> indexedAssignment(values: []T, index: int): T {
    var selected: T
    selected = values[index]
    selected
}

// The exceptional bounds-check edge occurs before the assignment completes. It is not a normal
// path to the later read, but it must remain represented as a failure edge in FIR/runtime lowering.

// expect: array.iteration-copy | SourceBound | NativeAdmitted<[]int> | item-slot=T | zero-trip=yes
func <T> lastOrFallback(values: []T, fallback: T): T {
    var selected: T = fallback
    for val item in values { selected = item }
    selected
}

// expect: array.iteration-item-read | SourceBound | NativeAdmitted<[]String> | item-assigned-at-body-entry
func <T> firstOrFallback(values: []T, fallback: T): T {
    for val item in values { return item }
    fallback
}

// expect: array.iteration-write-insufficient | SourceRejected | diagnostic=SOL2240 | zero-trip-join
func <T> missingFallback(values: []T): T {
    var selected: T
    for val item in values { selected = item }
    selected
}

// expect: array.nested-shape | SourceBound | NativeRejected | reason=multi-level-array-ABI
func <T> nestedArrayPass(values: [][]T): [][]T = values

// A closed single-level array with an admitted primitive/String element is distinct from a nested
// array, nullable element, span, collection interface, or ref-like element. Source DA semantics are
// the same, but FIR/native admission requires a separate concrete layout and ownership proof.

// 13. Unsupported operations are explicit negative cases
// ------------------------------------------------------

// expect: unsupported.open-arithmetic | SourceBound | NativeRejected | diagnostic=SOL2400 | no-rebind
func <T> addOpen(value: T, other: T): T {
    value + other
}

// expect: unsupported.open-member | SourceBound | NativeRejected | diagnostic=SOL2400 | no-member-rebind
func <T> openLength(value: T): int {
    value.length
}

// expect: unsupported.open-to-nongeneric | SourceBound | NativeRejected | diagnostic=SOL2400
func <T> openArgumentToInt(value: T): int {
    mark(value)
}

// These source expressions are not authorized merely because an eventual call might choose T=int,
// T=String, or an array type. The current specialized body projection does not rebind source
// operators, members, or nongeneric candidate sets per instance. A valid future implementation
// must run concrete source semantics and verify the resulting FIR rather than erase the error.

// expect: unsupported.generic-helper | SourceBound | NativeRejected | capability-must-be-declared
func <T: std.core.Object> returnConstrained(value: T): T = identity(value)

// expect: unsupported.unconstrained-equality | SourceRejected | diagnostic=no-equality-capability
func <T> compareOpenValues(left: T, right: T): bool = left == right

// Parsing this fixture does not assert the `expect` outcomes have been executed by a harness. The
// implementation status is documented in `spec/GenericFirFlow.sol` and
// `docs/generic-fir-specialization/Flow.md`.
