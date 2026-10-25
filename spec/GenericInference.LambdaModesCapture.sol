// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative lambda mode/capture contract; complete lifetime and escape proofs are pending.
module source.spec.generic_inference.lambda_modes_capture

// Lambda parameter modes, captures, and candidate inference
// ==========================================================
//
// Lambda generic inference cannot separate a type identity from the storage and lifetime rules
// governing that type. Parameter modes, captured locals, ref-like values, and checked exceptional
// exits remain part of one candidate's contextual function contract.

// Value parameter
// ---------------
//
// A value lambda parameter receives the contextual value type. A write to a parameter-local copy
// follows the declared lambda parameter mutability rule; it does not write the caller's value
// unless a separately declared ref/out mode exists.
//
// If the formal parameter type contains an open generic T, an untyped lambda parameter is typed as
// that symbolic T. The body can use members available on T's declared bounds, but that use does not
// infer T from the fact that one candidate exposed a particular member name.
//
// A typed value lambda parameter is compared exactly with the specialized formal value parameter.
// User conversions are not inserted at the lambda boundary unless a distinct explicit function
// conversion rule says they are permitted.

// Ref parameter
// -------------
//
// A `ref` callback parameter requires a `ref` formal mode and exact source storage type. The
// lambda's use observes the same location according to the function-value contract. Passing a
// value lambda parameter to a ref formal is a mode mismatch; boxing, copying, or wrapper capture
// cannot repair it implicitly.
//
// A ref parameter is not available as a heap capture. Returning a closure that retains its address,
// storing it in a field/array/object, or passing it to an escaping callback is rejected unless a
// separate lifetime proof establishes that the closure cannot outlive the call-scoped storage.
//
// Generic T inference from a ref parameter is exact. An `Animal` storage location cannot infer
// `T = Cat` because the runtime value may be a Cat. A value read after a prior flow narrowing does
// not change the location's invariant storage type.

// Out parameter
// -------------
//
// An `out` callback parameter requires an out formal and exact writable storage. Definite
// assignment is checked on every normal lambda exit. An exception/bottom exit does not count as a
// normal assignment proof.
//
// `out var` is created only after a unique outer call candidate is selected. Candidate lambda
// analysis cannot create several hypothetical out locals with different types. If candidates
// require different out types and cannot be ranked before that point, the outer call remains
// ambiguous or deferred.
//
// A candidate's speculative assignment to an out variable is rolled back before the next candidate.
// On selected normal return, the caller's storage becomes assigned and its nullable fact is
// invalidated according to ordinary call rules.

// Captured local classification
// -----------------------------
//
// A capture record identifies the declaration-owned local, whether it is mutable, whether the
// closure escapes, whether the closure may execute before the current operation continues, and the
// proven storage/lifetime representation. A local name or syntax offset alone is not capture
// identity.
//
// A stable immutable value capture can preserve its effective type if the flow contract proves the
// value cannot change. A mutable capture may invalidate smart-cast facts when the callback can
// execute or escape. Candidate analysis cannot execute the callback to decide which path occurred.
//
// Capturing an enclosing `ref`/`out` parameter is rejected when it would extend the storage
// lifetime. The compiler does not turn the location into a copied value without an explicit source
// conversion.
//
// Capturing a ref struct or interior borrow is prohibited from heap-escaping contexts. A `T:
// allows ref struct` bound does not override closure lifetime rules. The candidate can remain
// deferred when escape behavior is not proven.

// Flow-state isolation
// --------------------
//
// Lambda checking consumes an immutable incoming flow snapshot. It may create lambda-local
// assignments and narrowing, but those facts do not flow backward to the caller except through
// declared ref/out effects and the selected call contract.
//
// Each overload candidate begins with the same snapshot. A failed candidate cannot narrow a local,
// mark a local definitely assigned, invalidate a value, or add an effect for another candidate.
//
// Within one selected lambda, branch joins intersect definite-assignment facts and retain a
// narrowing only when every reachable incoming path agrees. Unreachable branches do not affect the
// normal join, but their syntax and name errors remain reportable under recovery rules.

// Nested callbacks
// ----------------
//
// A nested lambda receives its own contextual function type and transaction. An outer lambda's
// generic result may depend on the nested callback's selected result/effects. Evidence passes
// outward only through an explicit function signature position and a complete nested analysis.
//
// A nested closure cannot inherit an outer lambda's unresolved parameter as concrete evidence. A
// symbolic parameter may flow through several nested signatures, but the type solver must preserve
// declaration ownership and reject self-evidence cycles.
//
// A captured parameter's passing mode and lifetime remain visible in every nested closure. Wrapping
// the closure in another closure does not reset the lifetime.

// Callable conversion
// -------------------
//
// A lambda literal is analyzed directly against the candidate's function type. A typed function
// value instead supplies an existing parameter/result/mode/effect signature. Generic inference can
// inspect that signature without executing the function value.
//
// Function variance or effect subset conversion, if permitted, runs after generic inference using
// the language's declared conversion relation. It cannot alter `ref`/`out` mode, capture lifetime,
// return storage, or exception cleanup.
//
// A function value with an open method type parameter is not a first-class generic function unless
// a separate generic function-value feature is specified. No type arguments are guessed from a
// function variable's name.

// Ref/out and overload matrix
// ---------------------------
//
// | Formal | Lambda header | Result |
// |--------|---------------|--------|
// | `(T) -> R` | `x: T` | exact typed parameter, R from body if independent |
// | `(T) -> R` | untyped `x` | x receives contextual T; x alone is not T evidence |
// | `($0)` positional shape | `$0` | arity/type come from the candidate |
// | `(ref T) -> R` | `ref x: T` | mode exact; storage/lifetime checks apply |
// | `(ref T) -> R` | `x: T` | reject mode mismatch |
// | `(out T) -> R` | `out x: T` | check every normal exit assigns x |
// | `(out T) -> R` | `out var x` at call | create only after candidate selection |
// | `() -> R` | headerless body | zero parameters; body result is contextual |
// | `(T) -> R` | `{ it -> ... }` | invalid; no implicit `it` |
//
// This table intentionally does not promise that every mode/lifetime combination has a native
// closure representation. Verified FIR and ABI lowering have separate obligations.

// Required negative cases
// -----------------------
//
// - value/ref/out mode mismatch;
// - exact ref/out type mismatch after inference;
// - out parameter omitted on one reachable normal exit;
// - out storage marked assigned by a rejected candidate;
// - ref capture escapes through a returned closure;
// - ref-like capture enters a managed array or delegate heap;
// - mutable capture keeps a stale nullable smart cast;
// - lambda body assignment changes another candidate's incoming state;
// - nested closure erases the outer lifetime;
// - call mapping creates `out var` before overload selection;
// - source spelling of a typed parameter changes generic identity;
// - function conversion hides a checked effect or changes a passing mode.

// Implementation boundary
// ------------------------
//
// BodyAnalysis already detects some ref/out and ref-like captures and performs speculative state
// restoration for generic callback inference. Complete escape analysis, closure environment layout,
// mutable-cell lowering, all nested function conversions, and root/barrier planning remain future
// verified-FIR work. Source binding alone does not allocate a closure or prove its runtime lifetime.
