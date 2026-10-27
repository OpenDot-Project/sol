// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative overload boundary cases, continued from GenericFirOverloadSelection.sol.
module examples.semantics.generic_fir_overload_selection

// Candidate-local selection, native admission, verifier and diagnostic cases

// 7. Candidate-local evidence and deferred work
// ----------------------------------------------

func callbackExpected(): String {
    // expect: lambda.expected | Selected | target=useCallback<T> type-arguments=[String] evidence=[lambda-result]
    return useCallback({ readDisk() })
}

func callbackEffectsArePartOfApplicability(): String throws DiskFailure {
    // expect: lambda.effect | Selected | target=useChecked<T> effects=[DiskFailure]
    return useChecked({ readDisk() })
}

func callbackEvidenceDoesNotLeak(): () {
    // expect: lambda.transaction | Deferred | losing-candidate-state-restored=true
    val unresolved = useCallback({ readDisk() })
}

func unresolvedLambdaCandidateBlocks(): String {
    // expect: lambda.blocker | Deferred | reason=unresolved-lambda-could-change-overload
    return useCallback({ readDisk() })
}

func unsupportedConversionSearchBlocks(): String {
    // expect: conversion.deferred | Deferred | reason=user-conversion-search-not-implemented
    return choose(default<NamedText>())
}

func arrayEvidenceRemainsCandidateLocal(values: []String): String {
    // expect: array.candidate-local | Selected | target=first<T>([]T) type-arguments=[String]
    return first(values)
}

func ambiguousArrayJoinDoesNotPickFirst(): () {
    // expect: array.join | Ambiguous | reason=no-unique-common-element-type
    val value = first(["text", 1])
}

func checkedEffectSetOrderIsNotEvidence(): String throws DiskFailure {
    // expect: effects.order | Selected | type-arguments=[String] effect-order-is-not-identity=true
    return useChecked({ readDisk() })
}

func multiOpenEffectMappingCanDefer(): () {
    // expect: effects.multi-open | Deferred | reason=more-open-patterns-than-actual-effects
    val value = useTwoEffects({ readDisk() })
}

// 8. Winner selection is independent of native body admission
// -----------------------------------------------------------

interface Addable<T> { func add(other: T): T }

class AddableValue: Addable<AddableValue> { override func add(other: AddableValue): AddableValue = this }

func <T: Addable<T>> nativeUnsupportedOperation(value: T): T = value.add(value)

func nativeUnsupportedOperation(value: std.core.Object): String = "fallback"

func sourceWinnerCanBeNativeRejected(value: AddableValue): AddableValue {
    // expect: native.body-operation | NativeRejected | selected-source=nativeUnsupportedOperation<T> fallback=none
    return nativeUnsupportedOperation(value)
}

func betterSourceGenericDoesNotLoseToWorseNativeOverload(value: AddableValue): String {
    // expect: native.no-fallback | NativeRejected | selected-source=nativeUnsupportedOperation<T>
    val result = nativeUnsupportedOperation(value)
    return "unreachable"
}

func supportedConcreteBodyCanLower(value: String): String {
    // expect: native.identity | FIRSpecialized | target=passThrough<String> signature=(String)->String
    return passThrough(value)
}

func sameGenericDeclarationHasOneInstance(value: String): () {
    // expect: instance.reuse | FIRSpecialized | calls=2 distinct-instances=1
    val first = passThrough(value)
    val second = passThrough(value)
}

func differentTypeArgumentsHaveDifferentInstances(text: String, number: int): () {
    // expect: instance.distinct | FIRSpecialized | instances=[passThrough<String>,passThrough<int>]
    val first = passThrough(text)
    val second = passThrough(number)
}

func sourceOverloadIsCarriedIntoFir(value: String): String {
    // expect: fir.source-identity | FIRVerified | target-source=choose(String) target-specialization=[]
    return choose(value)
}

func genericOverloadIsCarriedIntoFir(value: long): long {
    // expect: fir.generic-identity | FIRVerified | target-source=exactOrGeneric<T> target-specialization=[long]
    return exactOrGeneric(value)
}

func specializationDoesNotUseDisplayNameAsTarget(value: String): String {
    // expect: fir.identity | FIRVerified | target-key=(source-declaration,type-arguments=[String])
    return passThrough(value)
}

func sameNameDifferentOwnersAreNotOneTarget(value: String): String {
    // expect: fir.owner | FIRVerified | selected-owner=passThrough; alternate=choose
    return passThrough(value)
}

func selectedCallOriginSurvivesSpecialization(value: String): String {
    // expect: fir.origin | FIRVerified | call-origin=source-call target-origin=generic-declaration
    return passThrough(value)
}

// 9. Verifier rejects altered overload and specialization identities
// -----------------------------------------------------------------

func verifierTargetMustMatchSelectedSource(value: String): String {
    // expect: verifier.target | TamperRejected | mutation=replace-target-with-object-overload
    return choose(value)
}

func verifierTypeArgumentsMustMatchCallBinding(value: long): long {
    // expect: verifier.type-arguments | TamperRejected | mutation=change-call-argument-to-int
    return exactOrGeneric(value)
}

func verifierSpecializedSignatureMustMatch(value: String): String {
    // expect: verifier.signature | TamperRejected | mutation=parameter-type-int
    return passThrough(value)
}

func verifierEffectsRemainOrderedAndTyped(value: String): String throws DiskFailure {
    // expect: verifier.effects | TamperRejected | mutation=erase-selected-effect
    return useChecked({ readDisk() })
}

func verifierRejectsDuplicateInstanceIdentity(first: String, second: String): () {
    // expect: verifier.duplicate | TamperRejected | mutation=duplicate-source-and-type-argument-key
    val left = passThrough(first)
    val right = passThrough(second)
}

func verifierRejectsOrphanInstance(value: String): String {
    // expect: verifier.orphan | TamperRejected | mutation=insert-unreferenced-passThrough<String>
    return value
}

// 10. Diagnostics preserve phase and do not become fallback selection
// -------------------------------------------------------------------

func noVisibleOverload(value: int): String {
    // expect: diagnostic.no-applicable | SOL2131 | selected-source=none
    return constrained(value)
}

func tiedOverloadDiagnostic(value: String): int {
    // expect: diagnostic.ambiguous | SOL2132 | selected-source=none
    return ambiguous(value, value)
}

func unresolvedDirectName(value: String): String {
    // expect: diagnostic.unresolved | SOL2130 | selected-source=none
    return unknownOverload(value)
}

func deferredIsNotNoApplicable(value: String): String {
    // expect: diagnostic.deferred | source-analysis-incomplete=true no-fallback=true
    return useCallback({ construct() })
}

func unsupportedNativeBodyDiagnostic(value: NamedText): NamedText {
    // expect: diagnostic.native-boundary | SOL2400 | source-winner-preserved=true
    return nativeUnsupportedOperation(value)
}

func instanceBudgetDiagnostic(value: String): String {
    // expect: diagnostic.resource | SOL2403 | partial-output=false
    return passThrough(value)
}

func verifierFailureDoesNotBecomeSourceAmbiguity(value: String): String {
    // expect: diagnostic.verifier | SOL2402 | source-selection=unchanged
    return choose(value)
}

// 11. Candidate outcomes remain separate from code-generation outcomes
// --------------------------------------------------------------------

func compilationPhaseBoundary(value: String): String {
    // expect: phase.source | Bound | source-candidate=choose(String)
    return choose(value)
}

func closedCallBindingIsNecessary(value: String): String {
    // expect: phase.binding | Bound | generic-arguments=[]
    return choose(value)
}

func closedInstanceIsAnotherProof(value: String): String {
    // expect: phase.instance | FIRSpecialized | target=passThrough<String>
    return passThrough(value)
}

func verifiedCapabilityIsRequired(value: String): String {
    // expect: phase.verified | FIRVerified | invalid-body-produces-no-capability=true
    return passThrough(value)
}

func llvmUsesFunctionIdentity(value: String): String {
    // expect: phase.llvm | Lowered | call-target=FirFunction.Id textual-llvm-input=false
    return passThrough(value)
}

func backendChoiceDoesNotRebindOverloads(value: String): String {
    // expect: phase.backend | same-selected-source-for=[AOT,ORC]
    return choose(value)
}

func artifactFailureDoesNotPublishPartialSpecialization(value: String): String {
    // expect: phase.artifact | Failed | existing-output-preserved=true
    return passThrough(value)
}

// 12. Review checklist scenarios
// ------------------------------

func reviewOne(value: String): String {
    // expect: review.01 | selected target is source-owned
    return choose(value)
}

func reviewTwo(value: String): String {
    // expect: review.02 | generic parameters use declaration identity and ordinal
    return passThrough(value)
}

func reviewThree(value: long): long {
    // expect: review.03 | explicit and inferred arguments preserve declaration order
    return passThrough(value)
}

func reviewFour(value: String): String {
    // expect: review.04 | all input argument ranks participate
    return choose(value)
}

func reviewFive(first: String, second: String): String {
    // expect: review.05 | incomparable vectors do not select a declaration-order winner
    return choosePair(first, second)
}

func reviewSix(value: String): String {
    // expect: review.06 | nongeneric preference is applied after equal conversion ranks
    return choose(value)
}

func reviewSeven(value: NamedText): String {
    // expect: review.07 | constraints are proved before a candidate is applicable
    return bounded(value)
}

func reviewEight(value: String): String {
    // expect: review.08 | expected-result evidence is local to the enclosing expression
    val expected: String = construct()
    return expected
}

func reviewNine(value: String): String {
    // expect: review.09 | exact ref/out storage is not converted
    return passThrough(value)
}

func reviewTen(value: String): String {
    // expect: review.10 | unresolved candidate work remains a blocker
    return choose(value)
}

func reviewEleven(value: String): String {
    // expect: review.11 | native availability cannot alter source overload ranking
    return choose(value)
}

func reviewTwelve(value: String): String {
    // expect: review.12 | source call origin and target declaration both survive FIR creation
    return passThrough(value)
}

func reviewThirteen(value: String): String {
    // expect: review.13 | function instance key includes the complete ordered argument vector
    return passThrough(value)
}

func reviewFourteen(value: String): String {
    // expect: review.14 | verifier compares call and function specialization arguments
    return passThrough(value)
}

func reviewFifteen(value: String): String {
    // expect: review.15 | unsupported selected body reports native boundary without fallback
    return passThrough(value)
}

func reviewSixteen(value: String): String {
    // expect: review.16 | resource exhaustion is not successful partial compilation
    return passThrough(value)
}

func reviewSeventeen(value: String): String {
    // expect: review.17 | FIR schema records ordered type-argument identities
    return passThrough(value)
}

func reviewEighteen(value: String): String {
    // expect: review.18 | AOT and ORC consume the same verified function graph
    return passThrough(value)
}

func reviewNineteen(value: String): String {
    // expect: review.19 | no target is selected from native symbol spelling
    return choose(value)
}

func reviewTwenty(value: String): String {
    // expect: review.20 | no runtime generic descriptor is implied by monomorphized FIR
    return passThrough(value)
}

// The fixture intentionally includes planned, deferred, and negative rows beside supported
// identity rows. A future conformance harness must report which phase each expectation exercises;
// an unsupported native body is not a failed generic overload match, and a parse-only pass is not
// evidence that source binding, FIR verification, LLVM lowering, AOT, or ORC passed.
