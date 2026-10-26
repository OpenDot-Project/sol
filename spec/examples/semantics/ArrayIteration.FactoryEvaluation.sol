// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative future contract: cursor factory and iterable expression have explicit evaluation points.
module spec.arrayiteration.factoryevaluation

class FactoryFailure: Exception {}
class AdvanceFailure: Exception {}
class ValueFailure: Exception {}

interface Cursor<T> {
    func moveNext(): bool throws AdvanceFailure
    func current(): T throws ValueFailure
}

interface Source<T> {
    func iterator(): Cursor<T> throws FactoryFailure
}

func sourceExpressionBeforeFactory(source: Source<int>): int
    throws FactoryFailure, AdvanceFailure, ValueFailure {
    var total = 0
    for val item in selectSource(source) { total += item }
    total
}

func factoryCalledOnce(source: Source<int>): int
    throws FactoryFailure, AdvanceFailure, ValueFailure {
    var total = 0
    for val item in countedSourceFactory(source) { total += item }
    total
}

func factoryIsNotInsideBackedge(source: Source<int>): int
    throws FactoryFailure, AdvanceFailure, ValueFailure {
    var total = 0
    for val item in source { total += item }
    total
}

func factoryFailurePreventsFirstAdvance(source: Source<int>): () throws FactoryFailure {
    for val item in source { consume(item) }
}

func factoryReceiverIsSavedBeforeBody(source: Source<int>): ()
    throws FactoryFailure, AdvanceFailure, ValueFailure {
    for val item in rebindSourceDuringBody(source) { consume(item) }
}

func factoryResultIsTheTraversedCursor(source: Source<int>): int
    throws FactoryFailure, AdvanceFailure, ValueFailure {
    var total = 0
    for val item in source { total += item }
    total
}

func factoryCallSideEffectPrecedesFirstMoveNext(source: Source<int>): ()
    throws FactoryFailure, AdvanceFailure, ValueFailure {
    for val item in source { observeFactoryOrder(item) }
}

func factoryCanReturnEmptyCursor(source: Source<int>): int
    throws FactoryFailure, AdvanceFailure, ValueFailure {
    var count = 0
    for val item in emptyCursorSource(source) { count += 1 }
    count
}

func emptyCursorFactoryStillRuns(source: Source<int>): int throws FactoryFailure {
    for val item in emptyCursorFactory(source) { consume(item) }
    factoryCount()
}

func directCursorSkipsFactory(source: Cursor<int>): int
    throws AdvanceFailure, ValueFailure {
    var count = 0
    for val item in source { count += item }
    count
}

func directCursorDoesNotCallIteratorMethod(source: DirectCursor<int>): int
    throws AdvanceFailure, ValueFailure {
    var count = 0
    for val item in source { count += item }
    count
}

func factoryResultTypeDeterminesCurrentType(source: Source<String>): int
    throws FactoryFailure, AdvanceFailure, ValueFailure {
    var total = 0
    for val item in source { total += item.length }
    total
}

func factoryGenericSubstitutionIsApplied(source: Source<int>): int
    throws FactoryFailure, AdvanceFailure, ValueFailure {
    var total = 0
    for val item in source { total += item }
    total
}

func factoryEffectDoesNotBecomeAdvanceEffect(source: Source<int>): ()
    throws FactoryFailure, AdvanceFailure, ValueFailure {
    for val item in source { consume(item) }
}

func advanceEffectDoesNotBecomeFactoryEffect(source: Source<int>): ()
    throws FactoryFailure, AdvanceFailure, ValueFailure {
    for val item in source { consume(item) }
}

func nestedFactoryRunsAfterOuterItem(outer: Source<int>, inner: Source<int>): int
    throws FactoryFailure, AdvanceFailure, ValueFailure {
    var total = 0
    for val first in outer {
        for val second in chooseInner(inner, first) { total += first + second }
    }
    total
}

func innerFactoryDoesNotRunForEmptyOuter(outer: Source<int>, inner: Source<int>): int
    throws FactoryFailure, AdvanceFailure, ValueFailure {
    var count = 0
    for val first in emptyOuterSource(outer) {
        for val second in chooseInner(inner, first) { count += second }
    }
    count
}

func factoryEvaluationOrderWithNamedArgs(first: Source<int>, second: Source<int>): ()
    throws FactoryFailure, AdvanceFailure, ValueFailure {
    for val item in chooseSource(left = markSource(first), right = markSource(second)) {
        consume(item)
    }
}

func sourceLocalAssignmentAfterFactoryDoesNotReplaceCursor(source: Source<int>): int
    throws FactoryFailure, AdvanceFailure, ValueFailure {
    var active = source
    var total = 0
    for val item in active {
        active = emptySource()
        total += item
    }
    total
}

// The source expression, iterator acquisition, advance, and current-value call are separate
// evaluation steps. Factory effects happen once; advance/current effects may recur per element.
