// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative candidate-effect fixture. Only a selected callable contributes effects.
module spec.effects.candidateeffects

class IntegerFailure: Exception {}
class TextFailure: Exception {}
class WideFailure: Exception {}

func choose(value: int): int throws IntegerFailure { value }
func choose(value: String): int throws TextFailure { value.length }
func choose(value: FirstReference?): int throws IntegerFailure { 0 }
func choose(value: SecondReference?): int throws TextFailure { 0 }

class FirstReference {}
class SecondReference {}

func chooseInteger(): int throws IntegerFailure {
    choose(1)
}

func chooseText(): int throws TextFailure {
    choose("one")
}

func unselectedTextEffectDoesNotLeak(): int throws IntegerFailure {
    choose(1)
}

func unselectedIntegerEffectDoesNotLeak(): int throws TextFailure {
    choose("one")
}

func noCandidateMeansNoPurityClaim(value: bool): int {
    // The call is unresolved. Analysis cannot report this operation as known-pure merely
    // because neither overload was selected.
    choose(value)
}

func effectfulGeneric<E: Throwable>(action: () -> int throws E): int throws E {
    action()
}

func effectBudgetOnSelectedGeneric(): int throws IntegerFailure {
    effectfulGeneric({ choose(1) })
}

func rejectedGenericCandidateDoesNotLeak(): int throws IntegerFailure {
    effectfulGeneric({ choose(1) })
}

func expectedTypeSelectsEffectfulCandidate(): () -> int throws IntegerFailure {
    chooseCallback({ choose(1) })
}

func chooseCallback(action: () -> int throws IntegerFailure): () -> int throws IntegerFailure {
    { action() }
}

func ambiguityDoesNotUnionCandidateEffects(): int {
    // The null literal is applicable to the two unrelated nullable reference overloads.
    // The diagnostic should report ambiguity, not pretend both implementations ran.
    choose(null)
}

func resourceLimitedCandidateSet(value: int): int {
    // A bound hit produces a resource/deferred analysis result, not an order-dependent winner.
    choose(value)
}

open class BaseFailure: Exception {}
class DerivedFailure: BaseFailure {}

func subtypeCandidateBudget(): int throws BaseFailure {
    chooseSubtype()
}

func chooseSubtype(): int throws DerivedFailure { 1 }

func siblingCandidateBudget(): int throws WideFailure {
    chooseSubtype()
}

// Candidate-local state includes selected target, generic substitution, argument mapping,
// lambda effects, and default effects. Rejected or deferred candidates must not mutate the
// caller's flow/effect state. Equal-ranked candidates produce ambiguity rather than source-order
// selection.
