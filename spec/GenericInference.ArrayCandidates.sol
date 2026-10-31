// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative array-literal call evidence; BodyAnalysis implements a bounded subset.
module source.spec.generic_inference.array_candidates

// Array literals as generic call evidence
// ========================================
//
// An array literal is a syntax form whose element expressions remain available until a call
// candidate is known. It is neither an untyped `Object` nor an array with a separately guessed type
// for each element. One array expression has one element type and one evaluation order.

// Evidence sources
// ----------------
//
// A generic call may obtain array-related evidence from four independent places:
//
// 1. An already-typed array value contributes its exact invariant element identity for spread or
//    named-vararg use, and an input relation when passed as an ordinary value.
// 2. A non-empty literal passed to an array-shaped formal contributes one input fact per element;
//    the generic solver must find one unique type satisfying all facts.
// 3. A non-empty literal passed to an unconstrained generic parameter may first obtain one unique
//    standalone element common type, then contribute the constructed array as the actual type.
// 4. An expected result or another argument may fix the element type; the literal is then checked
//    contextually against that one selected target.
//
// An empty literal contributes no element fact. It can be checked only after a target has been
// selected from another source, an expected result, or explicit generic arguments. There is no
// fallback to Object, bottom, zero-sized array, or a process-global empty array type.

// Formal-shape rules
// ------------------
//
// A formal `[]T` accepts a managed-array literal shape. Every element is matched against T using
// input evidence. The solver may choose one common nominal candidate when ordinary conversions
// permit it, but it does not invent numeric widening for typed locals.
//
// A formal `[N]T` also requires exactly N elements before inference. A different length rejects the
// candidate without collecting evidence from a prefix. The element type remains invariant after
// the fixed length is checked.
//
// A formal generic parameter `T` may infer an array value `[]E` from a non-empty literal only when
// E has one unique standalone common type. This is distinct from a formal `[]T`: the first infers
// the whole array as T, while the second infers the element parameter.
//
// A non-array formal cannot accept an array-shaped literal merely because its elements convert to
// that formal. The constructed array itself must be convertible to the target type after inference.
// No element-wise conversion can turn an array into a scalar.

// Independent element types and common type
// ------------------------------------------
//
// Each element is typed without executing it. A literal retains exact source text and sign context;
// a local retains its declared or flow-effective type; a nested call retains its selected result
// type only if that call is independently bound. An unresolved element keeps the enclosing array
// candidate incomplete.
//
// For independently typed element types E1..En, a standalone element type C is valid only when:
//
// - every Ei has a supported implicit conversion to C;
// - C is reachable through the candidate type/bound graph or another specified intrinsic rule;
// - C is most specific among valid candidates;
// - exactly one most-specific candidate remains;
// - no ref-like, nullable, or invariant-storage rule is violated.
//
// A candidate graph search has depth and node limits. Limit exhaustion does not return Object or
// the first discovered parent. The call becomes incomplete with a resource result at the source
// array span.
//
// If one element is a subtype and another is its base type, the base may be selected only when the
// conversion model proves it is the unique most-specific candidate. Two unrelated shared
// interfaces remain ambiguous unless the language's declared common-type rule selects one unique
// type. Declaration order and interface order never choose the result.
//
// Generic applications remain invariant. An element of `Box<Derived>` does not become
// `Box<Base>` by changing T. A constructed base projection can provide an actual `Base<Derived>`
// identity; the inner argument still matches exactly.
//
// An already-typed numeric variable is never reinterpreted because another literal in the same
// array fits a wider numeric type. An integer literal may use contextual representability under
// the selected element type, but the literal does not overflow an intermediate type and then widen.

// Nested arrays
// -------------
//
// Nested literals preserve rank and each shape boundary. `[[1], [2]]` is a two-dimensional array
// candidate; it cannot flatten to `[]int`. Every nested literal gets its own common-type/evidence
// decision using the corresponding nested formal element shape.
//
// `[][]T` matched against `[["a"], ["b"]]` collects String evidence at the leaf T. A malformed
// nested element rejects only the candidate whose formal requires that nested shape. A nested
// empty literal has no leaf evidence and needs its own expected inner element type.
//
// `[[1], []]` can use the first row to infer the element type of the second row only after the
// outer candidate context fixes the row type. It must not infer the second row from a guessed
// Object. The empty inner literal is rechecked under the closed row type.
//
// Jagged arrays are allowed when the formal type is jagged. Rectangular shape and fixed-array
// nesting use each fixed length independently. No general rectangular-array feature is implied.

// Nullable elements
// -----------------
//
// `[]String?` contains nullable elements; `([]String)?` is a nullable array container. The syntax
// distinction remains visible during inference. Null may be checked against a selected nullable
// element type but does not choose a non-null T by itself.
//
//     func <T> values(items: []T?): []T? = items
//
//     val names = values(["one", null])
//
// The non-null String element supplies T. The null element is validated against T?. It does not
// widen T to Object? or change the array's non-null container state.
//
//     func <T> one(item: T): T = item
//
//     val rows = one([null])
//
// This call lacks a non-null element bound. It is not allowed to settle T as Object merely because
// nullable bottom converts to many nullable types.

// Arrays and varargs
// ------------------
//
// A spread argument `*array` requires an actual managed array. A non-literal source array
// contributes its existing element identity; generic type inference does not inspect runtime
// elements. A named vararg argument is one array value and requires invariant element identity.
//
// A literal used as a named vararg array must have a unique standalone common element type. That
// chosen array type then matches the vararg element parameter exactly. It is not equivalent to
// several independent vararg arguments with lower-bound joining.
//
// An ordinary positional vararg element is checked as one element. If its type is itself an array,
// the generic element parameter may infer that array type. Source syntax must distinguish this from
// a spread with `*`.
//
// A zero-element vararg call contributes no T evidence. A named empty array literal has the same
// missing-evidence rule unless another argument or expected context fixes T. The mapper records
// `empty vararg` and `empty array` as different source forms even when neither contributes a bound.

// Candidate isolation and evaluation
// ----------------------------------
//
// Each overload candidate receives an independent array-shape check, element type query, common
// type search, evidence list, nested length validation, conversion ranking, and diagnostic origin.
// A rejected candidate cannot leave an inferred element type or diagnostic in the shared AST
// expression map.
//
// Candidate analysis may inspect a nested expression's type. It must not execute its side effects,
// invoke a getter, call a default argument, allocate a runtime array, or evaluate an element a
// second time during ranking. After selection, runtime code evaluates the receiver first and array
// elements once each in source order.
//
// If a nested call needs its own unresolved lambda or expected-result context, the enclosing array
// candidate remains deferred until that local call can be analyzed. It cannot substitute a
// placeholder error type and then use that placeholder as evidence.
//
// Source diagnostics point to the smallest useful origin: array bracket for shape/length, element
// expression for conversion or missing type, and the selected call for a conflict between otherwise
// valid element bounds. Candidate inspection preserves both the element index and source span.

// Ref-like and storage rules
// --------------------------
//
// A managed `[]T` cannot contain a ref struct. Inference does not bypass this rule through an
// unconstrained type parameter or a conversion to Object. A fixed array and tuple follow their own
// byref-like storage rules and cannot be treated as managed arrays by inference.
//
// `Span<T>` and `ReadOnlySpan<T>` are ordinary library nominal types with their own lifetime and
// construction contracts. An array literal does not implicitly become a span unless a separate
// explicit language conversion is specified and proven. Generic array inference does not grant
// borrow scope, pinning, or escape permission.
//
// An array literal is never writable storage for a ref/out formal. A named local whose value is an
// array may be writable as a variable, but ref/out exactness applies to that local's array type;
// the compiler does not take an element or array temporary address to satisfy the call.

// Evidence table
// --------------
//
// | Source form | Formal shape | Evidence relation | Empty behavior |
// |-------------|--------------|-------------------|----------------|
// | typed []E value | []T | input/element bound | not applicable |
// | typed []E value | named vararg T | exact E | not applicable |
// | *typed []E | vararg T | exact E | not applicable |
// | [e1, e2] | []T | one input fact per element | no evidence |
// | [e1, e2] | T | input `[]Common(E)` | no evidence |
// | [e1, e2] | [N]T | length then per-element input | length may be zero |
// | [e1, e2] | named vararg T | exact `Common(E)` | no evidence |
// | nested literals | nested array type | recursive at each array level | no inner evidence |
// | null-only literal | nullable element | null validation only | no non-null T |
// | literal with unresolved expression | any | deferred unless other facts close it | unresolved |
//
// `Common(E)` above exists only for a non-empty sequence with exactly one valid most-specific
// candidate. It is not a library API and is not serialized as a guessed display string.

// Rejection and deferred distinctions
// ------------------------------------
//
// Reject a candidate when its formal is not array-shaped where array conversion is required, a
// fixed length differs, an element cannot convert to the selected type, a named/spread array's
// element identity is not exact, or a managed array would contain ref-like storage.
//
// Defer when an element's type is unavailable, common-type traversal exceeds its budget, an
// expected target is needed but not yet available, or a nested candidate could change overload
// ranking. A conflict between fully known incompatible element bounds is rejection, not deferral.
// Cancellation propagates independently of both outcomes.

// Required regression matrix
// --------------------------
//
// - one element, repeated same type, derived/base pair, and unique generic base projection;
// - unrelated bases, two shared interfaces, repeated constructed path, and conflicting paths;
// - int literals, double literals, mixed literal forms, and typed numeric locals;
// - null alone, nullable plus non-null, nullable container, nullable element, nested nullable type;
// - empty outer array, empty inner array, and expected-result-only inference;
// - managed array, each fixed length, zero fixed length, wrong length, and nested fixed arrays;
// - direct array formal, unconstrained T formal, named vararg array, spread, ordinary array element;
// - named argument reorder, middle vararg, one trailing lambda adjacent to array evidence;
// - nested function result, nested generic call, unavailable member, user conversion, and cancel;
// - ref-like element, allowed generic parameter, Span target, and ref/out array variable;
// - overload with one array candidate, two ranked candidates, an ambiguous pair, and a deferred pair.
//
// The matrix is a language and analysis contract. A parse-only fixture does not prove common-type
// correctness, candidate isolation, array allocation, or runtime bounds behavior.

// Current implementation boundary
// -------------------------------
//
// BodyAnalysis now gathers bounded evidence for non-empty generic call array literals and checks
// selected array elements under the closed target. It uses one standalone common type for a whole
// literal passed as an unconstrained generic value or named/spread vararg array. Direct CallAnalysis
// has a separate simpler expression-value path. Full expression common-type inference, every
// nested generic/flow form, warning-quality diagnostics, fixed-array lowering, ref-like safety proof,
// FIR creation, and executable array allocation remain unverified.
