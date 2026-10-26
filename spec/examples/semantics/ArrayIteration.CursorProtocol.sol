// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative future cursor contract: resolve exact operations and preserve their call order.
module spec.arrayiteration.cursorprotocol

class FactoryFailure: Exception {}
class AdvanceFailure: Exception {}
class ReadFailure: Exception {}

interface Cursor<T> {
    func moveNext(): bool throws AdvanceFailure
    func current(): T throws ReadFailure
}

interface Source<T> {
    func iterator(): Cursor<T> throws FactoryFailure
}

func interfaceSource(source: Source<int>): int
    throws FactoryFailure, AdvanceFailure, ReadFailure {
    var total = 0
    for val item in source { total += item }
    total
}

func directCursor(cursor: Cursor<int>): int throws AdvanceFailure, ReadFailure {
    var total = 0
    for val item in cursor { total += item }
    total
}

class StructuralCursor<T> {
    var index: int
    val values: []T
    func moveNext(): bool { index < values.length }
    func current(): T { values[index] }
}

class StructuralSource<T> {
    val values: []T
    func iterator(): StructuralCursor<T> { StructuralCursor(0, values) }
}

func structuralPattern(source: StructuralSource<int>): int {
    var total = 0
    for val item in source { total += item }
    total
}

func iteratorMethodIsSelectedOnce(source: Source<int>): int
    throws FactoryFailure, AdvanceFailure, ReadFailure {
    for val item in source { consume(item) }
    0
}

func iteratorFactoryIsNotCalledForEachElement(source: Source<int>): int
    throws FactoryFailure, AdvanceFailure, ReadFailure {
    var count = 0
    for val item in source { count += 1 }
    count
}

func moveNextPrecedesCurrent(cursor: Cursor<int>): int throws AdvanceFailure, ReadFailure {
    var total = 0
    for val item in cursor {
        traceCurrent(item)
        total += item
    }
    total
}

func currentOnlyRunsAfterTrue(cursor: Cursor<int>): int throws AdvanceFailure, ReadFailure {
    var count = 0
    for val item in cursor { count += item }
    count
}

func currentIsNotCalledAfterFalse(cursor: Cursor<int>): () throws AdvanceFailure, ReadFailure {
    for val item in cursor { consume(item) }
}

func bodyStartsAfterSuccessfulCurrent(cursor: Cursor<int>): int throws AdvanceFailure, ReadFailure {
    var bodyCount = 0
    for val item in cursor {
        bodyCount += 1
        consume(item)
    }
    bodyCount
}

func currentValueIsCopiedToItem(cursor: Cursor<String>): int throws AdvanceFailure, ReadFailure {
    var total = 0
    for val item in cursor { total += item.length }
    total
}

func currentCallEffectsRemainSeparate(cursor: Cursor<int>): () throws AdvanceFailure, ReadFailure {
    for val item in cursor { consume(item) }
}

func missingAdvanceBudget(cursor: Cursor<int>): () throws ReadFailure {
    // Expected checked-effect error: moveNext can fail before the body is entered.
    for val item in cursor { consume(item) }
}

func missingCurrentBudget(cursor: Cursor<int>): () throws AdvanceFailure {
    // Expected checked-effect error: current can fail after moveNext returns true.
    for val item in cursor { consume(item) }
}

func acquisitionFailurePreventsAdvance(source: Source<int>): () throws FactoryFailure {
    for val item in source { consume(item) }
}

func advanceFailurePreventsCurrentAndBody(cursor: Cursor<int>): () throws AdvanceFailure {
    for val item in cursor { consume(item) }
}

func currentFailurePreventsBody(cursor: Cursor<int>): () throws ReadFailure {
    for val item in cursor { consume(item) }
}

func bodyFailureOccursAfterItemAssignment(cursor: Cursor<int>): () throws AdvanceFailure, ReadFailure {
    for val item in cursor { failUsing(item) }
}

func sourceExpressionRunsBeforeIteratorFactory(source: Source<int>): ()
    throws FactoryFailure, AdvanceFailure, ReadFailure {
    for val item in identity(source) { consume(item) }
}

func receiverIsEvaluatedBeforeFactory(source: Source<int>): ()
    throws FactoryFailure, AdvanceFailure, ReadFailure {
    for val item in selectSource(source) { consume(item) }
}

func nestedProtocolHasIndependentCursorState(outer: Source<int>, inner: Source<int>): int
    throws FactoryFailure, AdvanceFailure, ReadFailure {
    var total = 0
    for val first in outer {
        for val second in inner { total += first + second }
    }
    total
}

func methodPatternMustHaveBooleanAdvance(): () {
    val wrong = WrongAdvanceCursor()
    // Expected protocol-resolution error: moveNext must return bool.
    for val item in wrong { consume(item) }
}

func methodPatternMustHaveCurrentValue(): () {
    val wrong = NoCurrentCursor()
    // Expected protocol-resolution error: current must produce the item type.
    for val item in wrong { consume(item) }
}

func missingIteratorMethodIsNotIndexedFallback(source: MissingIterator): () {
    // Expected resolution error; a length/index pair is not an implicit protocol substitute.
    for val item in source { consume(item) }
}

func overloadedIteratorMustResolveUniquely(source: AmbiguousIteratorSource): () {
    // Expected ambiguity unless ordinary overload resolution selects one unique cursor result.
    for val item in source { consume(item) }
}

func inaccessibleIteratorCannotBeSelected(source: HiddenIteratorSource): () {
    // Expected visibility error: inaccessible protocol members are not found by backend guessing.
    for val item in source { consume(item) }
}

func staticCursorTypeControlsProtocol(source: BaseSource): () {
    // Dispatch follows the statically selected interface/member contract.
    for val item in source { consume(item) }
}

func genericCursorEffectsAreSubstituted<T, E: Throwable>(source: GenericSource<T, E>): ()
    throws E {
    for val item in source { consume(item) }
}

func protocolEffectSetIsNotWidened(source: NarrowSource): () throws FactoryFailure {
    // A selected implementation cannot silently widen an interface's checked-effect contract.
    for val item in source { consume(item) }
}

func normalIteratorHasNoSyntheticClose(cursor: Cursor<int>): () throws AdvanceFailure, ReadFailure {
    for val item in cursor { consume(item) }
}

// Iterable and cursor method selection must be represented in analysis/FIR by exact callable and
// receiver identities. User method names, overload order, and effects are never rediscovered from
// a native object at runtime. This fixture specifies source behavior beyond the current native gate.
