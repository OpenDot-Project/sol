// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative overload/FIR contract; some evidence paths remain planned or deferred.
module examples.semantics.generic_fir_overload_selection

// Generic overload selection and FIR identity matrix
// ===================================================
//
// Each `expect` row records the source-level winner before asking whether that winner can be
// represented by the native subset. Rows marked `Deferred` must not be treated as rejected merely
// to make another candidate win. Rows marked `NativeRejected` have a unique source winner but a
// body or ABI shape that the current FIR slice cannot admit. They must not fall back to a lower
// ranked overload. Parsing this file does not prove that an oracle has been executed.
//
// Ranking vocabulary used here:
//
// - `identity` means the actual and substituted formal types are the same resolved type identity.
// - `reference` means one intrinsic reference conversion is required.
// - `boxing` means a conversion to an admitted Object-like target is required.
// - `[a,b]` is a per-source-argument conversion-rank vector in source evaluation order.
// - `nongeneric` is only the final tie preference after equal conversion, vararg, and default ranks.
// - `selected-source` names the source declaration identity; it is not a mangled/native symbol.
//
// No fixture uses declaration order as a tie breaker. Display names such as `T`, `String`, or
// `choose` are not enough to identify an overload or an instantiated target.

// Shared declarations
// --------------------

func <T> choose(value: T): String = "generic"

func choose(value: String): String = "string"

func choose(value: std.core.Object): String = "object"

func <T> choosePair(left: T, right: T): String = "generic-pair"

func choosePair(left: String, right: String): String = "string-pair"

func choosePair(left: std.core.Object, right: std.core.Object): String = "object-pair"

func <T> exactOrGeneric(value: T): T = value

func exactOrGeneric(value: int): int = value

func <T> construct(): T

func <T: std.core.Object> constrained(value: T): String = "object"

func ambiguous(left: String, right: std.core.Object): int = 1

func ambiguous(left: std.core.Object, right: String): int = 2

func chooseVector(left: String, right: std.core.Object): String = "best"

func chooseVector(left: std.core.Object, right: std.core.Object): String = "fallback"

func <T> bounded(value: T): String where T: NamedValue = value.name()

interface NamedValue { func name(): String }

class NamedText: NamedValue { override func name(): String = "named" }

class PlainText {}

func <T> makeDefault(value: T = default<T>()): T = value

func fixedOrVararg(value: String): String = value

func <T> fixedOrVararg(value: T, vararg rest: T): T = value

func <T> onlyVararg(vararg rest: T): T

func <T> compare(left: T, right: T): bool = left == right

func compare(left: String, right: String): bool = left == right

func <T> passThrough(value: T): T = value

func <T> first(values: []T): T = values[0]

func <T, U> forward(value: U): T = construct<T>()

func <T> useCallback(callback: () -> T): T = callback()

func <T> useChecked(callback: () -> T throws DiskFailure): T throws DiskFailure = callback()

func <T, E: Throwable, F: Throwable> useTwoEffects(
    callback: () -> T throws E, F
): T throws E, F = callback()

class DiskFailure(message: String): Exception(message)

func readDisk(): String throws DiskFailure = "disk"

// 1. Identity and non-generic tie preference
// -------------------------------------------

func exactStringChoosesConcrete(): String {
    // expect: exact.string | Selected | target=choose(String) type-arguments=[] ranks=[identity] reason=exact-formal
    return choose("text")
}

func exactIntChoosesConcrete(): int {
    // expect: exact.int | Selected | target=exactOrGeneric(int) type-arguments=[] ranks=[identity] reason=nongeneric-final-tie
    return exactOrGeneric(7)
}

func exactLongChoosesGeneric(): long {
    // expect: exact.long | Selected | target=exactOrGeneric<T>(T) type-arguments=[long] ranks=[identity] reason=only-applicable
    val value: long = 7
    return exactOrGeneric(value)
}

func stringDoesNotChooseObject(): String {
    // expect: exact.before.reference | Selected | target=choose(String) ranks=[identity]
    return choose("value")
}

func stringObjectConversionIsWorse(): String {
    // expect: reference.fallback | Selected | target=choose(String) ranks=[identity] object-rank=[reference]
    val value: String = "value"
    return choose(value)
}

func pairKeepsIndependentArgumentRanks(first: String, second: String): String {
    // expect: pair.exact | Selected | target=choosePair(String,String) ranks=[identity,identity]
    return choosePair(first, second)
}

func pairDoesNotCollapseTwoConversions(first: String, second: String): String {
    // expect: pair.vector | Selected | target=choosePair(String,String) ranks=[identity,identity] object-ranks=[reference,reference]
    return choosePair(first, second)
}

func literalUsesOrdinaryLiteralTyping(): int {
    // expect: literal.int | Selected | target=exactOrGeneric(int) type-arguments=[] ranks=[identity]
    return exactOrGeneric(5)
}

func typedLiteralDoesNotCreateAnotherCandidate(value: short): short {
    // expect: typed.short | Selected | target=exactOrGeneric<T>(T) type-arguments=[short] ranks=[identity]
    return exactOrGeneric(value)
}

func explicitGenericForcesGenericCandidate(): String {
    // expect: explicit.generic | Selected | target=choose<T>(T) type-arguments=[String] ranks=[identity]
    return choose<String>("text")
}

func explicitGenericStillChecksArguments(value: int): String {
    // expect: explicit.mismatch | Rejected | reason=argument-conversion-after-explicit-substitution
    return choose<String>(value)
}

func explicitArityIsExact(value: String): String {
    // expect: explicit.arity | Rejected | reason=generic-arity-mismatch; no-truncation=true
    return choose<String, int>(value)
}

func explicitArgumentsDoNotDisableConstraints(value: int): String {
    // expect: explicit.bound | Rejected | reason=constraint-unsatisfied; target=constrained<T>
    return constrained<int>(value)
}

func concreteConstraintPasses(value: NamedText): String {
    // expect: constraint.pass | Selected | target=bounded<T> type-arguments=[NamedText]
    return bounded(value)
}

// 2. A vector is compared component by component
// ----------------------------------------------

func incomparableVectors(left: String, right: String): int {
    // expect: vector.incomparable | Ambiguous | targets=[ambiguous(String,Object),ambiguous(Object,String)] ranks=[[identity,reference],[reference,identity]]
    return ambiguous(left, right)
}

func oneStrictlyBetterVector(left: String, right: std.core.Object): String {
    // expect: vector.strict | Selected | target=chooseVector(String,Object) ranks=[identity,identity] alternative=[reference,identity]
    return chooseVector(left, right)
}

func typedReferenceDoesNotPreferLaterParameter(first: std.core.Object, second: String): int {
    // expect: vector.reverse | Selected | target=ambiguous(Object,String) ranks=[identity,identity]
    return ambiguous(first, second)
}

func genericAndConcreteEqualRank(): String {
    // expect: generic.tie | Selected | target=choose(String) ranks=[identity] final-preference=nongeneric
    return choose("same-type")
}

func concreteOverloadDoesNotBeatBetterGeneric(value: short): short {
    // expect: generic.better-conversion | Selected | target=exactOrGeneric<T>(T) type-arguments=[short] ranks=[identity]
    return exactOrGeneric(value)
}

func genericArityDoesNotBreakTie(first: String, second: String): String {
    // expect: arity.before-rank | Selected | target=choosePair(String,String) ranks=[identity,identity]
    return choosePair(first, second)
}

func overloadNameDoesNotBindFirstDeclaration(value: String): String {
    // expect: name.not-order | Selected | target=choose(String) source-identity=concrete-string-overload
    return choose(value)
}

func equalCandidatesRemainAmbiguous(left: String, right: String): int {
    // expect: exact.tie | Ambiguous | ranks=[[identity,reference],[reference,identity]] declaration-order-tiebreak=false
    return ambiguous(left, right)
}

func incomparableConversionsRemainAmbiguous(left: String, right: String): int {
    // expect: mixed.tie | Ambiguous | selected-source=none
    return ambiguous(left, right)
}

// 3. Expected-result evidence participates before the source winner is sealed
// ---------------------------------------------------------------------------

func expectedStringFromGenericCall(): String {
    // expect: result.string | Selected | target=construct<T>() type-arguments=[String] evidence=[expected-result]
    val value: String = construct()
    return value
}

func expectedIntFromGenericCall(): int {
    // expect: result.int | Selected | target=construct<T>() type-arguments=[int] evidence=[expected-result]
    val value: int = construct()
    return value
}

func noResultContextLeavesParameterOpen(): () {
    // expect: result.missing | Deferred | target=construct<T>() reason=no-input-or-expected-result-evidence
    val value = construct()
}

func nestedExpectedResultFlowsInward(): String {
    // expect: nested.expected | Selected | outer=passThrough<String> inner=construct<String>
    val value: String = passThrough(construct())
    return value
}

func nestedExpectedResultDoesNotComeFromOtherFunction(): String {
    // expect: distant.result | Deferred | reason=unrelated-function-result-is-not-evidence
    return construct()
}

func resultContextCanResolveCompetingGenericTargets(): String {
    // expect: result.disambiguates | Selected | target=construct<T>() type-arguments=[String]
    val text: String = construct()
    return text
}

func resultContextCannotOverrideArgumentMismatch(value: int): String {
    // expect: result.not-conversion | Rejected | reason=expected-result-does-not-rewrite-input-type
    val text: String = choose<String>(value)
    return text
}

func returnAnnotationIsTheContext(): String {
    // expect: result.return-annotation | Selected | target=construct<T>() type-arguments=[String]
    return construct()
}

func unannotatedBlockDoesNotInventExpectedType(): () {
    // expect: result.no-invented-type | Deferred | reason=block-result-is-unit
    val text = construct()
}

func expectedResultIsCandidateLocal(): String {
    // expect: result.candidate-local | Selected | target=construct<T>() type-arguments=[String] leak-to-other-candidate=false
    val value: String = construct()
    return value
}

func expectedResultIsNotADeclarationOrderHint(): String {
    // expect: result.not-order | Selected | target=construct<T>() type-arguments=[String]
    val value: String = construct()
    return value
}

// 4. Constraint evidence filters candidates but does not replace ranking
// ---------------------------------------------------------------------

func boundedCandidateWinsOnlyAfterProof(value: NamedText): String {
    // expect: constraint.after-evidence | Selected | target=bounded<T> type-arguments=[NamedText] constraints=[NamedValue]
    return bounded(value)
}

func unsatisfiedBoundRemovesCandidate(value: PlainText): String {
    // expect: constraint.reject | Rejected | target=bounded<T> reason=missing-NamedValue
    return bounded(value)
}

func multipleBoundsAreConjunctive(value: NamedText): String {
    // expect: constraint.all | Selected | target=bounded<T> every-bound=true
    return bounded(value)
}

func incomparableBoundsDoNotUseSpelling(value: NamedText): String {
    // expect: constraint.identity | Selected | target=bounded<T> parameter-owner=bounded ordinal=0
    return bounded(value)
}

func explicitBoundFailureDoesNotInferAnotherType(value: PlainText): String {
    // expect: constraint.explicit-no-fallback | Rejected | candidate=bounded<PlainText> fallback=none
    return bounded<PlainText>(value)
}

func boundDoesNotPromoteObjectBoxing(value: int): String {
    // expect: constraint.no-box | Rejected | target=constrained<int> reason=Object-constraint
    return constrained<int>(value)
}

func boundFailureOnOneCandidateKeepsOthers(value: String): String {
    // expect: constraint.other-candidate | Selected | target=choose(String) losing=constrained<T>
    return choose(value)
}

func boundIndeterminateBlocksWinner(value: NamedText): String {
    // expect: constraint.indeterminate | Deferred | reason=unresolved-bound-could-change-applicability
    return bounded(value)
}

// 5. Ref and out use exact storage evidence
// -----------------------------------------

func exchange<T>(ref left: T, out right: T): () { right = left }

func exchange(ref left: String, out right: String): () { right = left }

func exactRefOutChoosesConcrete(): () {
    var left = "left"
    var right: String
    // expect: refout.string | Selected | target=exchange(String,String) type-arguments=[] ranks=[identity,identity]
    exchange(ref left, out right)
}

func exactRefOutCanInstantiateGeneric(): () {
    var left = 1
    var right: int
    // expect: refout.int | Selected | target=exchange<T>(T,T) type-arguments=[int] ranks=[exact,exact]
    exchange(ref left, out right)
}

func refOutNeverWidensStorage(): () {
    var left: short = 1
    var right: int
    // expect: refout.no-widen | Rejected | reason=ref-out-exact-type
    exchange(ref left, out right)
}

func readOnlyValueIsNotRefCandidate(): () {
    val left = "fixed"
    var right: String
    // expect: refout.writable | Rejected | reason=immutable-ref-location
    exchange(ref left, out right)
}

func outVariableTypeComesFromSelectedTarget(): () {
    // expect: outvar.selected | Selected | type-arguments=[String] local-type=String
    produce<String>(out var created)
    val length = created.length
}

func <T> produce(out value: T): ()

func conflictingGenericRefEvidence(): () {
    var text = "text"
    var number = 7
    // expect: refout.conflict | Rejected | reason=exact-evidence-conflict target=exchange<T>
    exchange(ref text, out number)
}

func refOutMappingDoesNotReorderStorage(): () {
    var first = "first"
    var second: String
    // expect: refout.mapping | Selected | source-argument-order=[0,1] formal-order=[0,1]
    exchange(ref first, out second)
}

// 6. Defaults and varargs are later ranking criteria
// -------------------------------------------------

func genericDefault<T>(first: T, second: T = first): T = first

func genericDefault(first: String, second: String = first): String = first

func defaultTiePrefersFewerOmissions(value: String): String {
    // expect: default.count | Selected | target=genericDefault(String,String) defaults=[]
    return genericDefault(value, value)
}

func defaultCountBreaksEqualConversions(value: String): String {
    // expect: default.fewer | Selected | defaults=0 generic=false
    return genericDefault(value, value)
}

func vararg<T>(head: T, vararg rest: T): T = head

func varargOrdinaryArityWins(value: String): String {
    // expect: vararg.no-expansion | Selected | target=fixedOrVararg(String) uses-vararg=false
    return fixedOrVararg(value)
}

func defaultsAreNotInferenceEvidence(): () {
    // expect: default.no-evidence | Deferred | target=makeDefault<T> reason=missing-input-evidence
    val unknown = makeDefault()
}

func varargExpansionDoesNotBeatFixedArity(value: String): String {
    // expect: vararg.fixed | Selected | uses-vararg=false
    return fixedOrVararg(value)
}

func emptyVarargDoesNotInventType(): () {
    // expect: vararg.empty | Deferred | target=onlyVararg<T> reason=no-inference-evidence
    val unknown = onlyVararg()
}


// See [GenericFirOverloadSelectionBoundaries.sol](GenericFirOverloadSelectionBoundaries.sol) for the remaining contract.
