// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative diagnostic fixture. Effect origins remain attached to selected source operations.
module spec.effects.effectprovenance

class FirstFailure: Exception {}
class SecondFailure: Exception {}
class ThirdFailure: Exception {}

func first(): int throws FirstFailure { 1 }
func second(): int throws SecondFailure { 2 }
func third(): int throws ThirdFailure { 3 }

func noBudget(): int {
    first()
}

func severalMissing(): int {
    first()
    second()
    third()
    0
}

func deterministicUnion(): int throws FirstFailure, SecondFailure, ThirdFailure {
    third()
    first()
    second()
    0
}

func handledOriginIsRemoved(): int throws SecondFailure, ThirdFailure {
    try {
        first()
        second()
        third()
    } catch (failure: FirstFailure) {
        0
    }
}

func handlerOriginIsPreserved(): int throws SecondFailure {
    try {
        first()
    } catch (failure: FirstFailure) {
        second()
    }
}

func twoOriginsSameTypeAreOneBudgetMember(): int throws FirstFailure {
    first()
    first()
    0
}

func subtypeOriginCoveredByParent(): int throws Exception {
    first()
}

func siblingOriginNotCovered(): int throws SecondFailure {
    first()
}

func ambiguousEffectOrigin(value: int): int {
    // If overload resolution is ambiguous, no candidate's effects may be reported as if that
    // candidate had been selected. The call remains invalid or deferred.
    select(value)
}

func select(value: int): int throws FirstFailure { value }
func select(value: long): int throws SecondFailure { 0 }

func rejectedCandidateDoesNotLeak(value: int): int throws FirstFailure {
    select(value)
}

func defaultOriginIsAtCallSite(): int {
    // The diagnostic points at the omitted argument/call while retaining the declaration span
    // of the default expression as related provenance.
    withDefault()
}

func withDefault(value: int = third()): int { value }

// Diagnostics preserve operation and declaration origins. Candidate-local failures do not
// contaminate the selected candidate. Effect-set ordering is deterministic for display, but
// source order does not choose a contract or change set membership.
