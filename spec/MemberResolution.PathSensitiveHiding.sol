// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative path-sensitive name ownership; complete interface conflict resolution is pending.
module source.spec.member_resolution.path_sensitive_hiding

// Path-sensitive name ownership
// =============================
//
// A member's visibility within an inherited name group depends on the path by which its declaring
// owner is reached. Global depth alone cannot distinguish a hiding declaration on one interface
// branch from an independent branch that still exposes the same base declaration.

// Group construction
// ------------------
//
// First enumerate every valid constructed path within the lookup budget. Retain declarations by
// `(symbol identity, constructed owner identity)` and attach all distinct paths to that candidate.
// Only after enumeration is complete does lookup apply name hiding, override recovery, and
// operation-specific accessibility.
//
// Path enumeration does not read a property, invoke an accessor, inspect a runtime object, or
// execute a method. It records source declarations and constructed type substitutions.
//
// A failed path expansion, invalid bound, cancellation, or resource limit prevents the result from
// being classified as complete Missing/Single/OverloadSet. Partial groups are not selected.

// Hiding predicate
// ----------------
//
// For one candidate, inspect each retained route from receiver to its declaring owner. A route is
// hidden when a nearer owner on that same route declares a name that owns the requested operation
// category. The candidate is removed only when every valid route to it is hidden.
//
// One route with no hiding ancestor is enough to keep the declaration reachable. This rule matters
// when an interface has a property with the requested name but a sibling interface independently
// exposes an inherited method under the same name.
//
// A hidden candidate remains in declaration metadata and may still be relevant to override
// validation or an explicit conflict diagnostic. Hiding changes the effective member group for a
// source operation; it does not delete the declaration globally.

// Callable/value categories
// -------------------------
//
// A function group and a property/type group are distinct categories. A nearer non-callable owner
// can hide a farther callable member on a route. A nearer callable owner can hide a farther value
// on that route. Incomparable routes are kept independently until the effective group is
// classified.
//
// At the same effective depth, mixed callable and non-callable declarations with one spelling are
// ambiguous unless an explicit declaration rule resolves the conflict. Source order cannot choose
// one. A call against a function group that has no applicable overload does not fall back to a
// property. A property write with no setter does not fall back to a base field.
//
// An inaccessible nearest declaration remains the nearest name owner. The compiler reports an
// access error and does not search a public ancestor with the same spelling as a recovery shortcut.

// Path examples
// -------------
//
//     interface ValueRoot { func value(): String }
//     interface PropertyBranch { val value: String }
//     interface CallBranch: ValueRoot { }
//     class Split: PropertyBranch, CallBranch { }
//
// A lookup for `value` on Split retains both route categories. It must apply the language's
// same-depth conflict rule; it cannot globally say “property is closer” because the two branches
// are separate and equal-depth.
//
//     interface SharedRoot { func reset(): () }
//     interface LeftBranch: SharedRoot { }
//     interface RightBranch: SharedRoot { }
//     class Diamond: LeftBranch, RightBranch { }
//
// SharedRoot.reset is one declaration/constructed-owner candidate with two paths. The duplicate
// route must not create an overload ambiguity. Both paths remain available for explain output.
//
//     interface GenericRoot<T> { func read(): T }
//     interface IntPath: GenericRoot<int> { }
//     interface TextPath: GenericRoot<String> { }
//     class InvalidDiamond: IntPath, TextPath { }
//
// The same root symbol at two constructed owners produces two candidates. Declaration validation
// reports the conflicting base contract; call analysis cannot erase either type argument.

// Override relation
// -----------------
//
// A derived declaration suppresses a base recovery candidate only if the derived owner occurs
// before that base owner on a retained path and their specialized signatures match under method
// parameter alpha-equivalence. A declaration in a sibling branch is not a derived declaration on
// the other branch merely because it has a smaller global depth number.
//
// Suppression does not erase the base route from the explanation for the derived candidate. It
// keeps one dispatch declaration while retaining path evidence. An invalid override can still be
// used to reduce cascaded errors, but the declaration remains Invalid.
//
// A function with a different parameter signature does not suppress the base overload. Default
// parameter values, effects, and parameter names do not create a distinct overload identity.

// Candidate path budgets
// ----------------------
//
// The current resolver allows at most 128 inheritance levels, 8,192 distinct full path states per
// lookup, and 128 retained paths for one declaration/constructed owner. The full-path budget is
// not a global unique-node count; a shared node reached by distinct prefixes contributes distinct
// path states.
//
// Before a new state is enqueued, check the remaining path budget. Before appending a candidate
// route, check the per-candidate route cap. Exceeded limits return an incomplete result with a
// reason. The caller cannot use the candidates accumulated before the limit to decide a winner.
//
// The path key is the ordered sequence of workspace-owned constructed identities. A cycle check
// prevents one route from revisiting an identity already on that route. A new constructed identity
// reached along another route is still considered.

// Stability requirements
// ----------------------
//
// - reorder base declarations without changing the effective candidate set;
// - reorder partial source files without changing the path collection;
// - add a repeated diamond edge and preserve one declaration candidate;
// - add an unrelated branch and preserve the old branch's visible candidate;
// - add a hiding declaration to only one branch and preserve an alternate visible path;
// - add the same hiding declaration to every path and remove the candidate from the operation set;
// - change generic arguments on one route and preserve the other route's identities;
// - hit path cap and return no complete lookup group;
// - cancel while expanding the second path and publish no first-path success;
// - serialize deterministically without machine-local file paths or global type IDs.

// Current implementation boundary
// -------------------------------
//
// The development resolver keeps distinct constructed path states and candidate path arrays. Name
// hiding evaluates all retained paths before removing a candidate, and method suppression uses
// constructed path relationships. Full declaration-level interface conflict rules, explicit
// conflict resolution syntax, stable external path serialization, and verified dispatch lowering
// remain open.
