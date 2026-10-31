// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative constraint cases, continued from GenericFirConstraints.sol.
module examples.semantics.generic_fir_constraints

// Generic overload, nested substitution, verifier, and deferred constraint cases

// 8. Generic overloads and constraint filtering
// ---------------------------------------------

func genericOverloadName(value: NamedPeer): String {
    // expect: overload.name | Source:Selected(selectTrait.HasName) | constraints=HasName:satisfied,HasRank:unsatisfied | FIR:Rejected(unsupported-nominal-argument)
    return selectTrait(value)
}

func genericOverloadRank(value: RankOnly): String {
    // expect: overload.rank | Source:Selected(selectTrait.HasRank) | constraints=HasRank:satisfied,HasName:unsatisfied | FIR:Rejected(unsupported-nominal-argument)
    return selectTrait(value)
}

func genericOverloadBoth(value: NamedRanked): String {
    // expect: overload.equal-generic-shapes | Source:Ambiguous | candidates=[selectTrait.HasName,selectTrait.HasRank] | FIR:NoInstance
    return selectTrait(value)
}

func genericOverloadNoCandidate(value: Unrelated): String {
    // expect: overload.no-bounds | Source:Rejected(no-applicable-candidate) | candidates=[selectTrait.HasName,selectTrait.HasRank] | FIR:NoInstance
    return selectTrait(value)
}

func nonGenericWinner(value: NamedChild): String {
    // expect: overload.backend-independent | Source:Selected(nongeneric-pick) | generic-body-availability=not-ranking-input
    return pick(value)
}

func candidateBoundFailureDoesNotPoisonNext(value: NamedPeer): String {
    // expect: overload.candidate-isolation | Source:Selected(selectTrait.HasName) | discarded=selectTrait.HasRank(constraint-failure)
    return selectTrait(value)
}

func losingUnsupportedBodyDoesNotRejectWinner(value: NamedChild): String {
    // expect: overload.losing-body | Source:Selected(source-winner) | specialization=selected-only
    return pick(value)
}

func overloadConstraintDoesNotBeatConversion(value: NamedRanked): String {
    // expect: overload.constraint-vs-rank | Source:Ambiguous-or-ordinary-ranking | backend-support=not-ranking-input
    return selectTrait(value)
}

func explicitOverloadChecksBounds(value: NamedRanked): String {
    // expect: overload.explicit-bound | Source:Ambiguous | candidates=selectTrait | bounds-still-checked
    return selectTrait(value)
}

// 9. Nested generic forwarding and owner identity
// -----------------------------------------------

func closedForwardingInt(value: int): int {
    // expect: nested.int | Source:Applicable | functions=[forwardingIdentity<int>,identity<int>] | FIR:Eligible
    return forwardingIdentity(value)
}

func closedForwardingString(value: String): String {
    // expect: nested.string | Source:Applicable | functions=[forwardingIdentity<String>,identity<String>] | FIR:Eligible
    return forwardingIdentity(value)
}

func closedForwardingArray(value: []String): []String {
    // expect: nested.array | Source:Applicable | functions=[forwardingIdentity<[]String>,identity<[]String>] | FIR:Eligible
    return forwardingIdentity(value)
}

func sameSpellingDifferentOwners(value: NamedChild): NamedChild {
    // expect: nested.owner-capture | Source:Applicable | outer-T!=callee-T-by-identity | FIR:Rejected(unsupported-nominal-argument)
    return forwardingNamed(value)
}

func nestedConstraintPasses(value: NamedChild): NamedChild {
    // expect: nested.forwarded-bounds | Source:Applicable | proof=[outer.HasName,outer.HasRank] | FIR:Rejected(unsupported-nominal-argument)
    return forwardingNamed(value)
}

func nestedConstraintFails(value: NamedPeer): NamedPeer {
    // expect: nested.forwarded-missing-rank | Source:Rejected(constraint-HasRank) | FIR:NoInstance
    return forwardingNamed(value)
}

func sameConcreteNestedIdentityA(value: String): String {
    // expect: nested.same-key-a | Source:Applicable | instance=identity<String>
    return forwardingIdentity(value)
}

func sameConcreteNestedIdentityB(value: String): String {
    // expect: nested.same-key-b | Source:Applicable | instance=identity<String> | reused=true
    return forwardingIdentity(value)
}

func nestedUnconstrainedPrimitive(value: long): long {
    // expect: nested.signed64 | Source:Applicable | functions=[forwardingIdentity<long>,identity<long>] | FIR:Eligible
    return forwardingIdentity(value)
}

func nestedUnconstrainedString(value: String): String {
    // expect: nested.string-exact | Source:Applicable | functions=[forwardingIdentity<String>,identity<String>] | FIR:Eligible
    return forwardingIdentity(value)
}

// 10. Parameters and expected-result evidence
// -------------------------------------------

func inferredFromValue(value: String): String {
    // expect: evidence.input | Source:Applicable | type-arguments=[String] | constraint=Object:satisfied | FIR:Eligible
    return referenceIdentity(value)
}

func inferredFromExactInteger(value: int): int {
    // expect: evidence.exact-input | Source:Applicable | type-arguments=[int] | FIR:Eligible
    return identity(value)
}

func expectedResultSelectsString(): String {
    // expect: evidence.expected-result | Source:Applicable | type-arguments=[String] | FIR:Eligible
    return expectedResult()
}

func expectedResultSelectsArray(): []int {
    // expect: evidence.expected-array | Source:Applicable | type-arguments=[[]int] | FIR:Eligible
    return expectedResult()
}

func expectedResultNoUniqueBound(): NamedRanked {
    // expect: evidence.context-does-not-waive-constraints | Source:Ambiguous | candidates=[traitResult.HasName,traitResult.HasRank] | FIR:NoInstance
    return traitResult()
}

func inferenceDoesNotUseUnrelatedFunction(value: String): String {
    // expect: evidence.no-global-fact | Source:Applicable | type-arguments=[String] | evidence=local-call-only
    val first = identity(value)
    return first
}

func inferenceDoesNotGuessObject(): () {
    // expect: evidence.empty-does-not-default | Source:Deferred-or-SourceRejected | fallback=Object-forbidden | FIR:NoInstance
    val result = expectedResult()
}

func inferenceNoDefaultArgumentEvidence(): String {
    // expect: evidence.default-is-not-evidence | Source:Deferred-or-SourceRejected | FIR:NoInstance
    return expectedResult()
}

func inferenceBoundAfterContext(value: String): String {
    // expect: evidence.context-then-bound | Source:Applicable | type-arguments=[String] | constraint=Object:satisfied | FIR:Eligible
    return onlyReference(value)
}

func inferenceExplicitThenBound(value: String): String {
    // expect: evidence.explicit-then-bound | Source:Applicable | type-arguments=[String] | constraint=Object:satisfied | FIR:Eligible
    return onlyReference<String>(value)
}

// 11. Arrays, nullable shapes, and structural limits
// -------------------------------------------------

func unconstrainedArrayIdentity(value: []int): []int {
    // expect: shape.array-one-level | Source:Applicable | type-arguments=[int] | FIR:Eligible
    return unconstrainedArray(value)
}

func boundedArrayOfNamed(value: []NamedChild): []NamedChild {
    // expect: shape.array-user-nominal | Source:Applicable-or-SourceRejected-by-array-element-rule | FIR:Rejected(unsupported-array-element)
    return arrayElementBound(value)
}

func boundedArrayOfString(value: []String): []String {
    // expect: shape.array-string | Source:Applicable | type-arguments=[String] | FIR:Eligible
    return arrayElementBound(value)
}

func nullableNamedValue(value: NamedChild?): NamedChild? {
    // expect: shape.nullable-nominal | Source:Applicable-or-SourceRejected-by-bound-nullability | FIR:Rejected(unsupported-nullable-native-abi)
    return nullableBound(value)
}

func nullableStringValue(value: String?): String? {
    // expect: shape.nullable-string | Source:Applicable-or-SourceRejected-by-bound-nullability | FIR:Rejected(unsupported-nullable-native-abi)
    return nullableBound(value)
}

func refLikeArrayShape(value: []BorrowedName): []BorrowedName {
    // expect: shape.ref-like-array | Source:Rejected(ref-like-array-element) | FIR:NoInstance
    return constrainArray(value)
}

func nestedArrayIdentity(value: [][]int): [][]int {
    // expect: shape.nested-array | Source:Applicable | type-arguments=[[]int] | FIR:Rejected(unsupported-nested-array)
    return identity(value)
}

func fixedArrayIdentity(value: [2]int): [2]int {
    // expect: shape.fixed-array | Source:Applicable | type-arguments=[[2]int] | FIR:Rejected(unsupported-fixed-array)
    return identity(value)
}

func unitTupleIdentity(value: ()): () {
    // expect: shape.unit-tuple | Source:Applicable | type-arguments=[()] | FIR:Eligible-or-rejected-by-slot-policy
    return identity(value)
}

func nullableBoundUsesExactType(value: NamedChild?): NamedChild? {
    // expect: shape.bound-does-not-strip-nullable | Source:ConstraintCheckedAgainst=NamedChild? | FIR:Rejected(unsupported-nullable-native-abi)
    return nullableBound(value)
}

// 12. Constraint and FIR verifier agreement
// -----------------------------------------

func verifierExpectedFunctionInstance(value: int): int {
    // expect: verifier.signature | source-arguments=[int] | function-signature=[int]->int | verifier=accept
    return identity(value)
}

func verifierExpectedDistinctFunctionInstances(number: int, text: String): () {
    // expect: verifier.distinct-keys | instances=[identity<int>,identity<String>] | keys=distinct
    val a = identity(number)
    val b = identity(text)
}

func verifierExpectedNestedCallArguments(value: String): String {
    // expect: verifier.call-arguments | source-call=identity<String> | target-specialization=identity<String> | verifier=accept
    return forwardingIdentity(value)
}

func verifierRejectsOpenArgumentMetadata(value: int): int {
    // expect: verifier.open-argument | tamper=SpecializationArguments[T] | verifier=reject(SOL2402)
    return identity(value)
}

func verifierRejectsWrongArgumentOrder(left: int, right: String): int {
    // expect: verifier.argument-order | tamper=[String,int] | source-binding=[int,String] | verifier=reject(SOL2402)
    return pairwise(left, right)
}

func verifierRejectsChangedConstraintInstance(value: String): String {
    // expect: verifier.constraint-identity | tamper=Object-bound-instance-to-int | source-origin=onlyReference | verifier=reject(SOL2402)
    return onlyReference(value)
}

func verifierRejectsDuplicateInstanceRecord(value: int): int {
    // expect: verifier.duplicate-key | tamper=duplicate(identity<int>) | verifier=reject(SOL2402)
    return identity(value)
}

func verifierRejectsWrongCallTypeArguments(value: String): String {
    // expect: verifier.call-target-substitution | tamper=call-arguments-to-int | target=identity<String> | verifier=reject(SOL2402)
    return identity(value)
}

func verifierRejectsTargetSignatureMismatch(value: int): int {
    // expect: verifier.signature-substitution | tamper=target-parameter-to-long | selected-type-argument=int | verifier=reject(SOL2402)
    return identity(value)
}

func verifierKeepsSourceOrigins(value: String): String {
    // expect: verifier.source-origin | function-origin=identity-declaration | call-origin=identity-call | no-name-relookup
    return identity(value)
}

func verifierKeepsConstraintSource(value: String): String {
    // expect: verifier.constraint-origin | declaration=referenceIdentity | bound=std.core.Object | verifier=accept
    return referenceIdentity(value)
}

// 13. Deferred, invalid, and resource outcomes
// --------------------------------------------

func tooFewExplicitArguments(value: int): int {
    // expect: failure.explicit-arity | Source:Rejected(generic-arity) | FIR:NoInstance
    return pairwise<int>(value, value)
}

func tooManyExplicitArguments(value: int): int {
    // expect: failure.excess-arity | Source:Rejected(generic-arity) | FIR:NoInstance
    return identity<int, long>(value)
}

func failedConstraintDoesNotCreateFunction(value: RankOnly): RankOnly {
    // expect: failure.no-reservation | Source:Rejected(constraint-HasName) | specialization-count-for-inlineNamed=0
    return inlineNamed(value)
}

func incompleteEvidenceDoesNotCreateFunction(): () {
    // expect: failure.unresolved | Source:Deferred-or-SourceRejected | specialization-count-for-expectedResult=0
    val result = expectedResult()
}

func unsupportedNativeArgumentDoesNotCreateVerifiedProgram(value: NamedChild): NamedChild {
    // expect: failure.native-admission | Source:Applicable | FIR:Rejected(SOL2400) | VerifiedFirProgram=absent
    return inlineNamed(value)
}

func instanceLimitIsNotConstraintFailure(value: int): int {
    // expect: failure.resource-owner | Source:Applicable | FIR:Rejected(SOL2403) | constraint=int-unconstrained
    return identity(value)
}

func cancellationDoesNotPublishPartialGraph(value: int): int {
    // expect: failure.cancellation | source-type-argument=int | FIR:Canceled | partial-program=not-published
    return identity(value)
}

func specializationFailureDoesNotPickOtherOverload(value: NamedRanked): String {
    // expect: failure.no-backend-rerank | Source:Selected-by-source-rules | FIR:Rejected(unsupported-nominal-argument) | fallback-overload=none
    return chooseByName(value)
}

func foreignWorkspaceTypeCannotSatisfyConstraint(value: String): String {
    // expect: failure.workspace-identity | source-type=String | verifier-foreign-type=reject(SOL2402)
    return referenceIdentity(value)
}

func specializationCancellationBeforeLowering(value: String): String {
    // expect: failure.no-llvm-before-verification | FIR-verification=required | LLVM-module=not-created-on-failure
    return identity(value)
}

func resourceBudgetIndependentOfTypeInterning(value: String): String {
    // expect: failure.budgets-independent | TypeSpecializationBudget!=GeneratedFunctionBudget | no-partial-instance
    return identity(value)
}

// 14. Current implementation boundary summary
// --------------------------------------------
//
// The source-level expectations above describe declaration identity, nominal constraint checking,
// candidate isolation, and exact generic call arguments. The native expectations distinguish the
// first verified scalar/String/one-level-array slice from nominal owner/member and ref-like cases.
// The verifier's constraint check is necessary but not a replacement for source overload selection.
//
// Generic methods and generic owners require a second substitution axis. The current FIR builder
// rejects them rather than dropping the owner arguments. Ref-like values require an ABI and lifetime
// proof; `allows ref struct` cannot supply either one. Bound member calls require per-instance source
// rebinding or a verified generic dispatch representation. The current body projection does not
// claim that proof.
//
// `SOL2400` identifies a native subset boundary. `SOL2402` identifies an independent FIR verifier
// failure in the tamper expectations. `SOL2403` identifies the FIR specialization resource budget.
// A constraint failure belongs to source candidate analysis and must not be rewritten as one of
// these native codes.
