// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative path ownership and name-merge contract; complete interface-default validation is pending.
module source.spec.member_resolution.path_ownership

// Inheritance-path ownership in member lookup
// ============================================
//
// Member lookup walks constructed declarations, not a flattened set of source names. The route to
// a declaration can affect duplicate collapse, hiding, override recovery, ambiguity, and diagnostic
// explanation. A route is therefore first-class analysis evidence even when two routes end at the
// same constructed owner.

// Constructed identity
// --------------------
//
// A nominal node is the declaration identity plus the ordered closed/open type argument identities.
// A type-parameter node is the declaration owner and ordinal. Nullable, array, tuple, and function
// wrappers are not nominal member nodes unless a separate language member rule explicitly handles
// that category.
//
// `Node<T>` and `Node<String>` are distinct. Two separately interned display strings that resolve to
// the same declaration and argument identities are one node in the same TypeAnalysis workspace.
// Numeric type IDs from another workspace cannot be compared or serialized as global identities.
//
// A path is an ordered sequence from the original receiver through each constructed base or bound
// edge to a declaring owner. For `Child<int> -> Mid<[]int> -> Root<Box<[]int>>`, every node records
// its own substituted arguments. Substitution is applied once per edge; string replacement and
// repeated substitution-to-fixpoint are not used.

// Distinct paths and duplicate identities
// ----------------------------------------
//
// Reaching the same declaration on the same constructed owner through two valid diamond routes
// yields one member candidate with two retained paths. The candidate retains both paths so later
// analysis can distinguish a duplicate diamond from a conflicting constructed base.
//
// Reaching the same declaration symbol on different constructed owners yields distinct candidates.
// `Root<int>` and `Root<String>` cannot be collapsed by symbol ID. If the type declaration itself
// violates a single-base or interface identity rule, lookup still preserves both candidates for
// error recovery and reports the declaration error separately.
//
// Reaching two different declarations with identical names and signatures is not a repeated path.
// Separate interface defaults or unrelated bounds remain distinct until explicit conflict
// resolution proves a unique implementation.
//
// A complete path identity is the ordered sequence of constructed type identities and bound-edge
// kind. The path set is independent of source-file enumeration order. Duplicate paths are removed
// without dropping a candidate's unrelated remaining routes.

// Path-sensitive hiding
// ---------------------
//
// Hiding is evaluated along each path, not by comparing only global depth. An interface bound with
// a same-name declaration does not hide a declaration reached through an unrelated bound route.
//
// A candidate reachable through several paths remains visible when at least one valid path reaches
// it without a hiding ancestor. A nearer property on one interface route cannot erase the same
// inherited method reached independently through another interface route. If all paths to the
// candidate contain a declaration that owns the name, it is hidden at that use site.
//
// A nearer non-callable name can hide a farther member on its own path. A more-derived callable
// group can hide a base non-callable value on its own path. Same-effective-depth mixed declarations
// are an ambiguity. A lookup consumer cannot retry a farther path after the selected nearest
// declaration fails accessibility, receiver-kind, accessor, or conversion checks.
//
// Hiding removes visibility for the relevant source operation. It does not delete the declaration
// from the module index, public metadata, override validator, or unrelated lookup name.

// Override suppression
// --------------------
//
// A valid same-signature derived override replaces the base dispatch candidate on the path where
// the derived owner precedes the base owner. The comparison uses constructed owner substitution,
// alpha-equivalent method parameter owner/ordinal identities, ordered formal parameter types, and
// passing modes. It does not use source spelling of a generic parameter.
//
// Return type, parameter name, default value, checked effects, source file order, and declaration
// order are not overload identity. They are checked by their own override or call rules.
//
// A missing `override`, final base member, reduced accessibility, incompatible result, or widened
// checked effect is a declaration diagnostic. Recovery may still choose the most-derived body to
// prevent duplicate call-site ambiguity, but the candidate remains invalid/incomplete and cannot
// establish successful compilation.
//
// A different overload signature does not suppress a base method. A derived `convert(int)` can
// coexist with inherited `convert(String)` unless the language's name-hiding rule explicitly hides
// that group. The member merge and overload applicability stages are separate and both retain
// declaration origins.

// Properties, indexers, nested types, and callables
// ------------------------------------------------
//
// A lookup returns a name group with declaration category. A read requests a value/property;
// assignment requests a writable property/indexer; call syntax requests an overload group; type
// syntax requests a type. Lookup does not convert every declaration into a callable or value.
//
// An inaccessible nearest property remains the nearest name owner. The compiler reports access at
// the property/accessor instead of silently reading a public base property with the same spelling.
// If the nearest declaration is a function group but no overload applies, a base property does not
// become a fallback value. A property with no getter can still have a setter; read and write lookup
// ask different operation questions.
//
// An indexer uses its own parameter map and accessor metadata after the receiver graph has selected
// candidate declarations. Constructors do not participate in ordinary inherited member lookup.
// Companion and object receiver categories are retained; a type name does not fabricate an instance.

// Receiver access and path ownership
// ----------------------------------
//
// Visibility is checked after lookup identifies the declaration and the source operation supplies
// its actual static receiver type. The declaring type reached at the end of a path is not a
// substitute for the type of the expression before the call.
//
// For protected access in a derived type, the static receiver must satisfy the protected receiver
// restriction. `super.member` is a distinct allowed route. A value statically typed only as the
// base cannot be accessed merely because a runtime object might have the derived type.
//
// Getter and setter access are independently checked. Effects are added only for the accessor
// invoked by the operation. A property candidate does not become raw addressable storage through
// path inheritance. Ref/out requires an exact writable source location.
//
// Nullable receivers are narrowed before ordinary lookup. Safe access performs lookup on the
// non-null branch and preserves the conditional path in its call result/flow facts. It does not
// merge a null path into the constructed inheritance graph.

// Type-parameter bounds
// --------------------
//
// Every inline and `where` bound is a separate outgoing edge from the parameter node. The
// containing generic substitution is applied before each edge is enqueued. A bound path is marked
// as coming from a parameter constraint so diagnostics can explain the route.
//
// Two bounds reaching one declaration at the same constructed owner can collapse to one candidate
// while retaining all bound paths. Distinct declarations or constructed owners remain separate.
// A bound resolution failure or invalid bound is not an empty set and cannot produce a complete
// `Missing` result.

// Cycle handling
// --------------
//
// A route cannot revisit the same constructed type identity already present on that route. This
// terminates a cycle without erasing a different route that reaches the same identity through a
// different prefix. Recursive spelling alone is not a cycle test; constructed arguments participate
// in identity.
//
// If a graph produces an unbounded sequence of new constructed identities, the bounded search
// returns a resource-limit result. It does not stop at the first repeated declaration symbol or
// return the candidates collected before the limit as a complete set.

// Resource budget
// ---------------
//
// One lookup currently limits inheritance depth to 128, total distinct path states to 8,192, and
// retained paths per declaration/constructed-owner candidate to 128. A path state is the whole
// constructed route, not merely a globally unique node. This distinction preserves diamond
// provenance while preventing path explosion.
//
// If a pending queue would exceed the path-state budget, or one candidate receives more than 128
// distinct paths, return `LimitExceeded`. Callers emit SOL2290 at the lookup site and retain it as
// unverified. A partial candidate set is not eligible for overload selection.
//
// Cancellation is checked before path expansion and during merge/override comparisons. Cancellation
// propagates to the analysis owner; it is not SOL2215, SOL2290, or an empty successful lookup.

// Deterministic output
// --------------------
//
// For the same source set, lookup candidate identity/status/path provenance is invariant under file
// order, declaration enumeration, hash iteration, and interface list enumeration. Candidate
// presentation may sort by minimum inheritance depth, qualified symbol, signature, and local type
// identity. Presentation order never selects an overload.
//
// Serialized path records use source-file indexes and workspace-scoped type references. They do not
// expose private `.codex` paths, machine-local IDs, object addresses, or a guessed global identity
// based on `ToString()`.

// Required invariants
// -------------------
//
// - duplicating one same-identity diamond route preserves one candidate and increases path evidence;
// - adding an unrelated route preserves both candidates and all independently visible paths;
// - hiding on one route does not hide a candidate that has another unhidden route;
// - hiding on every route removes the candidate from that operation's effective name group;
// - a constructed argument change updates only dependent owner and signature identities;
// - an override suppresses only matching base signatures on reachable routes;
// - two same-spelling interface declarations stay distinct until a conflict rule resolves them;
// - invalid visibility does not reopen a base fallback;
// - hitting either path cap yields no complete candidate group;
// - cancellation never returns a partially merged group as complete;
// - lookup does not invoke getters, defaults, constructors, methods, or delegate operators;
// - lookup never changes the caller's flow state or publishes speculative lambda bindings.

// Current implementation boundary
// -------------------------------
//
// The development resolver retains up to 8,192 distinct path states and 128 paths per
// declaration/constructed owner. It performs path-sensitive hiding, groups inherited declarations,
// and suppresses same-signature override recovery candidates. Full interface-default conflict
// policy, access reporting for every operation, serialized stable path inspection, cross-binary
// metadata, and verified FIR remain incomplete.
