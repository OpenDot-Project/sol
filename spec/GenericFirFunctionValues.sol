// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative function-value boundary; direct generic FIR calls are implemented in a bounded slice.
module source.spec.generic_fir_function_values

// Generic declarations, function types, lambdas, and closure conversion
// =====================================================================
//
// This contract separates four language objects that can share a source-level signature:
//
// 1. A generic function declaration has type parameters owned by a declaration.
// 2. A generic direct call selects that declaration and supplies closed type arguments.
// 3. A function value has one concrete function type, including passing modes and effects.
// 4. A lambda or anonymous function creates a function value and may capture lexical storage.
//
// These objects are not interchangeable. Emitting one closed direct-call instance does not imply
// that the compiler can erase, store, return, pass, capture, compare, or dynamically dispatch a
// generic function value. A lambda whose contextual parameter/result types are generic evidence is
// still a lambda expression with its own flow, effect, capture, and lifetime obligations.
//
// Current implementation status:
//
// - Source analysis constructs function types with parameter modes and checked-effect sets.
// - Named/positional lambdas are analyzed against expected function types in supported body-analysis
//   paths. Candidate-local generic inference may inspect bounded lambda parameter/result/effect
//   evidence and then analyze the selected lambda normally.
// - Direct calls to closed module-level generic free functions can produce verified monomorphic FIR
//   instances for the admitted native type/body subset.
// - FIR currently has direct `FirCall` and `FirFunction` nodes, but no function-reference, closure,
//   indirect-call, or generic-function-value node. Native admission rejects closures and
//   function-value specialization. A source-level function-type result does not bypass that gate.
// - Generic body substitution reuses already-bound source facts. It does not rerun full semantic
//   analysis for each substitution; any proof that depends on a callback's concrete behavior remains
//   outside the current generic native guarantee.
//
// The requirements below are normative language/compiler contracts. An expected outcome marked
// `Future` or `FIR: Rejected` is not an assertion that today's compiler accepts the complete example.

// 1. Generic declaration identity is owner-sensitive
// --------------------------------------------------
//
// A type parameter is identified by its declaration owner and ordinal. Its displayed name is not
// an identity. Function values preserve the resolved function type, not the textual spelling of a
// source generic parameter.

func <T> identity(value: T): T = value

func <U> passThrough(value: U): U = value

func <T, U> sameNameDifferentOwners(value: T, other: U): (T, U) = (value, other)

// At a direct call, selection determines one owner and one closed substitution. A local variable
// also named T or U cannot capture or replace a declaration-owned type parameter.
//
// A future generic function-value representation must preserve this owner identity until an
// explicit instantiation rule closes it. It must not infer identity from `identity`, `T`, or the
// function's printed signature.

// 2. Direct generic calls are monomorphic after selection
// ------------------------------------------------------

func directIdentityCall(): String = identity<String>("sol")

// expect: Source: Bound; direct generic target identity<String>; FIR: Ready only for the admitted
// String body/ABI subset; target carries one closed String argument.

func inferredIdentityCall(): int = identity(7)

// expect: Source: Bound; argument evidence closes T=int; FIR: Ready only when the selected body and
// concrete signature remain inside the current native subset.

// The direct call is selected before FIR instance creation. Once selected, the call points to the
// exact instance of the declaration plus the ordered type-argument identities. It does not carry a
// runtime dictionary or open type parameter. Recursive calls with the same instance key reuse it.
//
// The current implementation may specialize these calls. It must reject them before artifact
// emission if a concrete argument, return type, body operation, or required runtime representation
// is outside its verified subset.

// 3. A function type describes one closed callable shape
// -----------------------------------------------------
//
// Sol function types use the existing parameter/result syntax. Passing modes and declared checked
// effects are part of the callable contract. Function type effects are a set; their source order is
// not a type identity or matching algorithm.

val integerTransform: (int) -> int = { value -> value + 1 }
val textTransform: (String) -> String = { value -> value }
val integerExchange: (ref int, out int) -> () = { ref left: int, out right: int -> right = left }

// The type `(T) -> T` inside a generic declaration refers to that declaration's T. It is not a
// first-class universally quantified type unless a separate language rule says so. Today, function
// types are resolved structurally and may contain open parameter identities during source analysis;
// that fact alone does not define a legal runtime value representation.

func <T> useTypedParameter(value: T, operation: (T) -> T): T = operation(value)

// expect: Source typing can determine the direct signature; indirect invocation is not currently
// represented by verified FIR; native result: Rejected(SOL2400).

// 4. Assigning a generic declaration to a function value is not an ordinary direct call
// --------------------------------------------------------------------------------------
//
// A source name can refer to a generic overload group. Using that name in a value context requires
// a rule that determines which declaration and which concrete type arguments the value denotes.
// Ordinary direct-call inference is not sufficient: a function value may escape the expression and
// be invoked many times, after the original expected type and local inference context are gone.

func valueFromExpectedType(): () {
    val transform: (String) -> String = identity
}

// expect: Source: Deferred until generic-reference value conversion is specified; no monomorphic
// closure may be fabricated from the expected type implicitly.

func ambiguousValueGroup(): () {
    val chooser: (String) -> String = choose
}

func <T> choose(value: T): T = value
func choose(value: String): String = value

// expect: Source: Deferred unless the overload-to-value rule provides a unique candidate after
// conversion, effects, generic inference, and visibility; declaration order is not a tie breaker.

// When a future conversion is admitted, the selected value must state whether it is a closed
// monomorphic wrapper, an erased generic function plus runtime descriptors, or another explicitly
// specified representation. It cannot silently change the overload's evaluation, exception, or
// dispatch semantics.

// 5. Explicitly closing a generic function value needs a declared syntax rule
// ---------------------------------------------------------------------------
//
// Direct calls support explicit type arguments in the call syntax. The language has not defined a
// separate generic function-reference expression syntax in this contract. Implementations must not
// invent a cast, a suffix, a fabricated zero-argument call, or a hidden closure at a value site.
//
// A future proposal for explicit closure of a generic declaration must specify:
//
// - how the declaration is resolved when names are overloaded or imported;
// - how every generic parameter is assigned a closed, source-owned type identity;
// - whether an expected function type may supply additional constraints after explicit arguments;
// - how visibility, checked effects, ref/out modes and receiver binding are retained;
// - whether the result is a direct code pointer, a wrapper closure, or an erased dictionary call;
// - the stable source origin and diagnostic when the result cannot be represented.
//
// Until then, an attempted generic function reference remains Deferred or rejected at the phase
// that owns the missing rule. It must not enter the specialization work queue as if it were a direct
// call.

// 6. Expected types and generic call inference have different scopes
// -------------------------------------------------------------------
//
// At a direct call, evidence may come from explicit type arguments, actual arguments, exact ref/out
// locations, and the expected result under the documented generic inference rules. Candidate state
// is isolated and a losing overload cannot leak lambda facts into another candidate.
//
// For a function value, the target function type may constrain a candidate's parameter and result
// types, but selection is a value conversion, not a call. It must not evaluate parameters, defaults,
// varargs, or the function body. It must not fabricate argument evidence that would exist only when
// a later caller invokes the value.

func inferAtCallFromArgument(): int = identity(9)

func cannotInferEscapingValue(): () {
    val callback: (int) -> int = identity
}

// expect: The first form has an actual argument and may infer T=int. The second form requires the
// unimplemented generic-reference conversion rule; it is not equivalent to calling identity(0).

// An implementation must report ambiguity or Deferred when a value conversion has multiple valid
// generic substitutions. It cannot select a substitution using a declaration's default generic
// argument because Sol has no implicit default for type parameters in this contract.

// 7. Overload selection for a value is deterministic and non-executing
// ---------------------------------------------------------------------
//
// Given an expected function type, candidate filtering uses declaration visibility, arity, exact
// passing modes, parameter/result compatibility, and declared effect compatibility according to
// the separately specified function-type conversion rules. Ranking compares all candidates using
// the defined conversion vector and any generic-specificity tie rules. It never invokes a getter,
// default expression, lambda, or candidate body to discover a type.
//
// A single best candidate may be converted only if its generic parameters can be completely closed
// and its body can be emitted by the chosen representation. If no candidate applies, the value
// expression is rejected. If candidates tie, it is Ambiguous. If analysis is incomplete for a
// candidate, the result is Deferred instead of choosing a lower-ranked overload.
//
// A native rejection after source selection does not permit fallback to a lower-ranked function.
// A `String -> String` expected type cannot silently change a selected generic function into a
// non-generic overload merely because the generic body is not currently lowerable.

// 8. Lambdas are values with contextual typing and a separate body
// ------------------------------------------------------------------
//
// A lambda expected type supplies parameter types, modes, result context and effect budget where
// the source rules allow it. Named headers must agree with that context; positional `$0` references
// require an expected function type. There is no implicit `it`. The lambda body is checked under
// the resulting parameter identities, not by substituting a same-spelled outer type parameter.

func <T> mapOne(value: T, transform: (T) -> T): T = transform(value)

func contextualLambdaAtGenericCall(): String = mapOne("ab") { text -> text }

// expect: Candidate-local replay may derive callback evidence where the implementation supports
// it. Selected lambda analysis is a separate body-binding result; native generic specialization
// still rejects the indirect function-value call today.

func nestedLambdaScopes(): () {
    val nest: (int) -> (int) -> int = { outer -> { inner -> outer + inner } }
}

// Nested lambdas own separate parameter scopes. A nested `$0` starts from the inner lambda's
// positional namespace. A generic parameter from the enclosing function is not a lambda parameter.
//
// The current FIR builder has no closure/environment nodes. Source analysis can retain contextual
// facts without authorizing closure allocation or native code generation.

// 9. Lambda forms must converge on one function-value contract
// ------------------------------------------------------------
//
// Named headers `{ x, y -> ... }`, positional references `{ $0 + $1 }`, typed headers, anonymous
// functions, and trailing lambdas produce the same conceptual callable shape after source typing.
// Their syntax-specific labels and origins remain available for diagnostics and debug information.
//
// Every form must preserve:
//
// - parameter count, names where observable, passing modes, and source-order mapping;
// - result type, including nullable and bottom results;
// - checked-effect set and call compatibility;
// - capture set and whether each capture reads a copied immutable value or shared mutable cell;
// - local return label ownership and normal/abrupt completion behavior;
// - ref-like, borrowed and ref/out location lifetime obligations.
//
// A parser accepting a form does not prove its contextual body was checked or that it has a native
// closure representation.

// 10. Generic lambda parameters and polymorphic lambdas are different features
// ---------------------------------------------------------------------------
//
// A lambda can be contextually typed with a function type that mentions an outer generic parameter.
// That does not make the lambda independently generic. A polymorphic lambda would require its own
// type-parameter owner, inference scope, constraint list, instantiation rules and value ABI. This
// language slice does not establish such syntax or runtime semantics.

func <T> returnContextual(value: T): (T) -> T = { item -> item }

// expect: Source may contextually type this identity body as T -> T. FIR/native closure result:
// Rejected(SOL2400); no closure environment or generic function-value ABI exists.

// A future generic lambda proposal must also define whether type parameters are inferred each time
// the value is called, fixed when the lambda is created, or reified at runtime. These options have
// different specialization graphs and cannot be chosen by a backend optimization.

// 11. Ref and out callback parameters remain invariant storage contracts
// -----------------------------------------------------------------------
//
// A function type containing ref/out encodes exact writable storage behavior. A callback cannot
// widen, box, copy, capture or turn the location into a temporary. Parameter mode and type are both
// part of callable compatibility. `out` assignment is required on every normal return from the
// invoked callback; exceptional exits do not create a normal assignment proof.

val exactCallback: (ref int, out int) -> () = { ref source: int, out destination: int ->
    destination = source
}

func <T> passRefCallback(callback: (ref T, out T) -> (), ref input: T, out result: T): () {
    callback(ref input, out result)
}

// expect: Source analysis may retain exact ref/out modes; a generic indirect call has no current
// FIR form and cannot be emitted natively. A future instance must prove exact concrete storage and
// full assignment on normal returns.

// An escaping callback cannot capture the location passed by ref or out. A non-escaping callback
// can use such storage only if a lifetime/escape proof exists and the compiler keeps the pointer
// valid across every direct/indirect call path. Merely annotating a function type is not such proof.

// 12. Checked effects are part of callback compatibility
// ------------------------------------------------------
//
// A callback's declared checked effects are not erased by assigning it to a function type. Passing
// a callback into a generic function does not move its effect budget to the caller or make effects
// disappear. An invocation contributes the selected callback type's declared checked effects to
// the enclosing callable budget; unchecked RuntimeException subtypes retain their ordinary rules.
//
// When a generic type parameter appears in a callback effect, substitution must preserve the
// owner/ordinal identity and then validate the concrete effect against Throwable and the function's
// declared budget. Effect sets use set semantics and must not be positionally zipped.

func <E: Throwable> invokeCallback(callback: () -> () throws E): () throws E {
    callback()
}

// expect: Generic effect substitution is a source-analysis obligation. The current direct generic
// FIR verifier can compare selected direct-call effect metadata, but cannot lower the indirect call.

// A wrapper that invokes a callback must declare or handle the callback's checked effect. Returning
// the callback without invoking it has a different effect flow, even if both have the same value
// type. Closure creation must not be treated as body execution.

// 13. Closure capture ownership and lifetime are explicit
// --------------------------------------------------------
//
// Captured immutable `val` storage may be copied only when its source value representation and
// lifetime permit it. Captured mutable `var` storage uses a shared cell when reads/writes must
// observe the same variable. Separate lambdas capturing the same mutable variable share one cell;
// they must not receive independent snapshots.
//
// A captured receiver or local may keep an object reachable after the creating stack frame returns.
// The environment layout must identify every managed reference slot so Yellow Tea can scan it. A
// native function pointer alone does not keep those slots alive; a live closure value owns its
// environment for exactly the closure's lifetime.

func capturedPair(seed: String): (int) -> String {
    var current = seed
    val first: (int) -> String = { suffix -> current + suffix.toString() }
    val second: () -> String = { current = current + "!" }
    first
}

// expect: Source capture analysis may classify the shared mutable cell. Native FIR is rejected:
// closure conversion, environment layout, safepoint roots and GC scanning are not implemented here.

// Escaping environments must own or trace their cells. Non-escaping stack environments still need
// precise safepoint roots while live. A compiler cannot mark a captured reference as an untracked
// raw pointer because a lambda happens not to be called in a local example.

// 14. Ref-like and borrowed values cannot be smuggled into an escaping environment
// -------------------------------------------------------------------------------
//
// `allows ref struct` relaxes the generic argument gate only. It does not authorize heap allocation,
// closure capture, boxing, return, storage in an ordinary field, or escape through a function value.
// A borrowed span captured by a lambda remains illegal even if the generic function is otherwise
// constrained to a ref-like type.

func <T> forbiddenCapture(value: T): () -> T where T: allows ref struct {
    { value }
}

// expect: Source: Rejected when T can be ref-like and the closure escapes; no lowering may store the
// captured value in a GC-managed environment.

// Non-escaping does not mean unrestricted: proof must include all returns, stores, calls, callbacks,
// exception paths, and nested captures. An unknown function-valued call is conservatively escaping
// until its contract proves otherwise.

// 15. Indirect invocation has an explicit execution edge
// ------------------------------------------------------
//
// Calling a local function value evaluates the target expression once, evaluates arguments in
// source order, supplies defaults according to the selected contract, and then performs an indirect
// invocation. The operation contributes checked effects and ref/out writes to flow analysis.
//
// An indirect call must validate:
//
// - non-null callable target or a specified null-call diagnostic;
// - exact function type, arity, parameter order, and ref/out modes;
// - argument evaluation order and exactly-once semantics;
// - callback result type, including bottom and nullable results;
// - declared checked effects and exceptional flow snapshots;
// - closure environment lifetime, GC roots, and thread-transfer rules.
//
// Current verified FIR has no indirect-call node. A source `operation(value)` can be analyzed by
// body analysis in some cases, but generic native instance construction rejects this function-value
// use until an indirect-call and closure/runtime contract is implemented.

// 16. Default arguments and varargs are attached to declarations, not closure values
// ----------------------------------------------------------------------------------
//
// A direct call can map named/default/vararg arguments against a selected source declaration. A
// function value has a callable signature; it does not by itself carry an unambiguous originating
// declaration's default-expression scope or a generic overload set.
//
// A future design must decide whether a function reference preserves declaration defaults in a
// wrapper, requires callers to fill all parameters, or exposes a canonical function-type call rule.
// For varargs it must specify the logical element sequence, named-array form, expansion, and
// ownership. It must also retain evaluation order and not run defaults at value creation.
//
// Until that decision is made, source call argument mapping must not be reused as evidence that a
// function value can be invoked with omitted arguments or expanded varargs.

// 17. Function value identity is not generic instance cache identity
// ------------------------------------------------------------------
//
// The direct-call specialization key is `(source declaration identity, ordered closed type-argument
// identities)`. That key identifies one monomorphic function body. A function value additionally
// needs an environment identity when it captures state, and may need a signature adapter if source
// variance/effect conversion permits one. Those identities are not interchangeable.
//
// A value wrapper created for `identity<String>` and a direct `identity<String>(text)` call may share
// machine code after the ABI contract permits it. The wrapper/environment still has observable
// creation, lifetime, effect and capture semantics. Reusing a function body must not merge two
// different closure cells or make a closure allocation disappear when observable.

// 18. Function type compatibility is explicit, not guessed by the backend
// -------------------------------------------------------------------------
//
// Source compatibility must specify parameter contravariance, result covariance, nullability,
// ref/out invariance, and checked-effect subset rules independently. Generic substitution may
// change any nested type occurrence, including function-type parameters and effects.
//
// Until full variance rules are implemented, the compiler may accept only exact structural
// function-type identity. It cannot use an LLVM pointer cast, equal machine widths, or an erased
// code-pointer representation to claim source-level compatibility.
//
// Function types are not nominal objects solely because they have the same rendered signature.
// Ownership, nullability, generic parameter owners, passing modes, result type, and effects all
// participate in the resolved source identity used by analysis.

// 19. Debug and diagnostic origins survive value conversion
// ----------------------------------------------------------
//
// Diagnostics for a generic direct call identify the instantiation call and selected declaration.
// Diagnostics inside a specialized body identify the original lambda/function body span and attach
// the instance key as contextual information. Generated wrappers and closure prologues carry
// synthetic origins linked to the source reference or lambda expression that required them.
//
// A function-value conversion failure is reported at the value expression, with notes for the
// candidate declarations and expected function type. An indirect-call failure is reported at the
// invocation expression. A capture escape is reported at the capture site and at the escape sink
// when both origins are known. Machine code debug locations map back to original source positions;
// they must not expose build-machine-only private paths.

// 20. Native and artifact admission stays separate from source typing
// -------------------------------------------------------------------
//
// A source-valid function value may be rejected by native FIR because the value representation,
// closure environment, indirect call, callback effect, ref-like lifetime, or GC root contract is not
// implemented. This is not permission to rewrite the source program or select another overload.
//
// Native `bin`, `cdylib`, `staticlib`, `solib`, and ORC modes must all consume verified FIR. A
// function value cannot cross a C ABI or Sol container boundary unless the artifact contract names
// its representation, ownership, lifetime, exception behavior, and versioning rules. AOT and ORC
// use the same source-level semantics even if they lower code pointers differently.

// 21. Required future validation matrix
// -------------------------------------
//
// Before function values enter generic native FIR, qualification must exercise:
//
// - direct calls and generic function references under explicit and inferred concrete types;
// - overload groups with a unique winner, an ambiguity, a deferred candidate, and native rejection;
// - lambda header, positional, anonymous, trailing, nested, bottom, nullable and checked-effect forms;
// - immutable-copy and mutable-shared-cell captures, nested captures, receiver capture, and escape;
// - exact ref/out callback locations, out assignment on normal exits, and exceptions before assignment;
// - ref-like and borrowed values rejected from heap/escaping closure environments;
// - collection/safepoint liveness for captured object, string and array references;
// - AOT and ORC indirect invocation, target ownership, error paths and cancellation;
// - deterministic symbol/debug origin maps and no cross-instance environment aliasing;
// - artifact export/import rejection until a stable callable ABI is specified.
//
// Each case records source-analysis result, selected declaration/type arguments, FIR result,
// diagnostic owner, and runtime expectation separately. A fixture is not proof that the case has
// been run. Current status and validation evidence are maintained by implementation documentation.
