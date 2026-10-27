// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative FIR specialization cases; implementation support and passing status are not claimed.
module source.spec.generic_fir_specialization_cases

// Generic free-function FIR specialization matrix
// ================================================
//
// This file describes the expected semantic/FIR result at each marked call. The annotations are
// normative test oracles, not comments copied from a passing compiler run. A test harness should
// compare the closed callable signature, canonical specialization key, candidate disposition,
// source diagnostic category, and call-site identity. Parsing this file does not prove any oracle.
//
// Annotation vocabulary
// ---------------------
//
// `// expect: <case> | Specialized | ...` requires one closed free-function specialization.
// `type-arguments` is ordered by declaration ordinal; `parameters` follows the selected callable
// signature; `result` is the substituted result. `instance` identifies the canonical declaration
// plus closed argument sequence, not a workspace-local numeric ID. `calls` lists nested call nodes
// from outermost to innermost. Identical keys at distinct sites refer to the same specialization
// identity within one analysis workspace, while preserving distinct call-site nodes.
//
// `Rejected` means complete evidence proves the candidate inapplicable. `Ambiguous` means multiple
// complete candidates remain equally best. `Deferred` means a supported candidate could still
// change the result but an explicitly named analysis boundary is unavailable. A negative result
// never creates a successful partially substituted FIR node. Diagnostic names below are semantic
// categories for the harness to map to its stable public diagnostic identifiers.
//
// This matrix is intentionally limited to free functions and their call-site FIR. It does not
// specify generic class/object owner monomorphization, member receiver substitution, or native
// machine-code emission. Those boundaries appear only as deferred cases near the end.

// Shared declarations
// --------------------

func <T> specialize(value: T): T = value

func <T> constant(): T

class FirBox<T>(val value: T) {
    func get(): T = value
}

interface FirNamed {
    func name(): String
}

interface FirComparable<T> {
    func compare(other: T): int
}

class FirNamedNumber: FirNamed, FirComparable<FirNamedNumber> {
    override func name(): String = "number"
    func compare(other: FirNamedNumber): int = 0
}

class FirNamedOnly: FirNamed {
    override func name(): String = "only-named"
}

class FirUnboundedValue { }

func <T: FirNamed> requireNamed(value: T): String = value.name()

func <T> requireNamedAndComparable(value: T): T
    where T: FirNamed, T: FirComparable<T> = value

// 1. Closed primitive and String substitutions
// ---------------------------------------------
//
// Each argument below already has a closed source type. FIR specialization uses those identities
// directly; it does not widen typed numeric variables, inspect machine representation, or infer
// from unrelated calls elsewhere in this module. The literal cases exercise the language's
// ordinary literal typing and representability before the generic signature is closed.

func primitiveAndStringSites(
    signed8: sbyte,
    signed16: short,
    signed32: int,
    signed64: long,
    unsigned8: byte,
    unsigned32: uint,
    unsigned64: ulong,
    binary16: half,
    binary32: single,
    binary64: double,
    binary128: quad,
    text: String,
): () {
    // expect: primitive.sbyte | Specialized | call=specialize(signed8) type-arguments=[sbyte] parameters=[sbyte] result=sbyte instance=specialize<sbyte>
    val a = specialize(signed8)
    // expect: primitive.short | Specialized | call=specialize(signed16) type-arguments=[short] parameters=[short] result=short instance=specialize<short>
    val b = specialize(signed16)
    // expect: primitive.int | Specialized | call=specialize(signed32) type-arguments=[int] parameters=[int] result=int instance=specialize<int>
    val c = specialize(signed32)
    // expect: primitive.long | Specialized | call=specialize(signed64) type-arguments=[long] parameters=[long] result=long instance=specialize<long>
    val d = specialize(signed64)
    // expect: primitive.byte | Specialized | call=specialize(unsigned8) type-arguments=[byte] parameters=[byte] result=byte instance=specialize<byte>
    val e = specialize(unsigned8)
    // expect: primitive.uint | Specialized | call=specialize(unsigned32) type-arguments=[uint] parameters=[uint] result=uint instance=specialize<uint>
    val f = specialize(unsigned32)
    // expect: primitive.ulong | Specialized | call=specialize(unsigned64) type-arguments=[ulong] parameters=[ulong] result=ulong instance=specialize<ulong>
    val g = specialize(unsigned64)
    // expect: primitive.half | Specialized | call=specialize(binary16) type-arguments=[half] parameters=[half] result=half instance=specialize<half>
    val h = specialize(binary16)
    // expect: primitive.single | Specialized | call=specialize(binary32) type-arguments=[single] parameters=[single] result=single instance=specialize<single>
    val i = specialize(binary32)
    // expect: primitive.double | Specialized | call=specialize(binary64) type-arguments=[double] parameters=[double] result=double instance=specialize<double>
    val j = specialize(binary64)
    // expect: primitive.quad | Specialized | call=specialize(binary128) type-arguments=[quad] parameters=[quad] result=quad instance=specialize<quad>
    val k = specialize(binary128)
    // expect: primitive.string | Specialized | call=specialize(text) type-arguments=[String] parameters=[String] result=String instance=specialize<String>
    val l = specialize(text)
}

func literalDefaultTypes(): () {
    // expect: literal.integer-default | Specialized | call=specialize(41) type-arguments=[int] parameters=[int] result=int instance=specialize<int>
    val integer = specialize(41)
    // expect: literal.floating-default | Specialized | call=specialize(2.5) type-arguments=[double] parameters=[double] result=double instance=specialize<double>
    val floating = specialize(2.5)
    // expect: literal.expected-narrowing | Specialized | call=specialize(7) type-arguments=[sbyte] parameters=[sbyte] result=sbyte instance=specialize<sbyte> literal-conversion=representable
    val narrow: sbyte = specialize(7)
    // expect: literal.out-of-range | Rejected | call=specialize(200) type-arguments=[sbyte] diagnostic=LiteralNotRepresentable
    val invalid: sbyte = specialize(200)
}

// 2. Repeated call sites and specialization identity
// -------------------------------------------------
//
// Specialization identity belongs to a generic declaration and its ordered closed type arguments.
// It is not keyed by source location, argument value, result context, printed display name, or a
// process-global type ID. Calls still retain separate source/call nodes for diagnostics and debug
// information. Distinct substitutions of the same declaration must not alias.

func repeatedSameSubstitution(value: int): int {
    // expect: repeat.int.first | Specialized | call=specialize(value) type-arguments=[int] parameters=[int] result=int instance=specialize<int> call-site=first
    val first = specialize(value)
    // expect: repeat.int.second | Specialized | call=specialize(value) type-arguments=[int] parameters=[int] result=int instance=specialize<int> call-site=second canonical-instance=same-as-first
    val second = specialize(value)
    first + second
}

func repeatedDifferentSubstitutions(number: int, text: String): () {
    // expect: repeat.distinct.int | Specialized | call=specialize(number) type-arguments=[int] parameters=[int] result=int instance=specialize<int>
    val first = specialize(number)
    // expect: repeat.distinct.string | Specialized | call=specialize(text) type-arguments=[String] parameters=[String] result=String instance=specialize<String>
    val second = specialize(text)
}

func repeatedAfterExplicitAndInferred(value: String): () {
    // expect: repeat.explicit | Specialized | call=specialize<String>(value) type-arguments=[String] parameters=[String] result=String instance=specialize<String>
    val explicit = specialize<String>(value)
    // expect: repeat.inferred | Specialized | call=specialize(value) type-arguments=[String] parameters=[String] result=String instance=specialize<String> canonical-instance=same-as-explicit
    val inferred = specialize(value)
}

// 3. Nested calls and invariant constructed signatures
// ----------------------------------------------------
//
// Every nested generic call is solved at its own call node. The enclosing expected/formal type may
// constrain an inner result only through the documented output-evidence path. An inner closed
// substitution is never guessed from another sibling or cached at the syntax node alone.

func nestedIdentity(): String {
    // expect: nested.identity | Specialized | call=specialize(specialize("inner")) calls=[specialize<String>,specialize<String>] type-arguments=[String] result=String outer=specialize<String> inner=specialize<String>
    return specialize(specialize("inner"))
}

func <T> wrapInBox(value: T): FirBox<T> = FirBox(value)

func <T> unwrapBox(value: FirBox<T>): T = value.value

func nestedBoxCalls(): String {
    // expect: nested.box.roundtrip | Specialized | call=unwrapBox(wrapInBox("payload")) calls=[unwrapBox<String>,wrapInBox<String>] outer-parameters=[FirBox<String>] outer-result=String inner-parameters=[String] inner-result=FirBox<String>
    return unwrapBox(wrapInBox("payload"))
}

func invariantBoxParameter(value: FirBox<String>): String {
    // expect: nested.invariant.unwrap | Specialized | call=unwrapBox(value) type-arguments=[String] parameters=[FirBox<String>] result=String instance=unwrapBox<String>
    return unwrapBox(value)
}

func invariantBoxMismatch(value: FirBox<FirNamedNumber>): () {
    // expect: nested.invariant.mismatch | Rejected | call=unwrapBox<String>(value) diagnostic=ArgumentConversionFailure actual=FirBox<FirNamedNumber> formal=FirBox<String> invariant=true
    val invalid = unwrapBox<String>(value)
}

func resultContextOnly(): String {
    // expect: nested.expected-result | Specialized | call=constant() type-arguments=[String] parameters=[] result=String evidence=ExpectedResult instance=constant<String>
    return constant()
}

func unresolvedWithoutContext(): () {
    // expect: nested.missing-input-and-result | Rejected | call=constant() diagnostic=MissingGenericEvidence parameter=T no-fallback=[Object,constraint,first-declaration]
    val invalid = constant()
}

// 4. Overload selection after generic closure
// -------------------------------------------
//
// Candidate inference closes each generic candidate independently. Selection then uses the
// language's normal conversion and overload ranking rules. A generic candidate's declaration
// order cannot win a tie; a more exact non-generic candidate remains preferred where the source
// overload specification requires it. The selected call stores its candidate identity in FIR.

func <T> chooseOverload(value: T): String = "generic"
func chooseOverload(value: String): String = "string"

func overloadString(value: String): String {
    // expect: overload.string | Specialized | call=chooseOverload(value) selected=chooseOverload(String) candidates=[chooseOverload<T=String>,chooseOverload<String>] winner=non-generic type-arguments=[] parameters=[String] result=String
    return chooseOverload(value)
}

func overloadInteger(value: int): String {
    // expect: overload.integer | Specialized | call=chooseOverload(value) selected=chooseOverload<T=int> candidates=[chooseOverload<T=int>] type-arguments=[int] parameters=[int] result=String
    return chooseOverload(value)
}

func <T> projectOverload(value: T): T = value
func <T> projectOverload(value: FirBox<T>): T = value.value

func overloadDirectShape(value: String): String {
    // expect: overload.direct-shape | Specialized | call=projectOverload(value) selected=projectOverload<T=String>(T) candidates=[direct<T=String>] type-arguments=[String] parameters=[String] result=String
    return projectOverload(value)
}

func overloadNestedShape(value: FirBox<String>): String {
    // expect: overload.nested-shape | Specialized | call=projectOverload(value) selected=projectOverload<T=String>(FirBox<T>) candidates=[nested<T=String>] type-arguments=[String] parameters=[FirBox<String>] result=String
    return projectOverload(value)
}

func <T: FirNamed> constrainedChoice(value: T): String = value.name()
func constrainedChoice(value: String): String = value

func overloadCandidateConstraint(value: FirNamedNumber): String {
    // expect: overload.constraint-ranking | Specialized | call=constrainedChoice(value) selected=constrainedChoice<T=FirNamedNumber> candidates=[generic-bound-satisfied,non-generic-inapplicable] type-arguments=[FirNamedNumber] parameters=[FirNamedNumber] result=String
    return constrainedChoice(value)
}

// 5. Constraint validation on a closed specialization
// ---------------------------------------------------
//
// Inference chooses a source identity first; validation then checks all substituted bounds. A
// constraint is not a conversion target and cannot rewrite an inferred argument to its interface.
// Multiple `where` requirements are conjunctive, including bounds written on one parameter.

func validInlineBound(value: FirNamedNumber): String {
    // expect: bounds.inline.valid | Specialized | call=requireNamed(value) type-arguments=[FirNamedNumber] parameters=[FirNamedNumber] result=String constraints=[FirNamed:satisfied] instance=requireNamed<FirNamedNumber>
    return requireNamed(value)
}

func invalidInlineBound(value: FirUnboundedValue): () {
    // expect: bounds.inline.invalid | Rejected | call=requireNamed(value) inferred=[FirUnboundedValue] diagnostic=ConstraintNotSatisfied parameter=T bound=FirNamed candidate-not-specialized=true
    requireNamed(value)
}

func validConjunctiveBounds(value: FirNamedNumber): FirNamedNumber {
    // expect: bounds.multiple.valid | Specialized | call=requireNamedAndComparable(value) type-arguments=[FirNamedNumber] parameters=[FirNamedNumber] result=FirNamedNumber constraints=[FirNamed:satisfied,FirComparable<FirNamedNumber>:satisfied]
    return requireNamedAndComparable(value)
}

func invalidSecondBound(value: FirNamedOnly): () {
    // expect: bounds.second.invalid | Rejected | call=requireNamedAndComparable(value) inferred=[FirNamedOnly] diagnostic=ConstraintNotSatisfied bound=FirComparable<FirNamedOnly> all-bounds-checked=true
    requireNamedAndComparable(value)
}

func invalidExplicitArgument(value: FirUnboundedValue): () {
    // expect: bounds.explicit-invalid | Rejected | call=requireNamed<FirUnboundedValue>(value) diagnostic=ConstraintNotSatisfied inference-bypassed=true validation-bypassed=false
    requireNamed<FirUnboundedValue>(value)
}

func explicitValidArgument(value: FirNamedNumber): String {
    // expect: bounds.explicit-valid | Specialized | call=requireNamed<FirNamedNumber>(value) type-arguments=[FirNamedNumber] parameters=[FirNamedNumber] result=String evidence=Explicit constraint=FirNamed:satisfied
    return requireNamed<FirNamedNumber>(value)
}

// 6. Mismatch and diagnostic ownership
// ------------------------------------
//
// Each negative call identifies the smallest conflicting evidence source. It must not emit a
// partially closed FIR call or silently substitute Object. A source diagnostic can coexist with
// independent positive specializations in this same file.

func <T> mergeSame(left: FirBox<T>, right: FirBox<T>): T = left.value

func conflictingValueBounds(left: FirBox<String>, right: FirBox<int>): () {
    // expect: mismatch.invariant-applications | Rejected | call=mergeSame(left,right) diagnostic=ConflictingInference parameter=T origins=[argument:left,argument:right] reason=InvariantConstructedType
    mergeSame(left, right)
}

func conflictingExactStorage(): () {
    var text: String = "text"
    var number: int = 9
    // expect: mismatch.ref-out-exact | Rejected | call=exchange(ref text,out number) diagnostic=ConflictingExactBounds parameter=T exact=[String,int] fir-call=absent
    exchange(ref text, out number)
}

func <T> exchange(ref left: T, out right: T): () { right = left }

func <T> exchangeWithValue(ref left: T, out right: T, extra: T): () { right = extra }

func mixedValueAndExactMismatch(): () {
    var count: int = 1
    // expect: mismatch.value-exact | Rejected | call=exchangeWithValue(ref count,out var result,"wrong") diagnostic=ConflictingInference parameter=T exact=[int] lower=[String]
    exchangeWithValue(ref count, out var result, "wrong")
}

func unresolvedOutputOnly(): () {
    // expect: mismatch.output-only | Rejected | call=constant() diagnostic=MissingGenericEvidence parameter=T result-context=none
    constant()
}

func incompatibleExplicitType(value: String): () {
    // expect: mismatch.explicit-argument | Rejected | call=specialize<int>(value) type-arguments=[int] diagnostic=ArgumentConversionFailure actual=String formal=int
    specialize<int>(value)
}

func explicitArityMismatch(value: int): () {
    // expect: mismatch.explicit-arity | Rejected | call=specialize<int,String>(value) diagnostic=GenericArityMismatch declared-arity=1 written-arity=2
    specialize<int, String>(value)
}

func conflictingInputAndExpectedResult(): () {
    // expect: mismatch.expected-bound | Rejected | call=specialize(1) expected=String inferred-input=[int] diagnostic=ConflictingInference parameter=T lower=[int] upper=[String]
    val invalid: String = specialize(1)
}

func wrongArrayElement(value: FirBox<String>): () {
    // expect: mismatch.invariant-array | Rejected | call=arrayIdentity<FirBox<int>>(value) type-arguments=[FirBox<int>] diagnostic=ArgumentConversionFailure actual=FirBox<String> formal=FirBox<int>
    arrayIdentity<FirBox<int>>(value)
}

func <T> arrayIdentity(value: T): T = value

func noResultTypeFallback(): () {
    // expect: mismatch.no-object-fallback | Rejected | call=constant() diagnostic=MissingGenericEvidence parameter=T fallback=none
    val absent = constant()
}

// 7. Recursive specialization and canonical in-progress instances
// ---------------------------------------------------------------
//
// Recursive source calls may refer back to the same closed function instance. The specialization
// worklist must publish a stable in-progress identity before traversing recursive call edges, then
// finish that identity exactly once. It must not recursively allocate an unbounded chain of equal
// signatures. This contract describes FIR construction only; termination of the source program is
// a separate runtime property.

func <T> recursiveIdentity(value: T, stop: bool): T {
    if stop { return value }
    return recursiveIdentity(value, true)
}

func recursiveInt(value: int): int {
    // expect: recursion.same-instance | Specialized | call=recursiveIdentity(value,false) type-arguments=[int] parameters=[int,bool] result=int instance=recursiveIdentity<int> recursive-edge=recursiveIdentity<int> target-state=InProgress identity-reused=true
    return recursiveIdentity(value, false)
}

func <T> leftRecursive(value: T, stop: bool): T {
    if stop { return value }
    return rightRecursive(value, true)
}

func <T> rightRecursive(value: T, stop: bool): T {
    if stop { return value }
    return leftRecursive(value, true)
}

func mutuallyRecursiveString(value: String): String {
    // expect: recursion.mutual-cycle | Specialized | call=leftRecursive(value,false) specializations=[leftRecursive<String>,rightRecursive<String>] cycle-closes-to=leftRecursive<String> duplicate-instance-count=0
    return leftRecursive(value, false)
}

func recursiveDifferentArgumentTypes(text: String, number: int): () {
    // expect: recursion.distinct-closed-types | Specialized | calls=[recursiveIdentity<String>,recursiveIdentity<int>] instances-distinct=true
    val first = recursiveIdentity(text, true)
    val second = recursiveIdentity(number, true)
}

func recursiveTypeGrowth(value: int): () {
    // expect: recursion.growing-instantiation | Deferred | call=grow(value) reason=SpecializationDepthLimit limit=128 partial-instance-published=false
    val invalid = grow(value)
}

func <T> grow(value: T): int = grow(FirBox(value))

// The bounded-recursion case is an oracle for a future recursively expanding family. It must be
// represented by a deterministic deferred/resource outcome, not stack overflow, process failure,
// candidate rejection, or an apparently successful partial FIR graph.

// 8. Unsupported generic owners, ref-like use, and callbacks
// ---------------------------------------------------------
//
// These cases prevent a free-function specialization test from accidentally claiming support for
// independent FIR features that are not implemented. A call outside the current boundary is
// explicitly Deferred (or rejected when a fully known language rule proves it invalid); it never
// gets credited as a specialized free-function call merely because its type arguments are visible.

class GenericFirOwner<T>(val value: T) {
    func <R> map(transform: (T) -> R): R = transform(value)
}

func genericOwnerCall(owner: GenericFirOwner<String>): () {
    // expect: unsupported.generic-owner | Deferred | call=owner.map(transform) reason=GenericOwnerMemberSpecializationUnsupported free-function=false owner-arguments=[String] method-arguments=[R=String]
    val invalid = owner.map({ text: String -> text })
}

ref struct FirBorrowed<T> {
    val value: T
}

func <T> preserveValue(value: T): T = value

func refLikePassThrough(value: FirBorrowed<int>): FirBorrowed<int> {
    // expect: unsupported.ref-like-signature | Deferred | call=preserveValue(value) type-arguments=[FirBorrowed<int>] reason=RefLikeFIRLayoutNotVerified body-safety=not-claimed
    return preserveValue(value)
}

func <T> heapArray(value: T): []T = [value]

func refLikeHeapElement(value: FirBorrowed<int>): () {
    // expect: unsupported.ref-like-array | Rejected | call=heapArray(value) type-arguments=[FirBorrowed<int>] diagnostic=RefLikeElementNotAllowed container=[]T body-specialization=invalid
    heapArray(value)
}

func <T> preserveScoped(value: T): T where T: allows ref struct = value

func refLikeAllowedByAntiConstraint(value: FirBorrowed<int>): FirBorrowed<int> {
    // expect: unsupported.ref-like-anticonstraint | Deferred | call=preserveScoped(value) type-arguments=[FirBorrowed<int>] constraint=allows-ref-struct satisfied body-escape-proof=unavailable
    return preserveScoped(value)
}

func <T, R> transform(value: T, callback: (T) -> R): R = callback(value)

func <T, R> transformFromCallback(callback: (T) -> R): R = callback(default<T>())

func callbackArgumentOnly(): () {
    // expect: unsupported.callback-only-evidence | Deferred | call=transformFromCallback(callback) missing=[T,R] reason=CallbackBodyCannotSeedCandidate parameter-context=unresolved
    val invalid = transformFromCallback({ $0 })
}

func callbackWithClosedValue(value: String): () {
    // expect: unsupported.contextual-callback | Deferred | call=transform(value,callback) known=[T=String] missing=[R] reason=LambdaResultFIRUnavailable lambda-context=(String)->R
    val invalid = transform(value, { text -> text.length })
}

func callbackWithTypedFunctionValue(value: String, callback: (String) -> int): int {
    // expect: unsupported.typed-callback | Deferred | call=transform(value,callback) candidate-type-arguments=[String,int] function-shape=(String)->int reason=GenericCallbackSignatureSpecializationUnsupported
    return transform(value, callback)
}

func callbackRefModeMismatch(value: int, callback: (ref int) -> int): () {
    // expect: unsupported.callback-mode | Rejected | call=transform(value,callback) diagnostic=FunctionParameterModeMismatch actual=ref expected=value
    transform(value, callback)
}

// 9. Explicit arguments, output contexts, and no cross-call leakage
// ---------------------------------------------------------------
//
// Explicit arguments and expected result types affect only the call that carries them. The same
// generic declaration may therefore produce different valid or invalid call outcomes without
// mutating other call nodes or the declaration's generic signature.

func explicitAndContextualCalls(value: String): () {
    // expect: context.explicit | Specialized | call=specialize<String>(value) type-arguments=[String] parameters=[String] result=String instance=specialize<String>
    val first = specialize<String>(value)
    // expect: context.inferred | Specialized | call=specialize(value) type-arguments=[String] parameters=[String] result=String instance=specialize<String>
    val second = specialize(value)
    // expect: context.int | Specialized | call=specialize<int>(8) type-arguments=[int] parameters=[int] result=int instance=specialize<int>
    val third = specialize<int>(8)
}

func independentExpectedResultContexts(): () {
    // expect: context.result.string | Specialized | call=constant() type-arguments=[String] result=String evidence=ExpectedResult instance=constant<String>
    val text: String = constant()
    // expect: context.result.int | Specialized | call=constant() type-arguments=[int] result=int evidence=ExpectedResult instance=constant<int>
    val number: int = constant()
}

func failedCandidateDoesNotPoisonLaterCall(value: FirUnboundedValue, text: String): String {
    // expect: context.failed-first | Rejected | call=requireNamed(value) diagnostic=ConstraintNotSatisfied candidate-state=local
    requireNamed(value)
    // expect: context.independent-second | Specialized | call=specialize(text) type-arguments=[String] result=String prior-candidate-state=discarded instance=specialize<String>
    return specialize(text)
}

func fileOrderDoesNotAffectInstanceIdentity(first: String, second: String): String {
    // expect: context.order.first | Specialized | call=specialize(first) type-arguments=[String] instance=specialize<String> candidate-order-independent=true
    val earlier = specialize(first)
    // expect: context.order.second | Specialized | call=specialize(second) type-arguments=[String] instance=specialize<String> canonical-instance=same-as-earlier
    val later = specialize(second)
    return later
}

// 10. Required assertions for a future semantic harness
// ----------------------------------------------------
//
// For every `Specialized` annotation, inspect the FIR call node and assert:
//
// - the target symbol is a free-function declaration with the stated generic owner;
// - every declaration-owned type parameter has exactly one closed substitution;
// - the parameter, result, nullable, constructed, and function types are substituted consistently;
// - ref/out modes and declared effect sets are preserved rather than inferred away;
// - each call node points to a canonical specialization identity and retains its source span;
// - repeated same-key calls share identity while distinct keys remain distinct;
// - recursive edges reuse the in-progress instance and the worklist reaches a stable result;
// - a failed constraint or conversion publishes no successful specialized call;
// - a deferred callback, owner, layout, or resource operation is not downgraded to success;
// - diagnostics identify the call and conflicting evidence origins without relying on declaration
//   order, source-file enumeration order, debug type IDs, or an Object fallback.
//
// These checks are stronger than parsing, AST construction, candidate collection, or a textual
// listing of inferred type arguments. FIR specialization is complete only when the call target,
// closed signature, effects, source identity, and specialization worklist state agree.
//
// No assertion in this file means that the current compiler passes the case. The file records the
// desired contract for the generic free-function FIR specialization milestone and must be wired to
// semantic tests before it can serve as regression evidence.
