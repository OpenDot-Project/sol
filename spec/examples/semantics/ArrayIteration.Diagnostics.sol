// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: diagnostics identify the unresolved or invalid source obligation precisely.
module spec.arrayiteration.diagnostics

func unresolvedIterable(values: UnknownSource): () {
    // Expected source-binding diagnostic at the iterable type.
    for val item in values { consume(item) }
}

func unresolvedItemType(values: []UnknownType): () {
    // Expected element-type diagnostic before body binding succeeds.
    for val item in values { consume(item) }
}

func loopItemTypeMustMatchElement(values: []int): () {
    for val item in values {
        // Expected ordinary assignment type error; the iterator element type is int.
        val invalid: String = item
        consume(invalid)
    }
}

func underscoreDiscardsItem(values: []int): int {
    var count = 0
    for val _ in values { count += 1 }
    count
}

// Keep parser-invalid surface forms in comments so this corpus remains parseable. For example,
// `for val (left, right) in values { consume(left, right) }` is rejected by the current parser.
func nativeItemSlotHasOneBinding(values: []int): () {
    // The parsed form has one source binding. Other well-formed source shapes outside the current
    // FIR gate receive a subset diagnostic rather than an empty or partially lowered loop.
    for val item in values { consume(item) }
}

func assignmentToValItem(values: []int): () {
    for val item in values { item = 0 }
}

func loopControlOutsideLoop(): () {
    // Expected control-flow diagnostic: continue has no active loop.
    continue
}

func breakOutsideLoop(): () {
    // Expected control-flow diagnostic: break has no active loop.
    break
}

func wrongCursorAdvanceType(cursor: InvalidCursor): () {
    // Expected cursor-protocol diagnostic rather than a native array fallback.
    for val item in cursor { consume(item) }
}

func ambiguousCursorMethod(cursor: AmbiguousCursor): () {
    // Expected overload-resolution diagnostic before effect propagation.
    for val item in cursor { consume(item) }
}

func privateCursorMethod(cursor: PrivateCursor): () {
    // Expected access diagnostic; a same-spelled inaccessible member is not callable.
    for val item in cursor { consume(item) }
}

func unsupportedNativeCursor(cursor: UserCursor<int>): () {
    // Source contract can resolve this shape; the current FIR gate reports SOL2400.
    for val item in cursor { consume(item) }
}

func unsupportedNativeFixedArray(values: [3]int): () {
    // Source contract can resolve this shape; the current FIR gate is dynamic-array-only.
    for val item in values { consume(item) }
}

func unsupportedNativeNestedArray(values: [][]int): () {
    // The closed native element layout excludes nested managed arrays.
    for val row in values { consume(row) }
}

func noUnsupportedLoopErasure(values: ResourceIterable<int>): () {
    // On failure, the compiler reports the exact loop origin and emits no successful empty-body FIR.
    for val item in values { consume(item) }
}

func unsupportedBodyOperationIsNotAnEmptyLoop(values: []int): () {
    // A supported source type does not make an unsupported body call eligible for native emission.
    for val item in values { unsupportedNativeCall(item) }
}

func noElementInferenceFromUnreachableBody(): () {
    // Expected inference diagnostic: the body cannot invent the element type of [] .
    for val item in [] { consumeInt(item) }
}

func noRuntimeTypeGuessing(values: MaybeArrayOrCursor): () {
    // Expected unresolved/ambiguous source contract; native lowering cannot inspect runtime shape.
    for val item in values { consume(item) }
}

func sourceErrorOriginIsIterable(values: UnknownSource): () {
    for val item in values { consume(item) }
}

func bodyErrorOriginIsSpecificStatement(values: []int): () {
    for val item in values {
        consume(item)
        // Expected error at this reference, not a generic error at the enclosing loop.
        unresolvedName
    }
}

func resourceLimitDefersRatherThanGuessing(values: []int): () {
    for val item in values { deeplyNestedAnalysis(item) }
}

func cancellationDoesNotPublishPartialFir(values: []int): () {
    for val item in values { consume(item) }
}

func duplicateDeclarationDoesNotForgeItemSlot(values: []int): () {
    for val item in values { consume(item) }
}

func wrongItemOriginDoesNotVerify(values: []int): () {
    for val item in values { consume(item) }
}

func wrongItemTypeDoesNotVerify(values: []int): () {
    for val item in values { consume(item) }
}

func unsupportedCursorDoesNotMintVerifiedCapability(values: UserCursor<int>): () {
    for val item in values { consume(item) }
}

// Diagnostics distinguish parse errors, name/member binding, source flow, FIR subset rejection,
// FIR tampering, and native runtime failures. A failure at one layer does not become success at the
// next layer by discarding the unsupported node.
