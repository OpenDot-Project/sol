// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: empty literals need an expected type and prove a zero-trip body.
module spec.arrayiteration.empty

func explicitEmptyInteger(): int {
    val values: []int = []
    var total = 0
    for val item in values { total += item }
    total
}

func explicitEmptyString(): int {
    val values: []String = []
    var total = 0
    for val item in values { total += item.length }
    total
}

func explicitEmptyNullableElement(): int {
    val values: []String? = []
    var total = 0
    for val item in values {
        if item != null { total += item.length }
    }
    total
}

func emptyExpectedFromDeclaration(): int {
    val values: []long = []
    var total: long = 0
    for val item in values { total += item }
    total
}

func emptyExpectedFromArgument(): int {
    consumeInts([])
    0
}

func emptyExpectedFromConditional(flag: bool): int {
    var values: []int = []
    if flag { values = [] } else { values = [1] }
    var total = 0
    for val item in values { total += item }
    total
}

func emptyExpectedFromReturn(): []int = []

func emptyExpectedFromGenericArgument(): () {
    acceptArray([] as []int)
}

func emptyCannotInferItem(): () {
    // Expected inference error: there is no element type for `item`.
    for val item in [] { consume(item) }
}

func emptyCannotGuessFromBodyOperation(): () {
    // Expected inference error: using a member name in the body does not infer the source element.
    for val item in [] { consume(item.length) }
}

func emptyCannotGuessFromAssignmentTarget(): () {
    var target: int
    for val item in [] { target = item }
}

func emptyCannotGuessFromUnreachableEffect(): () {
    // The effect body does not supply the array element type because it cannot run.
    for val item in [] { failForValue(item) }
}

func emptyTypedBodyIsNotExecuted(): () {
    val values: []int = []
    for val item in values { recordBodyEntry(item) }
}

func emptyTypedBodyStillParses(): () {
    val values: []int = []
    for val item in values {
        if true { consume(item) } else { consume(0) }
    }
}

func emptyTypedBodyStillChecksNames(): () {
    val values: []int = []
    for val item in values {
        // Expected name-resolution error even though the path cannot execute.
        unresolved(item)
    }
}

func emptyTypedBodyStillChecksTypes(): () {
    val values: []int = []
    for val item in values {
        // Expected type error; unreachable does not change operator typing.
        val invalid: bool = item + "text"
    }
}

func emptyTypedBodyStillChecksMutability(): () {
    val values: []int = []
    for val item in values {
        // Expected assignment error for immutable item.
        item = 7
    }
}

func emptyTypedBodyHasNoEscapingThrow(): () {
    val values: []int = []
    for val item in values {
        throw Failure("unreachable")
    }
}

func emptyTypedBodyHasNoEscapingCallEffect(): () {
    val values: []int = []
    for val item in values {
        mightThrow(item)
    }
}

func emptyTypedBodyHasNoCleanupEffect(): () {
    val values: []int = []
    for val item in values {
        defer { cleanupMayThrow() }
        consume(item)
    }
}

func expressionBeforeEmptyLoopCanThrow(): () throws Failure {
    val values: []int = buildEmptyArray()
    for val item in values { consume(item) }
}

func sourceCallReturningEmptyCanThrow(): () throws Failure {
    for val item in emptyArrayOrThrow() { consume(item) }
}

func bodyEffectRemainsForUnknownArray(values: []int): () throws Failure {
    for val item in values { mightThrow(item) }
}

func parameterMayBeEmptyOrNonEmpty(values: []int): () throws Failure {
    for val item in values { mightThrow(item) }
}

func emptyAfterRuntimeBranch(flag: bool): () throws Failure {
    var values: []int = []
    if flag { values = [] } else { values = [1] }
    for val item in values { mightThrow(item) }
}

func emptyAfterCallCannotBeProved(): () throws Failure {
    val values: []int = chooseEmptyOrPopulatedArray()
    for val item in values { mightThrow(item) }
}

func emptyAfterPropertyCannotBeProved(owner: EmptyOrPopulated): () throws Failure {
    for val item in owner.values { mightThrow(item) }
}

func emptyInsideNestedLoopIsStillZeroTrip(outer: []int): int {
    val inner: []int = []
    var result = 0
    for val first in outer {
        for val second in inner { result += first + second }
    }
    result
}

func nonEmptyLiteralExecutesPerElement(): int {
    var result = 0
    for val item in [3, 4] { result += item }
    result
}

func literalShapeSelectedByBranch(flag: bool): int {
    var values: []int = []
    if flag { values = [1] } else { values = [0] }
    var result = 0
    for val item in values { result += item }
    result
}

func literalElementEffectsRunBeforeTraversal(): int throws Failure {
    var result = 0
    for val item in [elementOrThrow(1), elementOrThrow(2)] { result += item }
    result
}

func emptyNestedLiteralNeedsElementContext(): [][]int = [[], []]

func emptyNestedLiteralFromArrayType(): int {
    val rows: [][]int = [[], []]
    var result = 0
    for val row in rows {
        for val item in row { result += item }
    }
    result
}

class EmptyOrPopulated { val values: []int }
class Failure: Exception {}

// Only a statically typed empty literal is the zero-trip proof in this matrix. A runtime fact that
// an arbitrary array happens to be empty is not guessed by source analysis or FIR verification.
