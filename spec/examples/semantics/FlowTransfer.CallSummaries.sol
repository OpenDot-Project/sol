// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative call-transfer fixture. Passing mode and capture facts determine invalidation.
module spec.flow.callsummaries

class Item {
    val length: int = 1
}

func replaceRef(ref item: Item?): () {
    item = null
}

func replaceOut(out item: Item?): () {
    item = Item()
}

func preserveValue(item: Item): Item {
    item
}

func refInvalidatesFact(item: Item?): int {
    var local = item
    if local == null {
        return 0
    }
    replaceRef(ref local)
    // Expected: ref exposes writable storage; the old non-null fact is dead.
    local.length
}

func outWritesNullableType(): int {
    var local: Item?
    replaceOut(out local)
    // Assigned does not mean non-null when the out type is nullable.
    local.length
}

func valueArgumentDoesNotRebindCaller(item: Item?): int {
    var local = item
    if local == null {
        return 0
    }
    val copied = preserveValue(local)
    // Expected: a by-value parameter cannot rebind the caller's local slot.
    local.length + copied.length
}

func closureMayWriteLocal(item: Item?): int {
    var local = item
    val write = { local = null }
    if local == null {
        return 0
    }
    invoke(write)
    // Expected: an escaping or invoked writer closure can mutate the captured cell.
    local.length
}

func invoke(action: () -> ()): () {
    action()
}

func callBeforeRefinement(item: Item?): int {
    var local = item
    replaceRef(ref local)
    if local == null {
        return 0
    }
    local.length
}

func unknownCallAndUncapturedLocal(item: Item?): int {
    var local = item
    if local == null {
        return 0
    }
    noCapture()
    // Whether the call invalidates local is derived from resolved capture/alias metadata, not
    // from the fact that a call occurred somewhere in the function.
    local.length
}

func noCapture(): () {}

func safeRefCallIsConditional(item: Holder?): int {
    var local: Item?
    item?.replace(out local)
    // Expected: the call may be skipped. On its executed edge, local receives a nullable value.
    local.length
}

class Holder {
    func replace(out item: Item?): () {
        item = Item()
    }
}

func exceptionalRefCallHasTwoStates(item: Item?): int throws Failure {
    var local = item
    if local == null {
        return 0
    }
    try {
        mutateThenThrow(ref local)
    } catch (failure: Failure) {
        // The call may mutate local before throwing. Unless the call contract proves otherwise,
        // the catch edge cannot reuse the pre-call non-null fact.
        return local.length
    }
    local?.length ?: 0
}

class Failure: RuntimeException {}

func mutateThenThrow(ref item: Item?): () throws Failure {
    item = null
    throw Failure()
}

// Effect/capture summaries are associated with resolved call targets. If a callable is
// unresolved or its alias effects are incomplete, mutable facts are invalidated conservatively
// or the analysis remains Deferred; absence of a known write is not proof of purity.
