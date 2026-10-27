// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative source scenarios; the bounded FIR implementation covers only the marked closed
// primitive/String/one-level-array shapes. Full body rebinding and arbitrary graph semantics remain planned.
module examples.semantics.generic_fir_nested_substitution

// Nested substitution composition
// ================================
//
// This fixture focuses on one compiler boundary: a selected generic caller has its own closed
// substitution, and a direct generic call in that body has a second declaration-owned substitution.
// The inner type arguments may be written explicitly, inferred from the caller's specialized
// values, or inferred from an expected result. Every expected instance is still associated with the
// declaration selected by source overload resolution. A same-spelled parameter in another function
// is a different identity until a selected call maps one to the other.
//
// `instance` text below describes the semantic identity, not a stable integer ID. FIR/JSON IDs are
// local to one compilation. These annotations specify intended semantics; reading or parsing this
// file does not claim that every case is accepted by the current compiler.

// Shared free-function declarations
// ----------------------------------

func <T> identity(value: T): T = value

func <T, U> chooseFirst(first: T, second: U): T = first

func <T, U> chooseSecond(first: T, second: U): U = second

func <T> boxArray(values: []T): []T = values

func <T> makeArray(): []T = []

func <T> produce(): T

func <T> accept(value: T): () {}

func <T> exact(value: T): T = value

func <T: std.core.Object> constrained(value: T): T = value

func <T> objectOrValue(value: T): T = value

// A new generic invocation inside a generic declaration
// -----------------------------------------------------

// expect: nested_inferred_identity | Selected | outer=[int] | inner=[int] | result=int
func <Outer> forwardInferred(value: Outer): Outer = identity(value)

// The inner identity parameter belongs to `identity`; it does not alias Outer by spelling.
//
// Composition:
//   outer-map: Outer@forwardInferred/0 -> int
//   call-map:  T@identity/0 -> Outer@forwardInferred/0
//   closed inner instance: identity<int>
//
// FIR identity must use the closed target argument after composing both maps. It must not emit
// `identity<Outer>` as an open function or compare the two parameter names as equal strings.

// expect: nested_explicit_identity | Selected | outer=[String] | inner=[String] | result=String
func <Outer> forwardExplicit(value: Outer): Outer = identity<Outer>(value)

// Explicit type arguments are parsed and resolved in the lexical context of the caller. The token
// `Outer` names the caller's parameter; it is not the inner declaration's own parameter identity.
// Substituting Outer with String closes the callee's T only for this selected instance.

// expect: same_spelling_different_owner | Selected | outer T=[long] | inner T=[long] | result=long
func <T> sameSpelling(value: T): T = identity(value)

// Both declarations use the source spelling T. Their canonical identities differ because their
// owners differ. The call relation, rather than spelling equality, maps the inner argument to the
// outer parameter and later to the closed long argument.

// expect: ordered_arguments_forwarded | Selected | outer=[int, String] | inner=[String, int]
func <A, B> reorder(valueA: A, valueB: B): B = chooseSecond<B, A>(valueB, valueA)

// Expected inner arguments follow the inner declaration's order. They are not copied from the
// outer substitution vector by index: the explicit call maps B to inner T and A to inner U.

// expect: reorder_inferred | Selected | outer=[byte, String] | inner=[String, byte] | result=byte
func <A, B> reorderWithInference(valueA: A, valueB: B): A = chooseFirst(valueB, valueA)

// The selected argument mapping identifies which call parameter supplied each piece of evidence.
// The source order of values does not change the declaration order of inferred type arguments.

// Nested chain with distinct declaration owners
// ---------------------------------------------

func <Middle> middle(value: Middle): Middle = identity(value)

func <Outer> outer(value: Outer): Outer = middle(value)

// expect: three_declaration_chain | Selected | outer=[int] | middle=[int] | identity=[int]
func threeLayer(value: int): int = outer(value)

// The graph for the int root contains exactly one key per declaration and closed argument tuple:
//   outer<int>
//   middle<int>
//   identity<int>
// One shared `identity<int>` body serves all call sites in this source compilation. Each call
// expression retains its own origin and target edge even when the target instance is shared.

// expect: chain_string | Selected | outer=[String] | middle=[String] | identity=[String]
func threeLayerString(value: String): String = outer(value)

// `outer<int>` and `outer<String>` are distinct instances. The shared declarations are not cloned
// into source declarations and cannot receive new public names, annotations, or accessibility.

func <First, Second, Third> threeHop(value: First, ignored: Second, returned: Third): First =
    identity(value)

// expect: unused_parameters_do_not_shift_ordinals | Selected | outer=[long, String, byte] | inner=[long]
func useOnlyFirst(value: long, ignored: String, other: byte): long = threeHop(value, ignored, other)

// The inner function's argument list contains First at declaration ordinal zero. The unused outer
// parameters remain part of the outer instance key and do not alter the inner function's one-slot
// generic argument tuple.

// Expected-result evidence through a nested call
// ----------------------------------------------

func <T> emptyArray(): []T = []

func <T> getEmptyArray(): []T = emptyArray()

// expect: nested_expected_array | Selected | outer=[int] | inner=[int] | result=[]int
func expectedArrayContext(): []int = getEmptyArray()

// The expected result for getEmptyArray first closes its T. The inner call's expected type is the
// already-substituted `[]int`; its independent inference then closes emptyArray's T to int.
// Expectations from one generic declaration must not be globally attached to same-named type
// parameters in any other declaration.

func <T> makeOne(): T = produce()

// expect: nested_expected_scalar | Selected | outer=[String] | inner=[String] | result=String
func expectedScalarContext(): String = makeOne()

// Expected type travels through each selected call edge one body at a time. A failed or ambiguous
// inner candidate cannot be converted into a successful outer result by copying the expected type
// blindly.

func <T> passArray(values: []T): []T = boxArray(values)

// expect: nested_expected_and_input_agree | Selected | outer=[byte] | inner=[byte] | result=[]byte
func arrayInputAndResult(values: []byte): []byte = passArray(values)

// Input and expected-result evidence for one selected call must agree with the same inner identity.
// When they conflict, the compiler reports the conflict at the source call and emits no partial FIR
// edge to an instance created with only a subset of the evidence.

// Same parameter spelling across nested overloads
// -----------------------------------------------

func <T> overloaded(value: T): T = value

func overloaded(value: String): String = value

func <T> callOverload(value: T): T = overloaded(value)

// expect: selected_generic_overload | Selected | target=overloaded<T@callOverload> | instance=overloaded<int>
func callOverloadWithInt(value: int): int = callOverload(value)

// expect: selected_exact_overload | Selected | target=overloaded(String) | no-generic-instance
func callOverloadWithString(value: String): String = callOverload(value)

// The source binder first selects the exact nongeneric String overload. No generic specialization
// key may be installed for the losing `overloaded<T>` candidate. The call origin still points to the
// selected source expression and maps the original argument exactly once.

func <T> convertByIdentity(value: T): T = overloaded(value)

// expect: generic_candidate_for_int | Selected | generic-target | instance=overloaded<int>
func genericOverloadInt(value: int): int = convertByIdentity(value)

// The two generic declarations above each spell T but own different parameter identities. A
// temporary applicability result for one cannot be reused to make the other win.

// Explicit and inferred mappings in one body
// ------------------------------------------

func <T> explicitThenInferred(value: T): T = identity<T>(identity(value))

// expect: mixed_explicit_inferred | Selected | outer=[int] | inner-calls=[int,int]
func mixedMapping(value: int): int = explicitThenInferred(value)

// The explicit outer inner-call argument fixes one callee parameter; the nested call uses evidence
// from its value argument. Both results still attach to separate source call nodes.

func <T> explicitPair(value: T): T = chooseFirst<T, T>(value, value)

// expect: explicit_pair | Selected | inner=[String,String]
func explicitPairString(value: String): String = explicitPair(value)

func <T> inferredPair(value: T): T = chooseFirst(value, value)

// expect: inferred_pair | Selected | inner=[String,String]
func inferredPairString(value: String): String = inferredPair(value)

// A call with fewer inferred parameters than the target declaration arity is not closed. It is a
// binding error/deferred result, not permission to use Object as an invented fallback argument.

func <T, U> missingInnerEvidence(value: T): T = chooseFirst(value, produce())

// expect: missing_inner_evidence | Deferred-or-Rejected | no-fir-instance-with-open-U
func missingInnerEvidenceAtRoot(value: int): int = missingInnerEvidence(value)

// There is intentionally no runtime call for the declaration above in the current subset. The
// contract requires the generic call to remain unresolved unless the producer's result evidence
// uniquely determines U under the selected candidate.

// Owner/member boundaries and source-only declarations
// ----------------------------------------------------

class GenericOwner<T> {
    func relay(value: T): T = value
    func <U> relayAgain(value: U): U = value
}

// expect: generic_owner_method | Source-bindable | FIR-rejected-until-owner-specialization
func ownerMethodBoundary(owner: GenericOwner<int>): int = owner.relay(1)

// expect: generic_owner_generic_method | Source-bindable | FIR-rejected-until-owner-specialization
func ownerAndMethodBoundary(owner: GenericOwner<int>, value: String): String = owner.relayAgain(value)

// The native tranche admits only module-level free-function specializations. A closed method type
// argument does not close the owner layout, receiver, virtual dispatch, property storage, or access
// path. The presence of an owner argument must not create a fake free-function key.

class GenericBox<T>(val item: T)

func <T> readBox(box: GenericBox<T>): T = box.item

// expect: constructed_nominal_projection | Source-inference-contract | current-native-boundary-unresolved
func readIntBox(box: GenericBox<int>): int = readBox(box)

// The source inference engine may project an actual constructed nominal type to a generic formal.
// That fact alone does not authorize native layout, storage, constructor or member specialization.

// Open caller types are not closed instances
// ------------------------------------------

func <T> openForward(value: T): T = identity(value)

// expect: declaration_only_open_forward | No-instance-until-call-site-closes-T
func <T> keepOpen(value: T): T = openForward(value)

// A generic declaration body is a template context. `openForward<T>` is not inserted into a
// verified FIR program as a concrete instance just because its body can be source-analyzed.
// Specialization starts only after an actual root call closes the generic parameters.

func <T, U> crossForward(value: T, replacement: U): U = identity(replacement)

// expect: caller_substitution_one_parameter | inner=[String] | unrelated_outer_T_not_copied
func crossForwardRoot(value: int, replacement: String): String = crossForward(value, replacement)

// The callee's identity call mentions only U. The first outer type parameter is not included in
// the inner instance key unless it appears in the inner declaration's own ordered arguments.

// Error recovery does not publish half-closed instances
// ------------------------------------------------------

func <T> conflictInner(left: T, right: T): T = left

func <Outer> conflictForward(left: Outer, right: int): Outer = conflictInner(left, right)

// expect: nested_conflicting_evidence | Rejected | no-instance | diagnostic-at-inner-call
func conflictingRoot(left: String, right: int): String = conflictForward(left, right)

// The outer function body itself may have a source-level type parameter, but its concrete
// substitution makes the inner inputs String and int. If the selected inner contract requires one
// exact invariant T for both inputs, the candidate is rejected. No losing candidate leaves behind
// an instance-cache entry that a later call could accidentally reuse.

func <Outer> unresolvedForward(value: Outer): Outer = missingInnerEvidence(value)

// expect: nested_deferred_candidate | Deferred | parent-not-verified
func unresolvedRoot(value: int): int = unresolvedForward(value)

// Deferred inner analysis blocks the parent instance when it could change the selected result,
// overload, effects, flow facts or argument mapping. A diagnostic recovery node is not a valid FIR
// call target.

// Constraints are checked at the selected declaration boundary
// ------------------------------------------------------------

func <T: std.core.Object> objectIdentity(value: T): T = value

func <T: std.core.Object> objectForward(value: T): T = objectIdentity(value)

// expect: object_constraint_preserved | Selected-if-both-source-and-verifier-prove-bound | instance=String
func constrainedString(value: String): String = objectForward(value)

// The outer declaration's bound does not grant a stronger bound to an unrelated inner declaration.
// The inner call must independently satisfy its own bounds after composing the outer substitution.

func <T> unconstrainedForward(value: T): T = objectIdentity(value)

// expect: unconstrained_caller_inner_object_bound | Candidate-proof-required | no-assumed-boxing
func sourceBoundBoundary(value: String): String = unconstrainedForward(value)

// A concrete String at the external root may satisfy an inner Object constraint once the caller
// specializes. The generic body still needs a valid source proof for forwarding arbitrary T; the
// compiler cannot use an assumption that all T values inherit Object unless the language bound says so.

// Recursive exact-key edges and growing keys
// -------------------------------------------

func <T> recursiveExact(value: T, again: bool): T {
    if again { return recursiveExact(value, false) }
    return identity(value)
}

// expect: recursive_exact_key | Selected | recursiveExact<int> self-edge | identity<int>
func recursiveExactRoot(value: int): int = recursiveExact(value, true)

// The worklist reserves the key before lowering its body. The same exact declaration and closed
// argument tuple encountered on the back edge resolves to the reserved target ID. It neither
// creates a duplicate function nor recursively expands the source tree in memory.

func <T> mutuallyLeft(value: T, again: bool): T {
    if again { return mutuallyRight(value, false) }
    return value
}

func <T> mutuallyRight(value: T, again: bool): T {
    if again { return mutuallyLeft(value, false) }
    return identity(value)
}

// expect: mutual_exact_key_cycle | Selected | mutuallyLeft<int> <-> mutuallyRight<int>
func mutualCycleRoot(value: int): int = mutuallyLeft(value, true)

// Mutual calls share one closed instance per declaration+argument tuple. Function name alone is
// insufficient: the left and right declarations remain different nodes in the closed graph.

// A type-changing recursive edge is not an exact cycle
// ----------------------------------------------------

func <T> growArray(value: T, again: bool): T {
    if again { return growArray([value], false) }
    return value
}

// expect: expanding_type_edge | Rejected-or-resource-limited | no-infinite-instance-chain
func growArrayRoot(value: int): int = growArray(value, true)

// If a future admitted type constructor permits this call, its keys grow as
// growArray<int>, growArray<[]int>, growArray<[][]int>, and so on. An active-declaration set cannot
// treat these as an exact recursion cycle. The implementation needs a finite graph budget and an
// origin-rich failure. In the current FIR ABI, nested arrays are rejected before code generation.

func <T> growNullable(value: T?, again: bool): T? {
    if again { return growNullable(value, false) }
    return value
}

// expect: nullable_growing_specialization | Current-ABI-rejected | no-native-FIR
func growNullableRoot(value: int?): int? = growNullable(value, true)

// Source call origins and duplicated instance targets
// ---------------------------------------------------

func <T> firstIdentity(value: T): T = identity(value)

func <T> secondIdentity(value: T): T = identity(value)

func repeatedIdentity(value: int): int {
    val first = firstIdentity(value)
    val second = secondIdentity(value)
    return identity(first) + identity(second)
}

// expect: repeated_sites_one_inner_instance | Selected | identity<int> shared | three-call-origins-retained
func repeatRoot(value: int): int = repeatedIdentity(value)

// Instance deduplication must not merge the three source calls into one evaluation. Every call keeps
// its own source span, argument order, effect edge and selected target. Only the function body is
// shared. Side effects in argument expressions still execute once at their original call site.

func <T> sourceOriginForward(value: T): T = identity(value)

// expect: origin_after_clone | Source-origin-preserved | declaration=sourceOriginForward | body-node=identity-call
func originRoot(value: String): String = sourceOriginForward(value)

// A generated FIR instance may add a concrete signature and target ID, but diagnostic and inspection
// provenance points to the original source AST nodes. Cloned bodies do not pretend to come from a
// synthesized file, generated line, temporary directory, or host-specific path.

// Determinism and ID scope
// ------------------------

func deterministicOne(value: int): int = identity(value)

func deterministicTwo(value: String): String = identity(value)

// expect: same-input-repeat | Same ordered function graph and JSON bytes
func deterministicRoot(value: bool): bool {
    val left = deterministicOne(1)
    val right = deterministicTwo("x")
    return identity(value)
}

// Within the same source snapshot and compiler configuration, source traversal and FIFO discovery
// order produce repeatable local function IDs. A local type ID or function ID is not a persistent
// cache key and is not stable across unrelated workspaces or type insertion order.

func <T> declarationA(value: T): T = value

func <T> declarationB(value: T): T = value

// expect: declaration_identity_not_spelling | Different declarations | instance keys remain distinct
func distinctDeclarations(value: int): int = declarationA(value) + declarationB(value)

// Reordering two independent call sites may alter local discovery IDs if IDs are assigned in
// traversal order, but must not alter the semantic set of declaration/type argument keys or change
// which source call points to which target. Stable artifact/cache IDs require a separate versioned
// canonical encoder and are not promised by FIR JSON.

// Argument mapping before inner substitution
// -------------------------------------------

func <T, U> takeNamed(second: U, first: T): U = second

func <T> forwardNamed(value: T, other: String): String = takeNamed(first = value, second = other)

// expect: named_mapping_before_key | outer=[int] | inner=[int,String] | result=String
func namedForwardRoot(value: int): String = forwardNamed(value, "name")

// Named source order and declaration parameter order are distinct. The body binding records the
// mapping first; specialization arguments stay ordered as T,U. FIR lowers argument evaluation in
// source order while stores target arguments at the mapped formal positions.

func <T> refIdentity(ref value: T): T = value

// expect: generic_ref_exact_after_substitution | Supported-for-admitted-value | instance=int
func refRoot(): int {
    var value = 1
    return refIdentity(ref value)
}

func <T> outIdentity(out value: T): T {
    value = produce<T>()
    return value
}

// expect: generic_out_closed | Source-analysis-and-FIR-proof-required | instance=String
func outRoot(): String {
    var value: String
    outIdentity(out value)
    return value
}

// `ref` and `out` constraints remain exact after substitution. A type conversion must not be used
// to satisfy a by-reference parameter. The call write and caller flow facts refer to the concrete
// local slot and the selected generic instance.

// Current implementation boundary
// -------------------------------
//
// Implemented in the bounded first native slice:
//
// - call bindings retain ordered declaration-owned generic arguments;
// - direct module-level generic calls can reserve a closed instance for supported primitive,
//   String, and one-level-array native types;
// - a generic caller's current substitution is applied to a nested call's type arguments;
// - repeated exact keys share a function ID, while each source call keeps its own origin;
// - the independent FIR verifier checks closed arguments, constraints, substituted signatures,
//   effect identities, and call-to-instance consistency;
// - the same LLVM 23.1.3 C++ API path receives the verified monomorphic function graph.
//
// Incomplete in this implementation:
//
// - the body is projected from source analysis facts; a full source rebind/recheck for every
//   concrete substitution is not yet implemented;
// - generic owners and members, closures, open generic values, nullable/reference-layout types,
//   nested arrays, generic C ABI export and cross-container generic linking remain outside FIR;
// - the worklist's compilation/type bounds do not establish arbitrary recursive type growth,
//   persistent cache identity, or stable IDs across workspaces;
// - source analysis may bind a case that the native gate rejects. A binding result is not itself a
//   successful verification or emitted artifact.


// Substitution in local values and result positions
// -------------------------------------------------

func <T> identityBlock(value: T): T {
    val copy: T = value
    return identity(copy)
}

// expect: local_and_return_substitution | outer=[int] | slots=[int,int] | inner=[int]
func identityBlockRoot(value: int): int = identityBlock(value)

// The local's explicit T type is projected through the current function instance map before FIR
// slot allocation. The read uses that same concrete slot identity. Replacing only the function
// result while leaving local T open would make read/store proofs incomplete.

func <T> identityStatement(value: T): T {
    val copied = identity(value)
    return copied
}

// expect: inferred_local_after_nested_call | outer=[String] | local=String | returned=String
func inferredLocalRoot(value: String): String = identityStatement(value)

// Local inference follows the source binding of the nested call. It does not introduce a new type
// parameter or a second identity specialization merely because the local has its own origin.

func <T> branchForward(value: T, select: bool): T {
    if select { return identity(value) }
    return exact(value)
}

// expect: branch_calls_same_substitution | outer=[byte] | identity=[byte] | exact=[byte]
func branchRoot(value: byte): byte = branchForward(value, true)

// Each branch uses the same closed outer T. Control-flow joins do not merge distinct generic
// declaration identities or permit a branch-local type to leak into another call.

func <T> loopForward(values: []T): []T {
    for val value in values { identity(value) }
    return boxArray(values)
}

// expect: loop_and_result_calls | outer=[String] | identity=[String] | boxArray=[String]
func loopRoot(values: []String): []String = loopForward(values)

// The loop item has the concrete element type of the specialized source array. Its binding, nested
// call, and returned array projection retain separate origins and slot identities. This does not
// authorize nested arrays or arbitrary iterator protocols.

// Same instance key, different source paths through wrappers
// ---------------------------------------------------------

func <T> wrapperOne(value: T): T = identity(value)
func <T> wrapperTwo(value: T): T = identity(value)
func routeOne(value: int): int = wrapperOne(value)
func routeTwo(value: int): int = wrapperTwo(value)

// expect: shared_leaf_reached_by_two_paths | identity<int> one-instance | two-call-origins
func sharedLeafRoot(first: int, second: int): int = routeOne(first) + routeTwo(second)

// The graph builder can reach identity<int> through two wrapper bodies. The exact key shares the
// leaf body once; wrapperOne and wrapperTwo remain distinct declaration identities. Each call edge
// retains the target selected from its own source body.

func <T> wrapperThree(value: T): T = identity(value)

// expect: same_declaration_two_type_arguments | wrapperThree<int> distinct from wrapperThree<String>
func sameDeclarationDifferentArguments(value: int): String {
    val integer = wrapperThree(value)
    return wrapperThree("value")
}

// Instance equality is not declaration-only. Different closed tuples create distinct signatures;
// a call target must match the argument tuple selected at its source site.

// Overload selection is contextual to each nested call
// ----------------------------------------------------

func <T> preferred(value: T): T = value
func preferred(value: int): int = value
func <Outer> delegatePreferred(value: Outer): Outer = preferred(value)

// expect: source-select-before-instance | int-overload-wins | no-preferred<int>
func preferredRoot(value: int): int = delegatePreferred(value)

// Source overload rules run before a specialization-key lookup. An existing cache entry cannot
// influence ranking, and the absence of a generic instance cannot make a source-valid exact overload
// lose. For another concrete type, the selected generic declaration contributes a closed instance.

// expect: nested_generic_selected | declaration=preferred<T> | instance=preferred<String>
func preferredStringRoot(value: String): String = delegatePreferred(value)

// Multiple inner type parameters and independent projection
// --------------------------------------------------------

func <T, U> pairFirst(first: T, second: U): T = first
func <T, U> pairSecond(first: T, second: U): U = second

func <A, B> crossPair(first: A, second: B): B {
    val projected: B = pairSecond(first, second)
    val ignored: A = pairFirst(first, second)
    return identity(projected)
}

// expect: multi-owner-projection | outer=[int,String] | pairFirst=[int,String] | pairSecond=[int,String] | identity=[String]
func crossPairRoot(first: int, second: String): String = crossPair(first, second)

// The outer declaration owns A and B. Each inner generic declaration independently owns T and U.
// Substitution is built per target call; one ordinal map across owners would map inner variables
// positionally even when call evidence requires a different order.

func <A, B> swappedPair(first: A, second: B): A {
    val selected: A = pairFirst(second, first)
    return identity(selected)
}

// expect: argument-evidence-after-swap | outer=[byte,String] | pairFirst=[String,byte] | identity=[byte]
func swappedPairRoot(first: byte, second: String): byte = swappedPair(first, second)

// Inference follows actual-to-formal argument mapping. It is not safe to copy the caller's generic
// vector positionally. The nested type arguments and result have a distinct declaration order.

// Call-site source order remains observable
// -----------------------------------------

func <T> evaluateThenForward(first: T, second: T): T {
    val chosen = identity(first)
    val ignored = identity(second)
    return chosen
}

func sideEffect(value: String): String = value

// expect: call-order-preserved | identity<String> shared | source-order=first,second
func evaluationOrderRoot(): String = evaluateThenForward(sideEffect("a"), sideEffect("b"))

// Instance sharing only shares a body. It cannot hoist, reorder, merge, duplicate, or discard source
// argument evaluation. Both call expressions retain their source spans, argument mapping and effect
// facts even if they target one identity<String> instance.

func <T> callInCondition(value: T): bool = isPresent(identity(value))
func <T> isPresent(value: T): bool = true

// expect: nested-call-in-condition | each-call-has-selected-origin | result=bool
func conditionRoot(value: String): bool = callInCondition(value)

// A Boolean result context does not become evidence for identity's T. Expected-result evidence is
// attached only to the call whose result position can constrain the selected parameter.

func <T> terminalCall(value: T): T = identity(value)

// expect: local-value-name-does-not-capture | outer parameter T remains owner-scoped
func <T> localNamedT(value: T): T {
    val T = identity(value)
    return terminalCall(T)
}

// A local identifier whose spelling resembles a type parameter remains a value name resolved by
// lexical scope rules. It cannot alter canonical type-parameter ownership or a key's type tuple.

// Exact recursion and mutual recursion
// ------------------------------------

func <T> recursiveExact(value: T, again: bool): T {
    if again { return recursiveExact(value, false) }
    return identity(value)
}

// expect: recursive_exact_key | recursiveExact<int> self-edge | identity<int>
func recursiveExactRoot(value: int): int = recursiveExact(value, true)

func <T> mutuallyLeft(value: T, again: bool): T {
    if again { return mutuallyRight(value, false) }
    return value
}

func <T> mutuallyRight(value: T, again: bool): T {
    if again { return mutuallyLeft(value, false) }
    return identity(value)
}

// expect: mutual_exact_key_cycle | mutuallyLeft<int> <-> mutuallyRight<int>
func mutualCycleRoot(value: int): int = mutuallyLeft(value, true)

// Reserve a function key before its body is traversed. Exact self and mutual backedges resolve to
// existing IDs. They do not recursively clone the same body. Distinct declarations remain distinct
// graph nodes, even when their names or generic parameter spellings look similar.

func <T> growArray(value: T, again: bool): T {
    if again { return growArray([value], false) }
    return value
}

// expect: expanding_type_edge | nested-array-ABI-rejected | no-infinite-instance-chain
func growArrayRoot(value: int): int = growArray(value, true)

// A future ABI that admits the growing call would produce keys growArray<int>, growArray<[]int>,
// growArray<[][]int>, and so on. Declaration-only cycle detection would incorrectly call this a
// closed cycle. The current FIR subset rejects nested arrays before native code generation; a future
// expansion still needs a finite graph budget and an origin-rich failure rather than truncation.

// Argument maps and distinct declaration owners
// ----------------------------------------------

func <T, U> takeNamed(second: U, first: T): U = second
func <A> forwardNamed(value: A, other: String): String = takeNamed(first = value, second = other)

// expect: named_mapping_before_key | outer=[int] | inner=[int,String] | result=String
func namedForwardRoot(value: int): String = forwardNamed(value, "named")

// Named-argument source order and target declaration order differ. The source binder records the
// parameter mapping before specialization. The inner key stays in declaration order; FIR expression
// order still follows the source program's evaluation order.

func <T> firstIdentity(value: T): T = identity(value)
func <T> secondIdentity(value: T): T = identity(value)

// expect: declaration_identity_not_spelling | firstIdentity<int> distinct from secondIdentity<int>
func distinctDeclarations(value: int): int = firstIdentity(value) + secondIdentity(value)

// Both declarations spell their type parameter T, but the owners differ. One owner-to-argument map
// cannot stand for both declarations; each target identity and type tuple is retained separately.

// No persistent ID/cache promise
// ------------------------------

// expect: repeat_compile_same_snapshot | same local ordering and JSON for identical inputs
func repeatCompilation(value: String): String = identity(value)

// Local type IDs and FIR function IDs depend on a compilation workspace and deterministic input
// traversal. They are not persistent cache keys, public ABI IDs, or cross-workspace identities.
// Reordering independent source files can change local IDs while preserving the semantic set of
// function declaration/type argument keys and every call-to-target relationship.

// If a caller's substitution cannot close an inner type argument, no successful instance is
// published. The parent remains unsupported/deferred when the missing value could change target
// selection, return type, effects, flow facts, or the emitted function signature.
