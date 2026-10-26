// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative constructed lookup-path contract; interface conflict resolution remains open.
module source.spec.member_resolution.paths

// Constructed lookup path contract
// ================================
//
// This file defines the observable path model used by receiver lookup. A path is compiler-owned
// evidence that connects one source receiver type to the type that declares a candidate. It is
// not a runtime object path, layout path, or ABI identity.

// Path nodes and edges
// --------------------
//
// A path node is a fully constructed ResolvedType identity in the current analysis workspace. A
// nominal declaration symbol without its arguments is not a valid node for a generic declaration.
// A type parameter node includes its owner and ordinal. A nullable node must be narrowed or handled
// with safe access before nominal member traversal begins.
//
// An edge is either:
//
// 1. a declared nominal base/interface, with the parent type substituted by the current node's
//    generic owner arguments; or
// 2. a nominal constraint attached to a type parameter, including every matching `where` entry.
//
// A missing or unresolved bound is not an edge to an empty type. It leaves an analysis obligation.
// An interface default does not create a second edge; it is a declaration reached through the
// interface's ordinary nominal identity.

// Composed substitutions
// ----------------------
//
// For `Leaf<A>: Mid<[]A>`, the `Leaf<int>` node produces `Mid<[]int>`. If `Mid<B>: Root<Box<B>>`,
// the next edge produces `Root<Box<[]int>>`. The compiler applies the first substitution to the
// written base once, then uses the resulting constructed parent for the next edge. It does not
// replace `[]int` again by matching the spelling `int` or re-run all substitutions to a fixpoint.
//
//     class Box<T>(val value: T)
//
//     interface Root<T> { func value(): T }
//
//     open class Mid<U>: Root<Box<[]U>> {
//         open override func value(): Box<[]U> = default<Box<[]U>>()
//     }
//
//     class Leaf<V>: Mid<V> { }
//
// A receiver `Leaf<int>` finds the `Root<Box<[]int>>` interface contract. A receiver `Leaf<T>` in
// an open generic body finds `Root<Box<[]T>>` and keeps T's owning declaration identity.

// Canonical visited identity
// --------------------------
//
// The visited key is the workspace-local constructed type identity. It includes the nominal
// declaration and ordered type arguments. It also includes parameter passing modes and effects for
// function types used as bound signatures. Equality of a display name is insufficient.
//
// Reaching the same identity through two paths is a repeated path. Keep one candidate and preserve
// deterministic path metadata for inspection. Reaching one declaration with two different
// constructed type arguments is not a repeated path. Both are needed to report a conflict.
//
// Example of a collapsible diamond:
//
//     interface Root<T> { func get(): T }
//     interface Left<T>: Root<T> { }
//     interface Right<T>: Root<T> { }
//     class Shared<T>: Left<T>, Right<T> { }
//
// For Shared<String>, both paths produce Root<String>. `Root.get` is one inherited declaration.
//
// Example of a conflicting constructed base:
//
//     interface IntRoot: Root<int> { }
//     interface TextRoot: Root<String> { }
//     class Invalid: IntRoot, TextRoot { }
//
// The two Root nodes differ. Declaration validation rejects the incompatible base contract. Any
// member/call analysis retains both paths for recovery and cannot pick one by interface order.

// Candidate collection
// --------------------
//
// Lookup enumerates all reachable nominal declaration members with the requested source name. A
// function candidate records:
//
// - exact DeclarationSymbol identity;
// - constructed declaring type;
// - inheritance depth;
// - constructed path sequence;
// - whether the path begins at a type parameter bound;
// - source owner file and declaration span through the symbol's parts.
//
// Candidate collection does not evaluate a property getter, invoke a method, execute a constructor,
// or compile an operator. It does not apply source conversions until a call/property operation
// requests an applicable target.

// Callable merge and override suppression
// ----------------------------------------
//
// A callable group includes different inherited signatures after owner substitution. A same
// signature candidate at a smaller inheritance depth suppresses the base recovery candidate when
// override validation says the most-derived declaration is the intended implementation. The
// declaration validator still reports a missing `override`, final target, reduced accessibility,
// incompatible result, or widened effect. Suppression prevents spurious call ambiguity during
// continued diagnostics; it is not a declaration-success rule.
//
// Method signature comparison substitutes each method's declaring type independently and alpha
// renames its method type parameter ordinal to the other method's parameter identity. It compares
// ordered parameter types and passing modes. It does not compare return types, parameter names,
// defaults, effects, or constraints as overload identity.
//
// Two candidates at equal depth are never suppressed solely because they compare equal. They may
// be distinct interface declarations or duplicate/invalid symbols. The declaration/overload
// validator reports the conflict and the call resolver retains ambiguity where applicable.

// Non-callable lookup
// -------------------
//
// Properties, nested types, enum variants, and indexers are not converted into functions for lookup.
// The nearest applicable non-callable declaration owns its name. A farther declaration cannot be
// used as a fallback when the nearest candidate is inaccessible, has the wrong receiver category,
// has an unavailable accessor, or is not callable.
//
// A more-derived callable group hides a base value with the same name. A same-depth mixture is
// ambiguous. A `MemberLookupResult` must classify the group before consumers decide whether the
// syntax requests a callable or value. This prevents a `value.member()` call from treating an
// arbitrary property as a method name, and prevents a value read from arbitrarily choosing one
// method overload.

// Type parameter bounds
// ---------------------
//
// A type parameter's inline constraint is visited first only for deterministic reporting; every
// matching where-clause bound still participates. This order cannot change the result. If a
// constraint references an enclosing type parameter, that identity remains the enclosing parameter
// in an open generic body. If a concrete enclosing owner is available, its constructed substitution
// is applied before traversing the bound.
//
// A method parameter may use the containing class's generic parameter in its bound. The owner map
// and method map remain disjoint. Type parameter spelling is never the map key.
//
// A bound can be a class or interface nominal. Invalid primitive, nullable, or unresolved bounds
// produce a constraint diagnostic. Multiple class-base bounds that violate the single-base rule
// are declaration errors before body member lookup. All interface bounds can contribute distinct
// overloads.

// Resource, cancellation and ordering
// ------------------------------------
//
// Each lookup uses a depth budget of 128 and an 8192 path-state visit budget. A declaration on a
// constructed owner retains at most 128 distinct paths. An exact path is visited once; the same
// constructed identity reached through a different path remains distinct until candidate merging.
// A cycle terminates when the current path repeats an identity; recursive type spelling does not
// recurse forever.
//
// On any path or per-candidate budget exhaustion, return a limit result with no candidate set claimed complete. Callers emit
// a source diagnostic and retain the lookup site as unverified. They cannot consume the partial
// subset collected before exhaustion as if it were all candidates.
//
// Cancellation is checked before dequeuing work, before expanding bounds, and while comparing
// candidates. Cancellation propagates. It does not emit SOL2215 or SOL2290 because those would
// falsely describe source code as invalid when the user canceled analysis.
//
// Deterministic order for serialized/debug output is inheritance depth, qualified declaration name,
// signature, and constructed type identity. That order is diagnostic presentation only; overload
// ranking must compare the complete candidate set.

// Required property-based invariants
// ----------------------------------
//
// For any finite acyclic base graph within budget:
//
// - adding a repeated path to the same constructed identity does not change the candidate set;
// - changing file enumeration order does not change candidate identities or status;
// - adding an unrelated member under another name does not change this lookup;
// - replacing a generic argument changes only signatures dependent on that parameter;
// - a base method with a distinct signature remains available beside a derived overload;
// - a same-signature valid override removes only its own base dispatch candidate;
// - inaccessible members never become addressable storage;
// - a limit/cancel operation never returns a complete partial result.
//
// Native execution, CLR reflection, source path conventions, and sorting by source line are not
// substitutes for these semantic invariants.
