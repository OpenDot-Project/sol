// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: exact cursor callables contribute independently substituted effect sets.
module spec.arrayiteration.cursoreffects

class AcquireFailure: Exception {}
class AdvanceFailure: Exception {}
class ValueFailure: Exception {}
class CloseFailure: Exception {}

interface Cursor<T, A, V> {
    func moveNext(): bool throws A
    func current(): T throws V
}

interface Source<T, A, V> {
    func iterator(): Cursor<T, A, V> throws AcquireFailure
}

func allThreeEffects(source: Source<int, AdvanceFailure, ValueFailure>): int
    throws AcquireFailure, AdvanceFailure, ValueFailure {
    var result = 0
    for val item in source { result += item }
    result
}

func acquisitionEffectMissing(source: Source<int, AdvanceFailure, ValueFailure>): int
    throws AdvanceFailure, ValueFailure {
    // Expected error: iterator() can fail before the first advance.
    for val item in source { consume(item) }
    0
}

func advanceEffectMissing(source: Source<int, AdvanceFailure, ValueFailure>): int
    throws AcquireFailure, ValueFailure {
    // Expected error: moveNext() is called at least once after acquisition succeeds.
    for val item in source { consume(item) }
    0
}

func currentEffectMissing(source: Source<int, AdvanceFailure, ValueFailure>): int
    throws AcquireFailure, AdvanceFailure {
    // Expected error: current() may fail on a true advance edge.
    for val item in source { consume(item) }
    0
}

func effectDoesNotRequireCurrentWhenAdvanceFalse(source: Source<int, AdvanceFailure, ValueFailure>): ()
    throws AcquireFailure, AdvanceFailure {
    for val item in source { consume(item) }
}

func effectStillPossibleForUnknownCursor(source: Source<int, AdvanceFailure, ValueFailure>): ()
    throws AcquireFailure, AdvanceFailure, ValueFailure {
    for val item in source { consume(item) }
}

func <T, A: Throwable, V: Throwable> genericEffectsSubstituteAtCall(source: Source<T, A, V>): ()
    throws AcquireFailure, A, V {
    for val item in source { consume(item) }
}

func <T, A: Throwable, V: Throwable> genericEffectMissingAtCall(source: Source<T, A, V>): ()
    throws AcquireFailure, A {
    // Expected error: V is not included in the caller budget.
    for val item in source { consume(item) }
}

func sameTypeDifferentMethodEffects(source: SplitEffectSource): ()
    throws AcquireFailure, AdvanceFailure, ValueFailure {
    for val item in source { consume(item) }
}

func baseInterfaceBudgetIsPreserved(source: BaseEffectSource): () throws AcquireFailure {
    // A selected derived implementation cannot widen the static base operation's effect set.
    for val item in source { consume(item) }
}

func overloadSelectionDeterminesEffect(source: OverloadedSource): () throws AcquireFailure {
    // The selected iterator overload is recorded before effect checking; the compiler does not
    // union arbitrary same-name overloads or defer the decision to runtime.
    for val item in source { consume(item) }
}

func inaccessibleOverloadDoesNotContribute(source: VisibilitySource): () throws AcquireFailure {
    // An inaccessible overload is not an applicable route and cannot hide an accessible failure.
    for val item in source { consume(item) }
}

func sourceExpressionEffectAndAcquisitionEffectAreDistinct(source: Source<int, AdvanceFailure, ValueFailure>): ()
    throws SourceFailure, AcquireFailure, AdvanceFailure, ValueFailure {
    for val item in chooseSourceOrFail(source) { consume(item) }
}

func currentEffectOnlyOnSuccessfulAdvance(source: Source<int, AdvanceFailure, ValueFailure>): ()
    throws AcquireFailure, AdvanceFailure, ValueFailure {
    for val item in source {
        if item < 0 { break }
    }
}

func closeEffectAddedToResourceSource(source: ResourceSource<int>): ()
    throws AcquireFailure, AdvanceFailure, ValueFailure, CloseFailure {
    for val item in source { consume(item) }
}

func closeEffectMissingOnEarlyReturn(source: ResourceSource<int>): int
    throws AcquireFailure, AdvanceFailure, ValueFailure {
    // Expected error: return still closes the acquired cursor.
    for val item in source { return item }
    0
}

func closeEffectMissingOnBreak(source: ResourceSource<int>): ()
    throws AcquireFailure, AdvanceFailure, ValueFailure {
    // Expected error: break closes the acquired resource cursor.
    for val item in source { break }
}

func closeNotRequiredForOrdinaryIterator(cursor: OrdinaryIterator<int>): ()
    throws AdvanceFailure, ValueFailure {
    for val item in cursor { consume(item) }
}

func declaredUncheckedEffectIsNotACheckedRequirement(cursor: UncheckedCursor<int>): () {
    for val item in cursor { consume(item) }
}

func checkedSubtypeCanBeHandledAtLoopBoundary(source: Source<int, AdvanceFailure, ValueFailure>): int {
    try {
        for val item in source { consume(item) }
    } catch (error: ValueFailure) {
        return -1
    } catch (error: AdvanceFailure) {
        return -2
    } catch (error: AcquireFailure) {
        return -3
    }
    0
}

func catchOnlyCurrentFailureDoesNotCatchFactoryFailure(source: Source<int, AdvanceFailure, ValueFailure>): ()
    throws AcquireFailure, AdvanceFailure {
    try {
        for val item in source { consume(item) }
    } catch (error: ValueFailure) {
        consume(error)
    }
}

func emptyArrayBodyEffectsDoNotNeedCursorRules(): () {
    val empty: []int = []
    for val item in empty { noEffect(item) }
}

func cursorEffectBudgetIsNotArrayIndexEffect(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

// Acquisition, advance, current, and close are separate declarations and separate exceptional
// edges. Generic substitutions preserve each callable's identity; they do not flatten the protocol
// into one fabricated `Iterator<T>` effect signature.
