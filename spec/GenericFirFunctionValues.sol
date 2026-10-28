// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative function-value boundary; source analysis closes local generic function values
// from expected types. Bounded direct generic FIR calls and exact-ABI noncapturing lambda and
// anonymous-function values are implemented.
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
// These objects are not interchangeable. A generic function value is closed at its source site by
// the expected function type and points to one monomorphic instance; it is not a universally
// quantified runtime value. A lambda whose contextual parameter/result types are generic evidence
// is still a lambda expression with its own flow, effect, capture, and lifetime obligations.
//
// Current implementation status:
//
// - Source analysis constructs function types with parameter modes and checked-effect sets.
// - Named/positional lambdas are analyzed against expected function types in supported body-analysis
//   paths. Candidate-local generic inference may inspect bounded lambda parameter/result/effect
//   evidence and then analyze the selected lambda normally.
// - Anonymous functions have distinct AST identities, grouped parameter/source origins, block and
//   expression bodies, bounded result inference, checked effects, and local/receiver/escape facts.
//   Noncapturing anonymous functions with an exact closed function-pointer signature can be
//   represented by a source-linked synthetic FIR function. Capture environments, nested closures,
//   declaration defaults/varargs, and ABI adapters remain outside this native slice.
// - Direct calls to closed module-level generic free functions can produce verified monomorphic FIR
//   instances for the admitted native type/body subset.
// - FIR has `FirFunctionReference` and `FirIndirectCall` nodes for eligible module-level functions,
//   stateless top-level object methods, and a bounded set of noncapturing lambdas whose native
//   function signature matches exactly. Lambda forms admitted by this slice are named headers
//   (including exact `ref`/`out` modes with a proven `out` assignment), positional `$n` references
//   with value parameters, and headerless zero-parameter bodies. Each is tied to its source lambda
//   and represented by a synthetic FIR function with the ordinary typed function-pointer ABI; it
//   has no closure environment. The LLVM C++ bridge lowers references as typed function pointers
//   and invocations as indirect calls. Stateless object methods have no instance storage,
//   constructor, base, initializer, virtual dispatch, `this`, or `super` use.
// - Capturing lambdas, receiver/member-bound access, generic enclosing function bodies, open or
//   otherwise unsupported generic targets from a lambda body, and any value needing a variance or
//   effect adapter remain outside native FIR. Exact-ABI nullable function pointers are admitted
//   through a zero null value and source-proven flow narrowing. Nullable managed references and a
//   source function type alone do not prove closure layout, ABI compatibility, escape safety, or GC
//   ownership.
// - A generic function value records the ordered type arguments selected by expected-type inference.
//   FIR verifies that the reference points to that exact closed instance and includes it in the
//   specialization reachability graph. Generic bodies are rebound under closed arguments before FIR.
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

func inferredText(value: String) = value

func <T, U> sameNameDifferentOwners(value: T, other: U): (T, U) = (value, other)

// At a direct call or generic function-value selection, each parameter keeps its declaration owner
// and ordinal. A local variable also named T or U cannot capture or replace a declaration-owned
// type parameter. Expected-type inference closes every selected target parameter before a function
// value is formed; it never infers identity from `identity`, `T`, or printed signatures.

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

// expect: Source typing determines a closed function signature. An indirect invocation from a
// generic instance is FIR-ready when the specialized parameter/result ABI and effects are supported.

// 4. Assigning a generic declaration to a function value closes it from context
// -----------------------------------------------------------------------------
//
// A generic function name in a value context needs an expected function type. Sol uses that type
// to infer the declaration-owned type arguments and then performs ordinary directional function
// conversion. Every generic parameter must have exactly one solution and satisfy its constraints.
// No reference suffix is introduced: explicit generic arguments remain part of call syntax.

func valueFromExpectedType(): () {
    val transform: (String) -> String = identity
}

// expect: Source: Bound; the expected signature closes the selected target as identity<String>.
// FIR: Ready when the closed body and exact native signature are supported; no wrapper is created.

func localGenericFunctionValue(): int {
    func <T> localIdentity(value: T): T = value
    val callback: (int) -> int = localIdentity
    callback(42)
}

// expect: Source body analysis uses the expected function type to infer T=int, checks the local
// declaration's constraints, and gives callback one fixed `(int) -> int` signature. Without an
// expected function type the generic local function value stays deferred; no universal callable is
// inferred. FIR: Rejected because local-function function references and closure eligibility have
// not been admitted to the verified FIR subset.

func inferredResultFunctionValue(): () {
    val transform: (String) -> String = inferredText
}

// expect: Source body analysis has inferred inferredText's result as String. Function-value
// conversion consumes that resolved signature; it does not execute the body to discover a type.

func <E: Throwable> readText(): String throws E = "text"

func valueFromExpectedEffects(): () {
    val read: () -> String throws IOException = readText
}

// expect: Source: Bound; expected effect budget contributes an upper bound and closes E to a
// compatible Throwable subtype. The value retains its declared effect set; creation does not throw.

func <T> choose(value: T): T = value
func choose(value: String): String = value

func chooseFunctionValueOverload(): () {
    val callback: (String) -> String = choose
}

// expect: Both candidates close to the same exact function type; the final nongeneric tie preference
// selects choose(String). If a generic candidate has a strictly better conversion, it still wins.

func <T> createWithoutSignature(): int = 1

func uninferredGenericFunctionValue(): () {
    val callback: () -> int = createWithoutSignature
}

// expect: Deferred; T occurs nowhere in the expected callable type, so the target cannot be closed.

func <T: Throwable> constrainedIdentity(value: T): T = value

func constraintRejectsGenericFunctionValue(): () {
    val callback: (String) -> String = constrainedIdentity
}

// expect: Rejected(SOL2206); String does not satisfy the closed Throwable constraint.

func equallyRanked(value: Object): Dog { for {} }
func equallyRanked(value: Animal): Animal { for {} }

func ambiguousOverload(): () {
    val callback: (Dog) -> Animal = equallyRanked
}

// expect: Both candidates convert by an intrinsic reference conversion. BodyAnalysis reports
// ambiguity and retains no selected function-value target.

// 4a. Contextual selection for a function overload group
// -----------------------------------------------------
//
// Candidate compatibility uses ordinary function conversion, including contravariant value
// parameters, covariant results, exact ref/out storage, and checked-effect coverage. Inference is
// transactional per candidate. A generic candidate with missing or indeterminate evidence keeps
// the group Deferred; a provably incompatible candidate is removed. Defaults and bodies provide no
// inference evidence. Declarations imported from different modules remain ambiguous.
//
// Conversion rank is Identity, intrinsic Reference, then Boxing. The nongeneric preference applies
// only after conversion ranks tie. Declaration order never breaks a tie. An overloaded value without
// an expected signature remains Deferred. The backend cannot select a different candidate.

func selectOverload(): () {
    val callback: (String) -> int = choose
}

// expect: Candidate resolution is based on the expected function type and all visible same-module
// declarations. If no candidate converts, report SOL2206; equal best candidates report SOL2212.

// 5. A generic function value is monomorphic, not a generic runtime closure
// -------------------------------------------------------------------------
//
// The selected declaration and ordered closed type arguments identify a function instance. The
// value carries its concrete function type and checked effects; it does not carry type descriptors,
// a dictionary, or open generic parameters. A backend must not synthesize a dummy call, a hidden
// closure, or an erased dispatch path.
//
// A generic reference can be returned by a generic factory. Rebinding the factory body under its
// concrete arguments must close the nested reference before FIR is built.

func <T> makeIdentity(): (T) -> T = identity

func useMonomorphicFactory(): (String) -> String = makeIdentity<String>()

// expect: makeIdentity<String> returns a function pointer to identity<String>; FIR contains both
// closed instances and proves the nested reference edge is reachable from the ordinary entry.

// Stateless top-level object methods have the same receiver-free ABI as module-level functions.
// The eligibility check applies to every partial declaration of the object, not only to the
// particular method chosen as a function value. No receiver, singleton address, or hidden closure
// environment is placed in the FIR value.
object IdentityOperations {
    func <T> identity(value: T): T = value
    func describe(value: String): String = value
}

func useStatelessObjectMethodValue(): (String) -> String = IdentityOperations.identity

// expect: The expected function type closes identity<String>. FIR records the selected method and
// its ordered type argument; direct and indirect calls use the same receiver-free function target.

func useStatelessObjectDirectCall(): String = IdentityOperations.describe("sol")

// expect: The selected object method is emitted without an implicit receiver only because its
// complete top-level object declaration is stateless and the method does not use this or super.

object StatefulOperations {
    val prefix = "sol"
    func <T> identity(value: T): T = value
}

func statefulObjectMethodValue(): () {
    val callback: (String) -> String = StatefulOperations.identity
}

// expect: Source selection and FIR reject this receiver-free representation. Object fields,
// constructors, bases, initialization, non-function members, virtual methods, this, and super are
// outside the receiver-free object method boundary, including members in other partial parts.
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

// expect: The first form has an actual argument and may infer T=int. The second form closes
// identity<T> as identity<int> from the expected function type; neither form fabricates a call.

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
// A single best candidate is selected only after its generic parameters can be completely closed.
// FIR then independently admits or rejects the selected body and representation. If no candidate
// applies, the value expression is rejected. If candidates tie, it is Ambiguous. If analysis is
// incomplete for a candidate, the result is Deferred instead of choosing a lower-ranked overload.
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
// it. The selected noncapturing lambda can use the existing function-pointer representation when
// its closed contextual signature is exact; the generic callee's indirect call must independently
// pass generic FIR verification.

func nestedLambdaScopes(): () {
    val nest: (int) -> (int) -> int = { outer -> { inner -> outer + inner } }
}

// Nested lambdas own separate parameter scopes. A nested `$0` starts from the inner lambda's
// positional namespace. A generic parameter from the enclosing function is not a lambda parameter.
//
// This nested value reads `outer`, so the inner lambda captures an outer parameter and remains
// FIR-rejected. A nested lambda that reads only its own parameters can be lifted independently when
// it meets the same exact-signature and noncapturing conditions.

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

// expect: Source may contextually type this identity body as T -> T. FIR rejects this occurrence
// because its enclosing function is generic; the bounded native lambda slice does not emit open
// generic-parent bodies or instantiate lambdas per specialization.

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
// LexicalAnalysis exposes the resolved local capture inventory per lambda, including nested-lambda
// environment propagation and read/write access. `LambdaEscapes` and `GetLambdaEscape(lambda)` add
// a separate source-use classification. Tracked lambda/anonymous-function values written to a
// local or copied through a local alias are followed by LocalSymbol identity. All simple writes,
// including branch alternatives, are unioned as may-aliases without selecting a path. Mutable
// locals can be ProvenNonEscaping if every joined use is local storage, aliasing, or invocation.
// Returning a value, passing it to a call, placing it in an aggregate/property, or storing through
// an external location is escape-capable. Null/untracked writes, aliases captured by another
// callable, ref/out publication, and unsupported consumers remain `Unknown` or `MayEscape`. No
// no-escape call contract is inferred.
// These facts do not prove borrow safety, select a native layout, or authorize code generation.
// `LambdaReceiverCaptures`/`GetLambdaReceiverCaptures(lambda)` separately identify explicit `this`
// and `super` uses, unqualified direct/inherited instance-member references, constructor properties,
// and accessor `field` references. Receiver captures propagate to each enclosing lambda environment.
// They are kept distinct from local symbols: a shadowing local remains local, and `other.member`
// does not capture the current receiver. Bounded inherited lookup reports `IsComplete = false` if its
// resource limit prevents a full result. Local-function forwarding and a bound-method ABI are not
// represented by this source inventory.
// `AstAnonymousFunction` preserves grouped parameters/origins, passing modes, optional result type,
// ordered checked effects, and body kind. BodyAnalysis resolves explicit/contextual parameter types,
// checks return paths and effects, and may infer a common result under bounded traversal. Missing
// bodies, function-value default/vararg mapping, or unresolved result types remain deferred. The
// analysis admits noncapturing, complete, closed, exact-ABI functions to the synthetic typed
// function-pointer FIR path used for noncapturing lambdas. Block and `= expression` bodies retain
// source identity and are independently verified. Captures, nested closures, generic enclosing
// functions, unsupported generic targets, defaults/varargs, and signatures requiring adapters
// remain rejected or deferred. This does not emit closure environments. Separate
// `AnonymousFunctionCaptures`/`GetAnonymousFunctionCaptures` and
// `AnonymousFunctionReceiverCaptures`/`GetAnonymousFunctionReceiverCaptures` inventories record
// local and enclosing-receiver requirements through nested anonymous-function scopes.
// `AnonymousFunctionEscapes`/`GetAnonymousFunctionEscape(function)` separately classify source uses:
// direct immediate invocation is `ProvenNonEscaping`; a return, call argument, aggregate element,
// or heap store is `MayEscape`; mutable or immutable local aliases remain `ProvenNonEscaping` when
// every modeled use stays local. Local invocation is a supported use, and an unused local value
// remains within its activation. Tracked local writes and branch alternatives are unioned; untracked writes,
// callable capture, ref/out publication, and unmodeled consumers remain `Unknown` or `MayEscape`.
// No call is assumed to have a no-escape contract.
// These classifications are analysis evidence only: they do not prove borrowed-value safety,
// closure lifetime, heap ownership, Yellow Tea rooting/tracing, environment layout, or ABI validity.
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
// exception paths, and nested captures. A call argument is conservatively escape-capable because no
// no-escape declaration contract is implemented. `Unknown` is not interchangeable with
// `ProvenNonEscaping`, including for a lambda stored in a local and invoked later.

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
// A closed generic instance may invoke a supported function-typed parameter through
// `FirIndirectCall`; each concrete specialization must pass signature and effect verification.
// A noncapturing lambda body may also call or reference a generic function when source binding has
// selected every closed type argument and the specialized target is in the verified native subset.
// Its FIR call/reference is a normal specialization-graph edge. An open or unsupported target is
// rejected; the lambda path does not invent an instantiation or a fallback overload.
// Lambda-created closure values, receiver-bound methods, and non-identity function adapters remain
// outside the native subset until their environment, ABI, and GC contracts exist. An array can hold
// a non-null function value when that value has the existing receiver-free code-pointer ABI. Its
// element storage is pointer-sized but is not a managed reference and does not add a GC root.

// 16. Default arguments and varargs are attached to declarations, not closure values
// ----------------------------------------------------------------------------------
//
// A function type carries no declaration default-expression metadata. An indirect call must supply
// every function-type parameter; it never evaluates a selected declaration's omitted defaults.
// A vararg source parameter becomes one array-valued parameter in the function type. An indirect
// call supplies that array as one argument and does not expand elements. Direct calls keep their
// ordinary named/default/vararg mapping and source-order evaluation rules.

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
// A function value is compatible with a target function type when arity and passing modes match,
// each target value parameter converts to the corresponding source parameter (contravariance), the
// source result converts to the target result (covariance), each ref/out parameter has identical
// storage type, and every source checked effect is covered by a target effect budget. A target may
// permit additional checked effects that the source never raises.
//
// These checks run after generic substitution and recurse through nested function types. Each value
// parameter reverses the conversion direction; each result preserves it. Nominal generic arguments
// remain invariant. Nullability, boxing, and ref/out storage continue to follow their ordinary
// source conversion rules. An LLVM pointer cast, equal machine widths, or erased code pointer cannot
// establish source compatibility.
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

// 20a. Arrays of receiver-free function values
// ---------------------------------------------
//
// A managed array may store non-null function values whose closed signature uses the existing raw
// code-pointer ABI. This includes direct module references, closed generic references, eligible
// stateless-object methods, and typed function-value parameters. The array type remains ordinary
// `[]F`; array indexing loads one pointer of the function signature, and an indirect invocation
// checks its exact parameter modes, result, and checked effects. Empty arrays require an explicit
// function element type. Iteration copies each code pointer into its value slot.
//
// The Yellow Tea element descriptor records pointer width and scalar layout with no managed-reference
// scan shape. The array itself remains a managed object and must be rooted while its elements are
// evaluated and initialized. Function code addresses are not object references; tracing them as
// `Header*` values would retain arbitrary objects or read invalid headers. This subsection describes
// non-null function elements; nullable exact-ABI elements are specified below and in
// NullableFunctionValues.sol. Neither array form permits closure environments, receiver-bound
// values, adapters, or borrowed code from an unloadable ORC unit. A value that requires such an
// environment remains FIR-rejected.

// 20b. Nullable function values and nullable array elements
// ---------------------------------------------------------
//
// A nullable exact-ABI function element uses the same pointer-width slot as a non-null function
// element. The null value is zero; no wrapper object, closure environment, or additional GC root is
// introduced. Null equality and inequality are direct pointer comparisons. A successful null check
// may refine only a stable local that identifies the tested pointer, and assignments/ref/out writes
// invalidate that fact. Nested arrays trace their ordinary array references and retain scalar
// function-pointer elements at the leaves. Nullable managed references and function values that
// require adapters are not admitted by this representation.
//
// This slice has verified-FIR tests for branch and short-circuit narrowing, ref/out invalidation,
// defaults/varargs, exact ABI rejection, and nested arrays. AOT and process-isolated ORC integration
// exercise null and non-null values in parameters, returns, locals, and arrays, including collection
// while a nullable callback array is live. `!!` remains source-valid but is rejected at native FIR
// admission until null-assertion exception lowering is implemented.

// 20c. Array conformance and runtime qualification
// ------------------------------------------------
//
// Qualification covers literal construction, empty typed arrays, generic closed targets, indexed
// invocation, function-array parameters, iteration, AOT and ORC execution, and scalar-pointer GC
// behavior. The runtime test deliberately stores the bit pattern of a managed object address in a
// callback slot, then collects: the object must be reclaimed while the array remains live. A separate
// callable round trip proves that the array preserves the exact function address and ABI. These
// runtime checks are distinct from source binding and independent FIR verification.

// 21. Required future validation matrix
// -------------------------------------
//
// Before broad function-value support is considered complete, qualification must exercise:
//
// - expected-type inference, generic function references, constraints, missing evidence, and specialization reachability;
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
