// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative recovery fixture. Unresolved effect evidence remains explicit and candidate-local.
module spec.effects.deferredcandidates

class FirstFailure: Exception {}
class SecondFailure: Exception {}

func first(): int throws FirstFailure { 1 }
func second(): int throws SecondFailure { 2 }

func choose(value: int, action: () -> int throws FirstFailure): int throws FirstFailure {
    action()
}

func choose(value: String, action: () -> int throws SecondFailure): int throws SecondFailure {
    action()
}

func selectedCandidateHasOneEffect(): int throws FirstFailure {
    choose(1) { first() }
}

func selectedCandidateDoesNotUnionRejectedEffects(): int throws FirstFailure {
    choose(1) { first() }
}

func candidateLambdaEffectRejectsMismatch(): int throws FirstFailure {
    // Expected: the String candidate's callback budget cannot accept second's unrelated effect.
    choose("text") { second() }
}

func overloadSelectionDoesNotUseDeclaredEffects(): int throws FirstFailure {
    // The throws set is checked after applicability/ranking; it cannot be used to prefer a
    // candidate whose argument conversions are otherwise worse.
    choose(1, { first() })
}

func unresolvedCallIsNotAssumedPure(value: bool): int {
    choose(value)
}

func recursionBudgetIsVisible(depth: int): int throws FirstFailure {
    if depth == 0 {
        return first()
    }
    recursive(depth - 1)
}

func recursive(depth: int): int throws FirstFailure {
    recursionBudgetIsVisible(depth)
}

func <E: Throwable> pendingOpenEffectPattern(action: () -> int throws E): int throws E {
    action()
}

func incompleteInferenceRemainsPending(): int {
    // Expected: when a generic effect pattern lacks enough evidence, the call is Deferred rather
    // than reported as empty or resolved by source order.
    pendingOpenEffectPattern({ 1 })
}

// Candidate outcomes are Applicable, Rejected, Ambiguous, Deferred, or ResourceLimited. Each
// candidate owns its generic effect substitution and lambda analysis until selection. Rejected
// facts are discarded; deferred facts remain visible without being promoted to successful
// analysis.
