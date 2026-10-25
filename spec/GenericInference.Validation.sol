// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative inference validation protocol; generic body specialization remains future work.
module source.spec.generic_inference.validation

// Validation after solving
// ========================
//
// A solver result is an intermediate proof inside candidate analysis. It is not a selected call.
// The candidate must validate constraints, substitutions, complete signatures, conversions, modes,
// effects, and flow obligations before it is marked applicable.

// Owner and method constraints
// ----------------------------
//
// For a member call, validate the containing constructed type's generic constraints and the
// selected method's generic constraints independently. A closed receiver does not erase the
// method's bounds. A method inference result does not alter the receiver's owner arguments.
//
//     class Store<T: Identified>(val value: T) {
//         func <R: Comparable<R>> compareWith(other: R): int
//     }
//
// A call validates T when the Store type is constructed and validates R for each compareWith
// candidate. A failure reports the parameter owner and source argument/bound that failed. The
// compiler must not catch a failed constraint and continue with an unconstrained type.
//
// `allows ref struct` is an anti-constraint rather than evidence. It permits an otherwise valid
// ref-like argument but does not imply that T can be boxed, placed in a managed array, captured,
// stored in an ordinary class field, returned across an unsafe lifetime, or passed to an API that
// requires Object. Body/escape checks remain responsible for those uses.

// Nested nominal constraints
// --------------------------
//
// After substitution, check every nominal application nested in parameter, result, function, and
// effect types. It is insufficient to validate only the outermost return. A candidate such as
// `Map<String, InvalidKey>` is rejected if the declared Map key constraint is not satisfied.
//
// Constraint status is three-valued:
//
// - Satisfied: all bounds and nested applications were proven.
// - Unsatisfied: at least one resolved bound is false for this type argument.
// - Indeterminate: a source type/bound is invalid, unavailable, or beyond a supported analysis.
//
// Indeterminate is not Satisfied. If it can affect overload selection, the final call stays
// deferred. A source error can be reported while independent candidates and declarations continue
// to be analyzed.

// Type-argument invariants
// ------------------------
//
// Every inferred type belongs to the current TypeSpecialization workspace. Owner+ordinal identities
// must come from the current TypeAnalysis. Type IDs are workspace-local; they must not be compared
// across sessions or emitted as stable cache identifiers.
//
// Substitution is simultaneous. Given T -> U and U -> int, an original T becomes U; an original U
// becomes int. The replacement for T is not recursively rewritten to int. This distinction avoids
// parameter capture when owner and method maps are composed.
//
// Substitution preserves function parameter modes and effect sets, nested nullable normalization,
// fixed-array length, tuple order, and constructed base arguments. It does not allocate a managed
// array containing ref-like elements or a nominal type that violates `allows ref struct`.

// Diagnostic ownership
// --------------------
//
// Every inference outcome retains its candidate and evidence origins. Diagnostics should identify:
//
// - unresolved evidence at the call and missing parameter name/ordinal;
// - conflict at the smallest incompatible source arguments or expected result;
// - invariant shape mismatch at the argument whose constructed type differs;
// - constraint failure at the candidate call and the bound declaration;
// - resource exhaustion at the operation that exceeded its limit;
// - cancellation only as an operation cancellation, never as source invalidity.
//
// The diagnostic cap limits visible messages, not candidate computation semantics. Once the cap is
// reached, analysis may suppress additional messages but must not mark an incomplete candidate
// complete or let a partial candidate set choose a winner.

// Bounds and resource limits
// --------------------------
//
// Structural matching depth is bounded at 128. One candidate projection/common-type traversal
// visits at most 4096 constructed identities. One generic parameter accepts at most 256 distinct
// bound identities. The TypeSpecialization workspace independently caps new interned types at
// 100000. Each limit reports a resource outcome and leaves no successful partial substitution.
//
// The compiler checks cancellation while visiting nested types, base edges, parameter bounds,
// overload candidates, and constraints. It must not swallow cancellation inside a catch-all source
// diagnostic handler. A canceled analysis object is either not returned or clearly marked partial;
// it cannot claim `IsComplete`.

// Deterministic result requirements
// ---------------------------------
//
// Equivalent source sets in different file enumeration orders produce the same:
//
// - inferred type identities, modulo workspace-local IDs;
// - candidate status and rejection/defer reasons;
// - chosen overload or ambiguity result;
// - argument-to-parameter mapping;
// - default origin and checked effect set;
// - source diagnostic path, span, and code order.
//
// Sorting by symbol name is allowed for stable presentation only. It cannot be a hidden solution to
// a tie. Cache keys include normalized source/module identities and every constraint source; they
// do not use display strings as type identity.

// Generic body checking boundary
// ------------------------------
//
// Successful call inference specializes the callable signature. It does not automatically analyze
// the generic body under every possible substitution. Full monomorphization must validate definite
// assignment, checked effects, null flow, ref/out, ref-like capture/escape, recursion limits, and
// generated layouts for the concrete instance. Until that body proof exists, call binding is not
// FIR verification and cannot authorize LLVM lowering.
//
// An expression-body function may infer its own return from its body when that local analysis is
// acyclic and complete. This does not authorize reverse inference across arbitrary callers. A
// recursive strongly connected component with no written result seed requires an annotation.

// Focused validation matrix
// -------------------------
//
// A complete implementation should independently exercise:
//
// 1. one lower bound, equal lower bounds, related lower bounds, unrelated lower bounds;
// 2. one upper bound, related upper bounds, unrelated upper bounds;
// 3. exact plus lower, exact plus upper, contradictory exact identities;
// 4. invariant Box<T>, nested nullable, tuple, fixed array, function modes, and effect-only types;
// 5. one base projection, repeated same constructed path, distinct constructed base paths;
// 6. type parameter bound projection and substituted enclosing owner parameter;
// 7. generic method on generic receiver with identical T spelling and different owner identities;
// 8. constraint satisfied, unsatisfied, and indeterminate at owner, method, and nested signature;
// 9. ref-like allowed/disallowed argument, managed-array rejection, and Object boxing rejection;
// 10. cancellation at each traversal stage and each independent resource limit;
// 11. input file order permutation and diagnostic-cap stability;
// 12. overload selection with conversion vectors, defaults, varargs, and one deferred candidate.
//
// Each matrix row needs a semantic oracle that checks candidate records and source spans. CST parse
// success alone is not evidence that generic inference or a receiver lookup is correct.
