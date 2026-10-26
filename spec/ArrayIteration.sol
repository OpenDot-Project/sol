// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative source contract; native qualification is tracked separately in docs.
module spec.arrayiteration.rules

class IterationFailure: Exception {}
class AcquisitionFailure: Exception {}
class AdvanceFailure: Exception {}
class CurrentFailure: Exception {}
class CloseFailure: Exception {}
class CleanupFailure: Exception {}

interface Iterator<T> {
    func moveNext(): bool throws AdvanceFailure
    func current(): T throws CurrentFailure
}

interface Iterable<T> {
    func iterator(): Iterator<T> throws AcquisitionFailure
}

interface ResourceIterator<T>: Iterator<T> {
    func close(): () throws CloseFailure
}

// ITERATION SOURCE CATEGORIES
//
// A `for-in` source has one statically resolved iteration category. Categories are not guessed
// from a common method spelling after lowering.
//
// 1. `[]T` is a managed dynamic-array reference. Its length is fixed after allocation. Intrinsic
//    array traversal evaluates the reference once and uses one length snapshot.
// 2. `[N]T` is inline fixed-size storage. Its element sequence is exactly N values and does not
//    acquire a heap iterator solely to participate in a source loop.
// 3. An `Iterable<T>` selects `iterator()` and then the returned `Iterator<T>` protocol.
// 4. A type without the Iterable interface may use the exact resolved method pattern:
//    `iterator(): Cursor`, `moveNext(): bool`, and `current(): T`, with optional `close(): ()`.
// 5. A `Vector<T>` or another collection is not an array by representation. It participates by
//    implementing/resolving the iterator contract.
//
// When a source satisfies more than one category, member binding and the language's ordinary
// overload/member precedence select one exact route before flow analysis. An implementation may not
// execute both routes, choose one based on runtime type, or reinterpret a cursor as an array index.

func dynamicArray(values: []int): int {
    var total = 0
    for val value in values {
        total += value
    }
    total
}

func inlineArray(values: [4]int): int {
    var total = 0
    for val value in values {
        total += value
    }
    total
}

// Dynamic arrays, inline arrays, and growable collection objects have different layouts and
// mutation contracts. Their common surface spelling `for val x in value` does not erase those
// differences.
func growableCollection(values: Vector<int>): int {
    var total = 0
    for val value in values {
        total += value
    }
    total
}

// DYNAMIC ARRAY EVALUATION
//
// The iterable expression is evaluated exactly once before the loop's first test. If evaluation
// completes normally, the resulting reference is retained strongly for the traversal. The runtime
// length is read once after that evaluation. `[]T` has immutable length; element stores do not alter
// the saved bound. The item sequence is values[0], values[1], ..., values[length-1].
//
// A zero-length array has zero body entries. A non-empty source does not imply that its body runs
// if an earlier expression fails or an outer transfer terminates the enclosing path. The body is
// not entered until its iteration test succeeds and the element value has been loaded.

func acquireArray(): []int throws AcquisitionFailure {
    val answer = makeArray()
    answer
}

func evaluateArrayOnlyOnce(): int throws AcquisitionFailure {
    var sum = 0
    for val element in acquireArray() {
        sum += element
    }
    sum
}

func sourceCallRunsForAnEmptyResult(): () throws AcquisitionFailure {
    // The call runs once. Its empty result makes the body unreachable; it does not erase the
    // acquisition effect.
    for val element in acquireEmptyArray() {
        consume(element)
    }
}

func sourceVariableCanBeRebound(values: []int, replacement: []int): int {
    var source = values
    var sum = 0
    for val element in source {
        source = replacement
        sum += element
    }
    sum
}

// The `source` expression above is materialized before entry. Rebinding its source local cannot
// retarget an active traversal. An indexed write through the retained reference can still be
// observed by a later element load. No iterator snapshot of the elements is created.

func indexedWriteIsVisible(values: []int): int {
    var total = 0
    var index = 0
    for val item in values {
        if index == 0 && values.length > 1 {
            values[1] = 99
        }
        total += item
        index += 1
    }
    total
}

// ITEM BINDING AND VALUE COPY
//
// Every successful iteration loads one element and writes that value into a loop-local slot
// before entering the body. The slot's declared mutability comes from the source binding:
// `val` is read-only and `var` is writable. This binding is a value copy, not an alias to the
// array's element storage. An assignment to a mutable item changes only the local slot.

func immutableItemCannotBeAssigned(values: []int): () {
    for val item in values {
        // Expected error: `item` is immutable.
        item = 17
    }
}

func mutableItemDoesNotWriteBack(values: []int): int {
    var total = 0
    for var item in values {
        item += 1
        total += item
    }
    total
}

func explicitArrayElementWriteBack(values: []int): () {
    var index = 0
    for var item in values {
        item += 1
        values[index] = item
        index += 1
    }
}

class Box {
    var value: int
}

func referenceItemCopyPreservesObjectIdentity(values: []Box): () {
    for val box in values {
        box.value += 1
    }
}

func replacingReferenceItemDoesNotReplaceArrayElement(values: []Box, other: Box): () {
    for var box in values {
        box = other
    }
}

// The reference slot `box` above is copied. Mutating `box.value` may be observed through an alias
// to the same object. Rebinding the local reference does not store `other` into the array.

// ZERO-ITERATION AND FLOW JOIN
//
// Every array loop has a zero-trip predecessor unless the source analysis proves a stronger
// condition. The entry facts therefore join with all reachable break and exhaustion facts after
// the loop. The intersection of definitely assigned locals cannot include a write that occurs only
// in the body.

func arrayCanBeEmpty(values: []int): int {
    var last: int
    for val item in values {
        last = item
    }
    // Expected error: the zero-trip edge leaves `last` unassigned.
    last
}

func assignedBeforeLoop(values: []int): int {
    var last = -1
    for val item in values {
        last = item
    }
    last
}

func breakCanHappenOnTheFirstItem(values: []int): int {
    var result = 0
    for val item in values {
        result = item
        break
    }
    // Expected error: empty input reaches the join without assigning `result`.
    result
}

func returnDoesNotCreateNormalAssignment(values: []int): int {
    var result: int
    for val item in values {
        result = item
        return result
    }
    // Expected error: empty input reaches this read.
    result
}

func initializeOnEveryNonEmptyBodyArm(values: []int): int {
    var result = -1
    for val item in values {
        if item < 0 {
            result = -item
        } else {
            result = item
        }
    }
    result
}

// Body-local facts are computed after the iterable has been evaluated and after the item has been
// assigned. They do not become facts on the zero-trip exit. Exceptions from the iterable expression
// use the pre-body state; body exceptions use the state at their own source location.

func sourceEffectPrecedesBody(values: []int, shouldFail: bool): int throws IterationFailure {
    var seen = 0
    for val item in prepare(values, shouldFail) {
        seen += item
    }
    seen
}

func bodyExceptionDoesNotUseLaterAssignment(values: []int): int throws IterationFailure {
    var seen: int
    for val item in values {
        if item < 0 {
            throw IterationFailure()
        }
        seen = item
    }
    0
}

// EMPTY ARRAY LITERALS
//
// An empty literal has no element type unless expected typing supplies one. Once typed, it has a
// statically known zero length. The body remains part of syntax and type/name validation, but its
// runtime effects and exceptional edges are unreachable. The iterable expression itself remains
// normally evaluated.

func typedEmptyLiteralHasNoBodyEffect(): () {
    val empty: []int = []
    for val item in empty {
        // This call is unreachable and contributes no escaping effect to this function.
        impossibleEffect()
    }
}

func contextualEmptyLiteralHasNoBodyEffect(): () {
    val empty: []int = []
    for val item in empty {
        impossibleEffect()
    }
}

func untypedEmptyLiteralNeedsContext(): () {
    for val item in [] {
        // Expected inference error: no element type is available.
        consume(item)
    }
}

func emptyLiteralBodyIsStillChecked(): () {
    val empty: []int = []
    for val item in empty {
        // Expected error: `missingName` is unresolved even on a zero-trip path.
        consume(missingName)
    }
}

func emptyLiteralDoesNotHideInvalidMutation(): () {
    val empty: []int = []
    for val item in empty {
        // Expected error: the item is immutable, even though the body is unreachable at runtime.
        item = 4
    }
}

// A variable or function call that produces an empty array at runtime is not a static proof.
// Its body is checked as a possible path and contributes effects that can escape.
func maybeEmptyArrayRetainsBodyEffect(values: []int?): () throws IterationFailure {
    for val item in chooseArray(values) {
        mayFail(item)
    }
}

// CHECKED EFFECT OWNERSHIP
//
// The iterable expression's effects occur once before the first iteration test. For an unknown
// array value, body effects are possible and are included in the enclosing function's effect set.
// A typed, statically empty literal contributes no body effect. The runtime length and in-range
// array access are intrinsic non-user operations in the language contract.

func arrayBodyEffectIncluded(values: []int): () throws IterationFailure {
    for val item in values {
        mayFail(item)
    }
}

func arrayBodyEffectMissing(values: []int): () {
    // Expected error: the array may be non-empty and `mayFail` can throw.
    for val item in values {
        mayFail(item)
    }
}

func expressionEffectRemainsForEmptyResult(): () throws AcquisitionFailure {
    for val item in acquireEmptyArray() {
        noEffect(item)
    }
}

func emptyBodyEffectDoesNotEscape(): () {
    val empty: []int = []
    for val item in empty {
        mayFail(item)
    }
}

func cursorAdvanceAndCloseEffectsAreSeparate(cursor: ResourceIterator<int>): int
    throws AdvanceFailure, CurrentFailure, CloseFailure {
    var sum = 0
    for val item in cursor {
        sum += item
    }
    sum
}

func cursorEffectBudgetMustIncludeEveryOperation(cursor: ResourceIterator<int>): int {
    // Expected effects: iterator acquisition if required, moveNext, current, and close.
    for val item in cursor {
        return item
    }
    0
}

// CONTROL TRANSFERS
//
// Normal body completion and `continue` advance the current loop after per-iteration lexical
// cleanup. `break` leaves the loop without another item load. `return` leaves the function.
// `throw` leaves through its exceptional edge. All implicit enclosing scopes perform their own
// cleanup according to the shared cleanup-transfer contract.

func sumWithContinue(values: []int): int {
    var total = 0
    for val item in values {
        if item < 0 {
            continue
        }
        total += item
    }
    total
}

func firstPositiveOrZero(values: []int): int {
    for val item in values {
        if item > 0 {
            return item
        }
    }
    0
}

func stopAtSentinel(values: []int, sentinel: int): int {
    var total = 0
    for val item in values {
        if item == sentinel {
            break
        }
        total += item
    }
    total
}

func nestedArrayBreakIsLocal(outer: [][]int): int {
    var total = 0
    for val row in outer {
        for val item in row {
            if item == 0 {
                break
            }
            total += item
        }
    }
    total
}

func nestedArrayContinueIsLocal(outer: [][]int): int {
    var total = 0
    for val row in outer {
        for val item in row {
            if item < 0 {
                continue
            }
            total += item
        }
    }
    total
}

func nestedReturnExitsBoth(values: [][]int): int {
    for val row in values {
        for val item in row {
            if item > 0 {
                return item
            }
        }
    }
    0
}

// PER-ITERATION CLEANUP
//
// Each body entry creates a fresh lexical iteration scope. A defer reached in that scope is
// registered for that iteration only. It executes once on normal completion, continue, break,
// return, or exceptional exit from that iteration. A skipped defer declaration is not registered.

func deferRunsBeforeNextArrayElement(values: []int): () throws CleanupFailure {
    for val item in values {
        defer { closeIteration(item) }
        if item < 0 {
            continue
        }
        work(item)
    }
}

func deferRunsOnArrayBreak(values: []int): () throws CleanupFailure {
    for val item in values {
        defer { closeIteration(item) }
        if item == 0 {
            break
        }
    }
}

func deferRegistrationIsPathSensitive(values: []int, register: bool): () throws CleanupFailure {
    for val item in values {
        if register {
            defer { closeIteration(item) }
        }
        work(item)
    }
}

func arrayCleanupFailureIsChecked(values: []int): () {
    // Expected error: per-iteration cleanup may throw on any entered iteration.
    for val item in values {
        defer { closeIteration(item) }
        work(item)
    }
}

// CURSOR OPERATIONS
//
// Cursor protocol calls are not intrinsic array instructions. The selected iterator factory is
// evaluated once. `moveNext` is called once per advance attempt. `current` is called only after a
// true result. Its value is copied into the body-local binding before body entry. Any source-level
// side effects are visible in that order.

func cursorOrder(cursor: Iterator<int>): int throws AdvanceFailure, CurrentFailure {
    var sum = 0
    for val item in cursor {
        sum += item
    }
    sum
}

func currentIsNotReadAfterFalse(cursor: Iterator<int>): int throws AdvanceFailure, CurrentFailure {
    var total = 0
    for val item in cursor {
        total += item
        if total < 0 {
            break
        }
    }
    total
}

func iteratorFactoryIsNotRepeated(values: Iterable<int>): int
    throws AcquisitionFailure, AdvanceFailure, CurrentFailure {
    var total = 0
    for val item in values {
        total += item
    }
    total
}

func cursorFactoryFailurePrecedesBody(values: Iterable<int>): ()
    throws AcquisitionFailure, AdvanceFailure, CurrentFailure {
    for val item in values {
        consume(item)
    }
}

func cursorCurrentFailureDoesNotEnterBody(cursor: Iterator<int>): ()
    throws AdvanceFailure, CurrentFailure {
    for val item in cursor {
        consume(item)
    }
}

// RESOURCE CURSOR EXIT RULES
//
// If the selected cursor contract has `close()`, the compiler registers one cursor cleanup after
// successful acquisition. It closes the cursor on normal exhaustion, break, return, and exceptional
// unwind. Acquisition failure creates no cursor cleanup. A failed `moveNext` or `current` still
// closes an already acquired resource cursor.

func closesOnExhaustion(cursor: ResourceIterator<int>): ()
    throws AdvanceFailure, CurrentFailure, CloseFailure {
    for val item in cursor {
        consume(item)
    }
}

func closesOnBreak(cursor: ResourceIterator<int>): ()
    throws AdvanceFailure, CurrentFailure, CloseFailure {
    for val item in cursor {
        if item == 0 {
            break
        }
    }
}

func closesOnReturn(cursor: ResourceIterator<int>): int
    throws AdvanceFailure, CurrentFailure, CloseFailure {
    for val item in cursor {
        return item
    }
    0
}

func closesOnAdvanceFailure(cursor: ResourceIterator<int>): ()
    throws AdvanceFailure, CloseFailure {
    for val item in cursor {
        consume(item)
    }
}

func closesOnCurrentFailure(cursor: ResourceIterator<int>): ()
    throws AdvanceFailure, CurrentFailure, CloseFailure {
    for val item in cursor {
        consume(item)
    }
}

func missingCloseBudget(cursor: ResourceIterator<int>): () {
    // Expected error: close can fail while normal or exceptional loop exit is being processed.
    for val item in cursor {
        consume(item)
    }
}

func closeFailureDuringBodyFailure(cursor: ResourceIterator<int>): ()
    throws AdvanceFailure, CurrentFailure, CloseFailure, IterationFailure {
    for val item in cursor {
        if item < 0 {
            throw IterationFailure()
        }
    }
}

// During an exceptional unwind, the original body/advance/current failure remains primary. A
// failure raised by cursor close is retained according to the language's suppressed-failure rule.
// Cleanup order is inner cursor, outer cursor, then enclosing lexical defer actions in LIFO order.

func nestedResourceCursorsCloseInsideOut(
    outer: ResourceIterator<int>,
    inner: ResourceIterator<int>,
): int throws AdvanceFailure, CurrentFailure, CloseFailure {
    var total = 0
    for val first in outer {
        for val second in inner {
            total += first + second
            if total > 20 {
                break
            }
        }
    }
    total
}

func outerDeferRunsAfterCursorClose(cursor: ResourceIterator<int>): ()
    throws AdvanceFailure, CurrentFailure, CloseFailure, CleanupFailure {
    defer { outerCleanup() }
    for val item in cursor {
        if item > 0 {
            return
        }
    }
}

// REF-LIKE CURSORS
//
// A ref-like cursor remains in a scoped location. The compiler does not box it to call a protocol,
// capture it in an escaping closure, store it in an object field, or return it beyond its lifetime.
// A source route that would require any such operation is rejected or Deferred until a safe lowering
// is proven. GC rooting of an array reference does not grant an arbitrary ref-like value an escape.

ref struct StackCursor {
    val length: int
    var position: int
    func moveNext(): bool = this.position < this.length
    func current(): int = this.position
}

func stackCursorStaysLocal(length: int): int {
    val cursor = StackCursor(length, 0)
    var count = 0
    for val item in cursor {
        count += item
    }
    count
}

func stackCursorCannotBeCaptured(length: int): () {
    val cursor = StackCursor(length, 0)
    val closure = { cursor.moveNext() }
    consume(closure)
}

func stackCursorCannotEscape(length: int): StackCursor {
    val cursor = StackCursor(length, 0)
    for val item in cursor {
        consume(item)
    }
    cursor
}

// GC ROOT AND NATIVE QUALIFICATION RULES
//
// For dynamic arrays, the array reference is a strong root from successful evaluation through the
// final loop test. Reference-typed item values are stored through the guest runtime's traced-store
// path. The loop does not expose an interior element pointer across a body call, collection poll, or
// control transfer. The guest collector is non-moving in the current implementation; the root keeps
// the array object reachable. A future moving collector would need a corresponding relocation rule.

func arrayAndStringItemsRemainReachable(values: []String): () {
    for val item in values {
        allocateMoreStrings(item)
    }
}

func objectElementsRemainReachable(values: []Box): () {
    for val item in values {
        collectGuestHeap()
        consume(item.value)
    }
}

func itemReferenceWriteUsesBarrier(values: []String, replacement: String): () {
    for var item in values {
        item = replacement
    }
}

func elementWriteThroughArrayUsesBarrier(values: []String, replacement: String): () {
    var index = 0
    for val item in values {
        values[index] = replacement
        index += 1
        consume(item)
    }
}

// Native qualification is narrower than the source contract. The first verified dynamic-array
// path accepts bool, sbyte, byte, short, int, uint, long, ulong, single, double, and String elements.
// Fixed arrays, nested arrays, nullable arrays, unsupported element layouts, Vector, user cursor
// calls, close edges, exception cleanup, and ref-like cursor lifetimes need separate qualification.
// A builder failure must report the unsupported source location; it cannot erase the loop body.

func nativeScalarElements(values: []int): int {
    var total = 0
    for val item in values {
        total += item
    }
    total
}

func nativeStringElements(values: []String): int {
    var total = 0
    for val item in values {
        total += item.length
    }
    total
}

func nativeBooleanElements(values: []bool): bool {
    var result = true
    for val item in values {
        result = result && item
    }
    result
}

func nativeUnsupportedFixedArray(values: [3]int): int {
    // The source contract is valid; the first verified dynamic-array node must reject this shape.
    var total = 0
    for val item in values {
        total += item
    }
    total
}

func nativeUnsupportedCursor(values: Iterable<int>): int
    throws AcquisitionFailure, AdvanceFailure, CurrentFailure {
    var total = 0
    for val item in values {
        total += item
    }
    total
}

// Source legality, analysis completeness, verified-FIR admission, and native execution are distinct
// claims. Each compiler phase reports its own accepted subset. No phase infers implementation from
// the presence of an example or from another phase's success.
