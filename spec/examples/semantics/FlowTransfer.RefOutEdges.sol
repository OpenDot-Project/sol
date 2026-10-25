// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative ref/out flow fixture. Location evaluation and whole-call semantics remain planned.
module spec.flow.refoutedges

func setBoth(out first: int, out second: int): () {
    first = 1
    second = 2
}

func missSecond(flag: bool, out first: int, out second: int): () {
    first = 1
    if flag {
        second = 2
        return
    }
    // Expected: second is unassigned on this normal exit.
}

func readBeforeWrite(out value: int): int {
    // Expected: an out parameter cannot be read before assignment.
    val previous = value
    value = previous + 1
    value
}

func readRef(ref value: int): int {
    // Positive: ref is assigned on entry.
    value
}

func aliasSameLocation(): int {
    var number = 0
    setPair(ref number, ref number)
    number
}

func setPair(ref first: int, ref second: int): () {
    first = 1
    second = first + 1
}

func callOutEstablishesAssignment(): int {
    var value: int
    setBoth(out value, out var other)
    value + other
}

func callOutNeedsNormalReturn(): int throws Failure {
    var value: int
    try {
        assignThenThrow(out value)
    } catch (failure: Failure) {
        // Expected: absent a stronger out-effect contract, do not assume completion.
        return value
    }
    value
}

class Failure: RuntimeException {}

func assignThenThrow(out value: int): () throws Failure {
    value = 1
    throw Failure()
}

func conditionalOutIsNotDefinite(flag: bool): int {
    var value: int
    flag && writeTrue(out value)
    // Expected: short-circuit false skips the out call.
    value
}

func writeTrue(out value: int): bool {
    value = 1
    true
}

func safeCallOutIsConditional(writer: Writer?): int {
    var value: int
    writer?.fill(out value)
    // Expected: safe invocation may be skipped.
    value
}

class Writer {
    func fill(out value: int): () {
        value = 9
    }
}

func namedRefOutArguments(): int {
    var left = 1
    var right: int
    update(left = ref left, right = out right)
    left + right
}

func update(ref left: int, out right: int): () {
    left += 1
    right = left
}

func indexLocationEvaluatesOnce(items: []int): int {
    var index = 0
    update(ref items[nextIndex(ref index)], out items[nextIndex(ref index)])
    items[0]
}

func nextIndex(ref index: int): int {
    index += 1
    index - 1
}

// The two calls above are separate source arguments. Each argument expression runs once,
// left-to-right, and each chosen location is retained for the actual call. A verifier must
// distinguish the two different computed indexes instead of re-evaluating one after the call.
// Ref/out requires exact type and writable storage; conversions, copies, ordinary properties,
// and escaping captured locations do not qualify.

func valCannotBeRef(): () {
    val immutable = 1
    // Expected: readonly storage is rejected for ref.
    readRef(ref immutable)
}

func propertyCannotBeOut(box: Box): () {
    // Expected: an ordinary property is not writable caller storage.
    fill(out box.value)
}

func fill(out value: int): () {
    value = 1
}

class Box {
    var value: int = 0
}

func finallyAssignsPendingOut(out value: int): () {
    try {
        return
    } finally {
        value = 1
    }
}

// Definite-assignment diagnostics refer to the failed read, call edge, or normal exit. Throw
// paths do not impose out assignment on the caller; they also cannot be used to make an invalid
// normal return appear assigned.
