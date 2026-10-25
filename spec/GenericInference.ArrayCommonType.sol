// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative array common-type selection; complete flow and expression coverage is pending.
module source.spec.generic_inference.array_common_type

// Array literal common-type selection
// ===================================
//
// A non-empty array literal passed without an array target may need a standalone element type. The
// compiler considers the entire literal as one expression and selects one unique element type for
// all positions. This rule is separate from generic lower-bound aggregation but uses the same
// conversion and constructed-type identity model.

// Inputs to the search
// --------------------
//
// Obtain one independently classified type per element, preserving the original expression node.
// Exact literal text remains available after a candidate type is chosen. Nullable state, generic
// argument identity, ref-like status, and source flow type remain explicit. An unknown element is
// not assigned an error placeholder that later converts to everything.
//
// A candidate common type may come from the input identities, a constructed base/interface path, a
// declared type-parameter bound, or a narrowly specified intrinsic nullable/reference conversion.
// It does not come from arbitrary method names, runtime inspection, assembly metadata not loaded by
// the current analysis, or numeric storage width.
//
// Every candidate graph walk uses constructed identities. An `IReadable<String>` path differs from
// `IReadable<Object>`. Repeated paths to the same constructed candidate are collapsed while
// preserving provenance. Distinct candidates remain distinct for specificity/ambiguity.

// Candidate validity
// -------------------
//
// Candidate C is viable only if every element type converts to C using the source conversion
// classifier and every literal fits the target domain. Ref-like elements cannot enter a managed
// array. A nullable element can convert only where C admits its nullability. Invariant generic
// wrappers remain exact even if their inner values have a base relationship.
//
// Candidate C is dominated when another valid type converts to C while C does not convert back.
// The remaining minimal candidates are the most-specific set. One member selects C. More than one
// member is ambiguity. Zero members is conflict. No source, graph, hash, declaration, or interface
// ordering selects the result.
//
// `std.core.Object` is not an automatic common-type fallback for every source. It is a candidate
// only when the ordinary conversion rules permit each element and no ref-like or anti-constraint
// rule forbids it. If several interfaces are more specific than Object, Object being reachable does
// not make one interface win.

// Lower-bound versus common-type inference
// ----------------------------------------
//
// When a formal is `[]T`, every element contributes an input/lower bound to T. The generic solver
// combines the set after collection. When the formal is a free `T`, the literal first requires a
// standalone common element E and contributes the constructed type `[]E` to T.
//
// These relations are not interchangeable. A generic inference variable may accept an implicit
// common base for several ordinary input values; an invariant named array argument requires its
// element identity to match exactly after the literal's one common type is established.
//
// A direct typed array value already has one element identity. The compiler does not re-examine its
// runtime contents to discover a narrower type. Spread and named-array arguments therefore use the
// static array signature.

// Numeric cases
// -------------
//
// Two default integer literals contribute their default source type and retain exact text for
// target range validation. A typed byte variable and an integer literal do not automatically choose
// int if that would widen the byte variable; the common candidate must be supported by ordinary
// conversion rules.
//
// A mixture of already-typed byte and long values does not select long solely because the backend
// can widen byte. Sol does not infer numeric widening for generic variables or array common types.
// An explicit conversion at the source element creates a value of the converted type and can then
// participate normally.
//
// Unary minus is part of literal representability. The signed minimum can be accepted only when
// the exact signed target permits the magnitude after sign application. Parsing cannot overflow an
// intermediate int and then pretend the result fits a larger target.
//
// Floating literal defaulting uses double. A quad target requires exact source-decimal conversion
// under the language's binary128 rule. An unavailable exact-rounding implementation defers rather
// than silently converting through double.

// Reference and interface cases
// -----------------------------
//
// For `Cat` and `Dog` with one `Animal` base, Animal can be the common type when both convert to it.
// For two unrelated implementations of `IReadable`, that interface may be selected only if it is
// the unique most-specific common candidate. If both also implement independent interfaces
// `IResettable` and `IClosable`, the common set may be ambiguous.
//
// If Cat implements `IReadable<String>` and Dog implements `IReadable<String>`, the exact
// constructed interface can be shared. If one implements `IReadable<String>` and one
// `IReadable<Object>`, generic invariance keeps them distinct. Repeated route identity does not
// authorize changing the generic argument.
//
// A type parameter `T: Animal, IReadable<String>` contributes its declared bound graph only when
// the owner/ordinal identity is available and all bound substitutions are valid. A missing bound
// metadata record makes analysis incomplete; it is not evidence for Object.

// Nullable cases
// --------------
//
// A nullable value and a non-null value can select a nullable common type if the conversion rules
// admit one unique lifted candidate. The selected array element type then stores nullable metadata
// for every element. The array container itself remains non-null unless the entire array expression
// is explicitly nullable.
//
// A null-only element set has no non-null common seed. `[]`, `[null]`, and a non-empty set whose
// every expression is bottom/nullable-bottom do not infer an arbitrary T. A target `[]String?`
// makes each null element checkable; it does not turn a missing context into String.
//
// `[]String?`, `[](String?)`, `([]String)?`, and `([]String?)?` have separate container/element
// identities. A common type search cannot remove a nullable suffix by display-name normalization.

// Tuples and nested array ranks
// ----------------------------
//
// Tuple elements do not use the array common-type rule. `(A, B)` evidence preserves tuple arity and
// ordered positions. A tuple with one different member type is structurally incompatible unless its
// own function/type conversion contract says otherwise.
//
// Nested array common type runs recursively at each depth. Every row must have one common inner
// element type. `[[1], [2.0]]` does not flatten or widen numeric types. `[[1], []]` needs context to
// type the inner empty literal. Fixed-array length is checked at every nested fixed-array level.

// Search budgets and termination
// ------------------------------
//
// A candidate search has finite depth and node budgets. The visited identity includes constructed
// generic arguments and nullable wrappers. One repeated identity terminates a cycle; different
// identities reached through different paths remain candidates.
//
// If a budget is exceeded, the result is ResourceLimit with the source literal and explored
// candidate origin. A partial candidate set cannot select the first type encountered. Cancellation
// is checked between nodes and propagates without a source diagnostic.
//
// No candidate search enumerates every type in every loaded assembly. Only source declarations and
// explicitly available metadata participating in the current analysis can be considered.

// Diagnostic classification
// -------------------------
//
// | Condition | Candidate state | Primary source location |
// |-----------|-----------------|------------------------|
// | one common type | complete | array argument/call result |
// | no conversion candidate | rejected | incompatible element(s) |
// | two minimal common types | ambiguous | array argument |
// | element expression unknown | deferred | unknown element |
// | fixed length mismatch | rejected | fixed array literal |
// | ref-like array element | rejected | element and array target |
// | path/node budget | resource limit | array literal |
// | canceled query | canceled | no language error |
//
// Diagnostics preserve element index, constructed candidate identity, and origin. A concise CLI
// message may omit the full graph, but analysis inspection retains stable structured reasons.

// Required common-type matrix
// ---------------------------
//
// - one input, identical inputs, related class inputs, unrelated class inputs;
// - one interface, repeated interface path, two independent interfaces;
// - identical and conflicting constructed generic base paths;
// - one non-null and one nullable value, null plus concrete, null-only;
// - int literals, negative boundary, float/double, quad exactness, typed numeric variables;
// - ref-like type, ref-like behind a generic bound, Object as explicit and implicit target;
// - one nested rank, empty inner rank, fixed lengths, tuple element, unresolved nested call;
// - file-order permutation, path-order permutation, cycle, node limit, cancellation.

// Current implementation boundary
// -------------------------------
//
// Current call binding computes a bounded nominal/nullable common type for non-empty array literals
// where a standalone array type is needed. Generic input bounds are combined by the shared inference
// solver. Numeric widening, all user-defined conversions, full flow-sensitive type evidence, exact
// quad rounding, every nested callable form, and executable array layout remain outside this result.
