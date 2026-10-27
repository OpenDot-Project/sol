// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative scenario matrix; runtime exception unwinding and full effect inference are not implemented.
module examples.semantics.generic_fir_effects

// Generic FIR effects
// ===================
//
// This source fixture states how a closed generic free-function instance carries a declared
// checked-effect set from source binding through FIR verification. It does not claim that every
// scenario below is accepted by the current compiler. Each `expect` annotation is a normative
// oracle for a semantic harness or a documented implementation boundary.
//
// `Checked` means a type outside RuntimeException's unchecked subtree that must fit a caller's
// declared/caught checked budget. `Unchecked` means a RuntimeException subtype; it does not add a
// required checked budget. `Open` is a generic parameter or a type containing one. `Closed` means
// every function-owned type parameter has one concrete, workspace-owned substitution.
//
// These examples focus on call binding and typed FIR metadata. A successful verified FIR call is
// not proof that the guest runtime can throw, catch, unwind, or run a cleanup handler. The current
// native runtime does not implement source-level exception unwinding.

class DiskFailure(message: String): Exception(message)
class NetworkFailure(message: String): Exception(message)
class TimeoutFailure(message: String): Exception(message)
class FatalRuntimeFailure(message: String): RuntimeException(message)

func readDisk(path: String): String throws DiskFailure = path
func readNetwork(host: String): String throws NetworkFailure = host
func readTimed(host: String): String throws TimeoutFailure = host
func readUnchecked(value: String): String throws FatalRuntimeFailure = value
func readClean(value: String): String = value

func <E: Throwable> identityWithEffect(value: String): String throws E = value

func <T, E: Throwable> transformWithEffect(
    value: T,
    callback: (T) -> String throws E
): String throws E = callback(value)

func <E: Throwable> applyWithEffect(
    callback: () -> String throws E
): String throws E = callback()

func <E: Throwable> applyTwoSameEffect(
    first: () -> String throws E,
    second: () -> String throws E
): String throws E = first()

func <E: Throwable, F: Throwable> applyTwoEffects(
    first: () -> String throws E,
    second: () -> String throws F
): String throws E, F = first()

func <T, E: Throwable> nestedFailure(
    value: T,
    callback: (T) -> String throws E
): String throws DiskFailure = callback(value)

// Direct substitution
// -------------------
//
// A selected generic function has one declaration identity and one ordered type-argument tuple.
// The `throws E` position is substituted from that same tuple. It is not erased merely because the
// native function pointer has no exception type in its machine-level signature.
//
//     func <E: Throwable> load(): String throws E = "cached"
//     func caller(): String throws DiskFailure = load<DiskFailure>()
//
// expect: explicit-effect-01 | Specialized | type-arguments=[DiskFailure]
//     | effects=[DiskFailure] | checked-budget=DiskFailure | call-origin=load<DiskFailure>()
//
// The function declaration's type parameter list is ordered. If a generic function has `T, E`,
// the stored arguments and the effects derive from the second argument, even if both arguments
// happen to have the same printed spelling or nominal declaration.
//
//     func <T, E: Throwable> load(value: T): String throws E = "cached"
//     func caller(): String throws DiskFailure = load<String, DiskFailure>("key")
//
// expect: explicit-effect-02 | Specialized | type-arguments=[String, DiskFailure]
//     | parameter-types=[String] | result=String | effects=[DiskFailure]
//
// An explicit effect argument does not prove that the body can execute a throw. The declaration
// is checked as source, and the generic instance is admitted only when its concrete body types,
// effects, supported operations, and FIR source obligations remain valid.
//
//     func <E: Throwable> noThrow(): String throws E = "value"
//     func caller(): String throws DiskFailure = noThrow<DiskFailure>()
//
// expect: explicit-effect-03 | Specialized | effects=[DiskFailure]
//     | note=declared effect metadata is conservative even when this body has no throw expression
//
// The compiler cannot replace a declared `DiskFailure` with `Throwable` to simplify machine
// lowering. Such widening changes function-type compatibility, checked-effect coverage, source
// metadata, and downstream diagnostics.

func inferredDiskEffect(): String throws DiskFailure {
    return applyWithEffect({ readDisk("cache") })
}

func inferredNetworkEffect(): String throws NetworkFailure {
    return applyWithEffect({ readNetwork("edge") })
}

// Inference through callback bodies
// ---------------------------------
//
// The source callback contributes a checked effect only after candidate-local body analysis has
// selected its calls. If the candidate is rejected, its effect facts are discarded with its other
// speculative facts.
//
//     func inferredDiskEffect(): String throws DiskFailure {
//         return applyWithEffect({ readDisk("cache") })
//     }
//
// expect: callback-effect-01 | SourceBinding | inferred-E=DiskFailure
//     | call-effects=[DiskFailure] | enclosing-budget=[DiskFailure]
//
// The FIR projection stores the closed target argument and its substituted declared effect list.
// The call remains anchored to the original lambda call and the selected generic declaration.
//
// expect: callback-effect-02 | FirCall | target-arguments=[DiskFailure]
//     | target-function-effects=[DiskFailure] | call-result=String
//
// A callback that cannot be analyzed is incomplete, not effect-free. An unresolved member,
// ambiguous overload, or unresolved nested call cannot be used as evidence for an empty set.
//
//     func unresolvedCallback(): String throws DiskFailure {
//         return applyWithEffect({ missingRead("cache") })
//     }
//
// expect: callback-effect-03 | Diagnostic | category=unresolved-call
//     | candidate-state=Incomplete | generic-instance=not-created
//
// Inference from an ordinary value position and an effect position must agree on the same ordered
// type argument. An effect cannot overwrite a concrete parameter fact.
//
//     func inferredTypeAndEffect(): String throws DiskFailure {
//         return transformWithEffect("cache", { path: String -> readDisk(path) })
//     }
//
// expect: callback-effect-04 | Specialized | type-arguments=[String, DiskFailure]
//     | parameter-types=[String, (String) -> String throws DiskFailure]
//
// A callback that returns the wrong value type does not become applicable just because its effect
// set matches. The function's ordered parameter/result shape is checked independently from the
// unordered effect set.
//
//     func wrongResult(): () {
//         val callback: () -> int throws DiskFailure = { readDisk("x") }
//     }
//
// expect: callback-effect-05 | Rejected | reason=function-result-mismatch
//     | effect-match-does-not-rescue-candidate=true
//
// Same generic effect at multiple callback sites
// -----------------------------------------------
//
// If one parameter E occurs in two callback function types, both actual checked effect sets must
// be compatible with the same E. The compiler cannot assign a separate hidden effect argument to
// each callback position.
//
//     func sameEffect(): String throws DiskFailure {
//         return applyTwoSameEffect(
//             { readDisk("first") },
//             { readDisk("second") }
//         )
//     }
//
// expect: shared-effect-01 | Specialized | type-arguments=[DiskFailure]
//     | formal-effect-positions=[E, E] | actual-effect-sets=[[DiskFailure], [DiskFailure]]
//
// Different actual effects cannot be merged merely by finding a common exception base. There is
// no implicit set-union rule that rewrites `E` into `Exception`.
//
//     func incompatibleSameEffect(): String {
//         return applyTwoSameEffect(
//             { readDisk("first") },
//             { readNetwork("second") }
//         )
//     }
//
// expect: shared-effect-02 | RejectedOrDeferred | reason=one-E-has-conflicting-actual-effects
//     | generic-instance=not-created | positional-pairing=forbidden
//
// If a future Sol function effect conversion permits a declared supertype budget, that conversion
// is checked after solving and is governed by the function-type contract. The compiler must not
// silently widen a generic type argument as an inference shortcut.

// Distinct effect parameters and unordered sets
// ----------------------------------------------
//
// E and F are owned by the same generic declaration but remain distinct by ordinal. Their spelling
// is not identity. The list `throws E, F` is an unordered set at the function-type boundary.
//
//     func twoEffects(): String throws DiskFailure, NetworkFailure {
//         return applyTwoEffects(
//             { readDisk("a") },
//             { readNetwork("b") }
//         )
//     }
//
// expect: distinct-effects-01 | Specialized | type-arguments=[DiskFailure, NetworkFailure]
//     | effects-set=[DiskFailure, NetworkFailure]
//
// Reversing source effect order does not change the selected type tuple or set membership.
//
//     func reversedEffects(): String throws NetworkFailure, DiskFailure {
//         return applyTwoEffects(
//             { readNetwork("b") },
//             { readDisk("a") }
//         )
//     }
//
// expect: distinct-effects-02 | Specialized | type-arguments=[NetworkFailure, DiskFailure]
//     | effects-set=[DiskFailure, NetworkFailure] | semantic-order-independent=true
//
// The ordered generic tuple still follows E then F. Only the effect set itself is unordered. A
// stable JSON presentation may sort normalized identities after the selected tuple is fixed.
//
//     func explicitReversedTuple(): String throws DiskFailure, NetworkFailure {
//         return applyTwoEffects<NetworkFailure, DiskFailure>(
//             { readNetwork("b") },
//             { readDisk("a") }
//         )
//     }
//
// expect: distinct-effects-03 | Specialized | type-arguments=[NetworkFailure, DiskFailure]
//     | effect-set-normalized=[DiskFailure, NetworkFailure]
//
// Two open effect patterns with multiple valid pairings are ambiguous or deferred. Declaration
// order, source order, hash iteration, diagnostic ordering, and cache ordering are not evidence.
//
//     func pairFromOneCall(): () {
//         applyTwoEffects({ readDisk("a") }, { readDisk("b") })
//     }
//
// expect: distinct-effects-04 | AmbiguousOrDeferred | possible-tuples-not-collapsed=true
//     | instance-created=false

// Nested constructed effect identities
// -------------------------------------
//
// If an exception declaration is generic, its constructed arguments participate in identity.
// `ReadFailure<String>` and `ReadFailure<int>` are not interchangeable merely because they have
// the same declaration name.
//
//     class ReadFailure<T>(val item: T): Exception("read")
//     func <T> readTyped(value: T): T throws ReadFailure<T> = value
//     func typedCaller(): String throws ReadFailure<String> = readTyped("item")
//
// expect: constructed-effect-01 | Specialized | type-arguments=[String]
//     | effects=[ReadFailure<String>] | result=String
//
// A second call with `int` gets a distinct type identity and a distinct specialization key.
//
//     func integerCaller(): int throws ReadFailure<int> = readTyped(4)
//
// expect: constructed-effect-02 | Specialized | type-arguments=[int]
//     | effects=[ReadFailure<int>] | result=int | key-differs-from=constructed-effect-01
//
// A base exception with a different generic argument is not an exact effect match. If a declared
// conversion rule admits a subtype, it must be applied after exact source selection and must not
// mutate the canonical instance key.
//
//     func badTypedBudget(): String throws ReadFailure<int> = readTyped("item")
//
// expect: constructed-effect-03 | Rejected | reason=caller-budget-does-not-cover-closed-effect
//     | instance-body-may-be-shared=true | call-site-remains-invalid=true

// Caller budgets and checked/unchecked classification
// ----------------------------------------------------
//
// A checked closed effect must be caught or declared at the selected caller. The function's
// generic body does not erase this obligation when converted to a native call.
//
//     func missingBudget(): String = applyWithEffect({ readDisk("x") })
//
// expect: caller-budget-01 | Rejected | reason=DiskFailure-not-handled-or-declared
//     | FIR-program-created=false
//
// A wider caller budget can cover a narrower effect only through the source exception conversion
// relation. The matching and diagnostic use constructed type identity and source spans.
//
//     func coveredBudget(): String throws Throwable = applyWithEffect({ readDisk("x") })
//
// expect: caller-budget-02 | SourceBinding | coverage=checked-by-exception-hierarchy
//     | actual-call-effect=[DiskFailure] | declared-budget=[Throwable]
//
// RuntimeException and descendants are unchecked. They do not become an inferred checked E just
// because the callback throws them. The runtime failure remains semantically possible; only the
// mandatory checked-budget rule differs.
//
//     func uncheckedOnly(): String = applyWithEffect({ readUnchecked("x") })
//
// expect: unchecked-effect-01 | TypeArgument=FatalRuntimeFailure
//     | required-checked-budget=[] | unchecked-runtime-behavior-preserved=true
//
// A callback with both a checked and unchecked failure contributes its checked set to the generic
// effect parameter. It does not infer the unchecked type as a second checked exception.
//
//     func checkedAndUncheckedBudget(): String throws DiskFailure {
//         return applyWithEffect({
//             if (true) { readDisk("x") } else { readUnchecked("x") }
//         })
//     }
//
// expect: unchecked-effect-02 | Specialized | checked-effects=[DiskFailure]
//     | unchecked-effect-recording=implementation-defined | no-budget-for-FatalRuntimeFailure=true
//
// Effect inference is not a route to turn a checked type into a RuntimeException or to relabel a
// RuntimeException as checked. Exception ancestry comes from declaration analysis, not naming.
//
// expect: unchecked-effect-03 | Rule | classification-source=resolved-exception-hierarchy
//     | name-based-classification=forbidden

// Caller-side catches and exceptional flow
// -----------------------------------------
//
// Catch analysis is a source flow rule. A catch removes only the covered closed effect from the
// normal continuation's escaping set. It does not rewrite the generic function's declared effect
// metadata or the call's source identity.
//
//     func caughtDisk(): String {
//         try {
//             return applyWithEffect({ readDisk("x") })
//         } catch (error: DiskFailure) {
//             return "recovered"
//         }
//     }
//
// expect: caller-catch-01 | SourceAnalysis | escaping-effects=[]
//     | generic-target-effects=[DiskFailure] | call-origin-preserved=true
//
// A catch for `NetworkFailure` does not remove `DiskFailure`. A catch for a declared base may
// remove a subtype if the resolved conversion relation proves coverage.
//
//     func wrongCatch(): String throws DiskFailure {
//         try { return applyWithEffect({ readDisk("x") }) }
//         catch (error: NetworkFailure) { return "unreachable" }
//     }
//
// expect: caller-catch-02 | SourceAnalysis | escaping-effects=[DiskFailure]
//     | declared-budget-satisfied=true
//
// The current FIR model records declared call effects and verifies checked caller budgets. The
// current native pipeline does not lower source catch regions or exceptional CFG edges. A future
// exception-flow FIR must preserve both normal and exceptional successors before native EH can be
// claimed.
//
// expect: caller-catch-03 | NativeLowering | catch-supported=false
//     | reason=exceptional-control-flow-not-implemented

// Nested generic calls and caller substitution
// --------------------------------------------
//
// A generic caller instance substitutes its own type variables into the type arguments of nested
// generic calls before the target instance key is formed. A nested call cannot retain a parameter
// identity owned by the caller after the caller is closed.
//
//     func <E: Throwable> relay(): String throws E = applyWithEffect<E>({ "ready" })
//     func diskRelay(): String throws DiskFailure = relay<DiskFailure>()
//
// expect: nested-effect-01 | outer-instance=[DiskFailure]
//     | inner-instance=[DiskFailure] | inner-target-effects=[DiskFailure]
//
// If the source callback creates a different concrete effect than E, source candidate checking
// rejects or defers the candidate before FIR. The FIR builder must not patch an inconsistent
// target argument after overload selection.
//
//     func mismatchedRelay(): String throws DiskFailure {
//         return relay<DiskFailure>()
//     }
//
// expect: nested-effect-02 | RejectedOrDeferred | source-candidate-is-authoritative=true
//     | no-post-selection-effect-rewrite=true
//
// For `T` and `E` declared by one function, nested substitution is simultaneous and ordinal-based.
// Replacing T with a type that contains E must not recursively rewrite the replacement unless the
// type-substitution contract expressly defines such a step.
//
// expect: nested-effect-03 | Rule | substitution=simultaneous
//     | parameter-identity=declaration-owner-plus-ordinal
//
// Function values and callbacks remain separate from direct generic calls. A function value that
// requires reified selection, closure conversion, or an unsupported effect adapter cannot be
// admitted merely because its declared effect list is closed.
//
// expect: nested-effect-04 | RejectedOrDeferred | reason=callback-ABI-not-in-native-subset
//     | source-call-inference-may-still-be-available=true

// Overload selection and candidate isolation
// -------------------------------------------
//
// Generic effect arguments belong to a candidate-local inference transaction. Losing candidates
// cannot publish type arguments, effect facts, diagnostics, or synthetic FIR functions.
//
//     func select<T, E: Throwable>(value: T, callback: (T) -> String throws E): String throws E
//     func select<T>(value: T, callback: (T) -> String): String
//
// expect: overload-effects-01 | Rule | effect-list-is-not-overload-identity=true
//     | selected-candidate-defines-effect-argument=true
//
// An overload with a better parameter conversion can win even if another candidate declares
// fewer effects, subject to checked-effect compatibility. Effect cardinality is not a ranking
// score, and a candidate's effect-list ordering is never a ranking score.
//
// expect: overload-effects-02 | Rule | parameter-ranking-before-effect-budget=true
//     | effects-only-overload-tie-break=forbidden
//
// If two candidates remain ambiguous, no generic FIR instance may be created from either
// candidate. Failed candidate attempts must leave the specialization queue unchanged.
//
// expect: overload-effects-03 | Ambiguous | created-instances=[]
//     | loser-effect-facts-leak=false | loser-diagnostics-leak=false
//
// Candidate replay may inspect lambda bodies more than once under different expected function
// types. Each replay must restore flow state, including caught effects and assigned out variables.
//
// expect: overload-effects-04 | Rule | lambda-state-is-candidate-local=true
//     | failed-candidate-state-restored=true | selected-candidate-effects-committed-once=true

// Constraints, ref-like types, and native ABI admission
// ----------------------------------------------------
//
// A generic exception parameter E must satisfy the source constraint that makes it a Throwable.
// The verifier independently checks the constraint for each closed FIR function instance.
//
//     func <E: Throwable> callEffect(): String throws E = "ready"
//
// expect: constraint-effects-01 | DiskFailure | Satisfied
// expect: constraint-effects-02 | int | Rejected
//
// A type argument supported by source generics is not automatically supported by this native
// bootstrap ABI. The admitted first slice is limited to verified scalar/String types and one-level
// arrays already supported by FIR. Nullable, half, quad, general nominal, ref-like, nested-array,
// function, or unsupported aggregate representations remain outside that boundary.
//
// expect: abi-effects-01 | []byte | ClosedEffectMayBind=true | NativeSpecialization=Supported
// expect: abi-effects-02 | []ReadFailure | ClosedEffectMayBind=true | NativeSpecialization=Rejected
// expect: abi-effects-03 | half | ClosedEffectMayBind=true | NativeSpecialization=Rejected
// expect: abi-effects-04 | ref-struct | ClosedEffectMayBind=true | NativeSpecialization=Rejected
//
// A rejected native specialization must identify its generic call or declaration origin and report
// an unsupported-subset/resource diagnostic. It must not fabricate an empty effect list, a body
// returning the default value, or an extern declaration with an open type.
//
// expect: abi-effects-05 | Rule | fallback-body=forbidden
//     | open-effect-parameter-in-verified-FIR=forbidden
//
// Ref/out parameters preserve their exact modes after type substitution. Effect coverage is
// orthogonal: a function with `out T` and `throws E` must prove both output assignment on normal
// returns and the checked effect budget at call sites.
//
// expect: abi-effects-06 | Rule | exact-ref-out-type=true
//     | effect-coverage-does-not-prove-out-assignment=true
//     | definite-assignment-does-not-prove-effect-coverage=true

// Source binding and FIR evidence
// -------------------------------
//
// The source `BodyCallBinding` stores declaration-ordered type arguments, specialized parameter
// types, result type, argument mapping, and default positions. A generic `FirCall` adds the closed
// target type argument IDs. Its target `FirFunction` carries the same specialization tuple and a
// body whose expression, slot, result, and effect types have been substituted.
//
// expect: fir-metadata-01 | binding.type-arguments=[DiskFailure]
//     | call.type-arguments=[DiskFailure] | target.specialization-arguments=[DiskFailure]
//     | binding.return-type=String | target.effects=[DiskFailure]
//
// The verifier compares the source call and target specialization under the caller's own
// substitution. For a generic caller, it applies that caller map to the source call's open target
// arguments before comparing the concrete FIR target. Comparing printed names is insufficient.
//
// expect: fir-metadata-02 | Rule | source-origin=exact-call-site
//     | target-origin=selected-source-declaration
//     | generic-identity=declaration-plus-ordered-closed-argument-identities
//
// The verifier checks the target's closed type argument ownership, generic arity, applicable
// Throwable constraints, substituted source signature, substituted declared effects, call result,
// exact argument types, passing modes, checked budgets, and flow proof. A serialized FIR document
// is for inspection; it is not a capability accepted by the LLVM backend.
//
// expect: fir-metadata-03 | Rule | verified-capability-required=true
//     | FIR-JSON-reparse-as-verified-program=forbidden
//
// The JSON inspection schema is version 4. Each call and function includes type identity IDs for
// generic arguments. IDs are compilation-local and do not define a stable artifact ABI.
//
// expect: fir-metadata-04 | Rule | schema=5
//     | host-paths-serialized=false | type-IDs-stable-across-sessions=false

// Library, artifact, and entry boundaries
// ----------------------------------------
//
// Generic function specialization does not change `Main.main([]String): ()`. The entry remains a
// nongeneric object method, and generic arguments cannot select a different process entry.
//
// expect: artifact-effects-01 | Rule | entry-type-arguments=[]
//     | generic-entry=Rejected | alternate-entry-option=not-added
//
// A closed internal instance may be emitted only when a direct statically selected source call
// caused it. This slice does not provide a separately importable generic code template, a generic
// C ABI export, a stable generic symbol name, or cross-version instance linking.
//
// expect: artifact-effects-02 | Rule | automatic-public-generic-export=false
//     | stable-mangled-ABI-name=false | versioned-cache-key-required-for-future=true
//
// A public function that uses an internal closed instance is subject to the ordinary library
// artifact visibility and effect rules. The generic declaration's implementation body must not
// leak into metadata as a fake nongeneric callable.
//
// expect: artifact-effects-03 | Rule | generic-declaration-exported-as-C=false
//     | internal-instance-origin-retained=true
//
// Static/dynamic SolRT selection and AOT/ORC backend selection do not change the closed type
// identity or checked effect set. Each output path must consume the same verified FIR instance.
//
// expect: artifact-effects-04 | Rule | AOT-and-ORC-source-binding-equal=true
//     | SolRT-linkage-does-not-change-type-arguments=true

// Resource bounds and cancellation
// -------------------------------
//
// The current builder caps distinct specialized functions in one compilation at 4096 and the
// nested closed type depth at 128. Type substitution has its own workspace resource limit. These
// bounds are not interchangeable: consuming one does not silently grant more of another.
//
// expect: bounds-effects-01 | Rule | maximum-specialized-functions=4096
//     | maximum-type-depth=128 | type-workspace-limit=independent
//
// Reaching a specialization bound yields `SOL2403` at the requesting source call. It is not reported
// as a successful empty function, an unconstrained type, a different overload, or an empty
// checked-effect set.
//
// expect: bounds-effects-02 | Limit | diagnostic=SOL2403
//     | generic-instance-created-for-over-limit-request=false
//
// Cancellation is checked before and during function graph expansion and body construction. A
// canceled compilation has no verified output and must not publish a partially drained queue.
//
// expect: bounds-effects-03 | Cancelled | verified-program=null
//     | partial-specialization-queue-published=false

// Boundaries that remain planned
// ------------------------------
//
// This fixture does not claim full specialization. The current implementation projects existing
// source body facts through a closed substitution and admits only the verified FIR/native type
// subset. The full contract still requires concrete semantic rebinding where substitution can
// change operator/member applicability, flow, effects, constraints, capture, or storage decisions.
//
// expect: planned-effects-01 | Requirement | rebind-body-under-concrete-types=true
//     | current-implementation=partial
//
// The following features remain outside this first native slice: generic owners and methods,
// constructor/property/operator specialization, virtual or indirect generic calls, reified type
// tokens, closure conversion, generic cross-module templates, source `catch` lowering, `finally`,
// cleanup effect propagation, and native exception unwinding.
//
// expect: planned-effects-02 | Unsupported | generic-method=true
// expect: planned-effects-03 | Unsupported | source-catch-native-lowering=true
// expect: planned-effects-04 | Unsupported | native-checked-exception-unwinding=true
//
// The current generic-inference subsystem supports an unambiguous single open effect pattern in
// its bounded body-analysis case. Multi-open unordered set matching, unique assignment proofs for
// constructed exception patterns, and full cross-lambda fixed-point coverage are not implied by
// the FIR metadata change.
//
// expect: planned-effects-05 | Deferred | two-open-effect-patterns=true
// expect: planned-effects-06 | Deferred | source-order-pairing=true
//
// The exception hierarchy remains a source semantic contract. This native subset records
// substituted effect IDs and verifies caller budgets; it does not synthesize C++ exceptions or
// transform `catch` into LLVM landing pads.
//
// expect: planned-effects-07 | Rule | guest-exception-unwind-implementation=false
//     | checked-effect-metadata-verification=true
