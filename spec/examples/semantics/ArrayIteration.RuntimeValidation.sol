// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Runtime contract: validated array headers and exact strides guard native element access.
module spec.arrayiteration.runtimevalidation

func validArrayLength(values: []int): int {
    var count = 0
    for val item in values { count += 1 }
    count
}

func nullArrayIsOutsideNonNullType(values: []int): int {
    var count = 0
    for val item in values { count += 1 }
    count
}

func nullableArrayMustBeChecked(values: []int?): int {
    if values == null { return 0 }
    var count = 0
    for val item in values { count += 1 }
    count
}

func corruptLengthIsRuntimeFailure(values: []int): int {
    // A forged negative length is invalid runtime state, not a zero-trip source array.
    var total = 0
    for val item in values { total += item }
    total
}

func oversizedLengthIsRejectedAtConstruction(): []int {
    allocateArrayWithLength(int.maxValue + 1)
}

func corruptStrideIsRuntimeFailure(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func descriptorKindMustMatchSourceElement(values: []String): int {
    var total = 0
    for val item in values { total += item.length }
    total
}

func addressRoutineChecksExpectedStride(values: []long): long {
    var total: long = 0
    for val item in values { total += item }
    total
}

func loadCannotReadPastLength(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func bodyExplicitOutOfRangeLoadStillFails(values: []int): int {
    for val item in values { return values[values.length] }
    0
}

func integerIndexIsSignedAndNonNegative(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func lastIncrementDoesNotOverflowAtMaximumLength(values: []int): int {
    var count = 0
    for val item in values { count += 1 }
    count
}

func referenceElementLoadUsesPointerWidth(values: []String): int {
    var total = 0
    for val item in values { total += item.length }
    total
}

func scalarElementWidthIsNotPointerWidth(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func misalignedAddressIsNotAccepted(values: []long): long {
    var total: long = 0
    for val item in values { total += item }
    total
}

func corruptReferenceSlotIsRejected(values: []Cell): int {
    var total = 0
    for val item in values { total += item.number }
    total
}

func runtimeValidationDoesNotDiscoverATypeFromData(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func typeIdentityControlsElementKind(values: []byte): int {
    var total = 0
    for val item in values { total += item }
    total
}

func invalidHeaderCannotTurnArrayIntoCursor(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func runtimeFailureCannotResumeWithDifferentStride(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

class Cell { val number: int }

// The source type selects the element kind at compile time. Runtime checks validate a value against
// that kind; they do not choose a new lowering based on a corrupt or unexpected header.
