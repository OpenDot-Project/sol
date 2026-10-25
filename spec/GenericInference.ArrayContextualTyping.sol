// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative contextual array inference; full nested/rectangular typing remains pending.
module source.spec.generic_inference.array_contextual_typing

// Contextual typing of array literals during generic inference
// ============================================================
//
// An array literal is kept as a syntax tree until a candidate supplies a formal array shape. The
// candidate must infer one element type for each array rank, preserve fixed lengths, and validate
// every element against the same selected type. This contract describes ordinary and generic call
// contexts; it does not define array allocation layout or runtime bounds checks.

// Shape model
// -----------
//
// `[]T` is a managed array whose element type is T. `[N]T` is a fixed-length value array with N
// elements. `[][]T` is a jagged array whose outer elements are arrays of T. A rectangular-array
// feature is not implied by nested literal syntax.
//
// Shape matching is recursive. At every level, the literal count is checked before collecting leaf
// evidence. `[[a, b], [c]]` has two inner array nodes of distinct lengths; a rectangular target that
// requires equal rows cannot be manufactured from those nodes. A jagged target may accept them if
// each inner row independently satisfies its element formal.

// Candidate-local evidence flow
// ------------------------------
//
// 1. Preserve each element's source span, token spelling, nested node, and argument index.
// 2. Map the entire array expression to a formal before evaluating candidate-specific conversions.
// 3. If the formal is `[]T`, collect one input fact `T <- Ei` for every independently typed element.
// 4. If the formal is `[N]T`, reject a count different from N before collecting any Ei facts.
// 5. If the formal is an open generic parameter T, find one unique standalone element common type
//    E, then contribute the constructed `[]E` as the actual evidence for T.
// 6. If the formal is a non-generic array type, check all elements contextually against its fixed E.
// 7. Solve generic evidence without committing candidate-local expression or flow records.
// 8. Recheck the selected literal against the closed target and publish its ordinary binding once.
//
// Ordinary argument facts and array element facts share the candidate solver but retain distinct
// origins. A conflict from element 3 of argument 1 must identify that element, not just the call.

// Standalone common element type
// ------------------------------
//
// Given independently typed elements E1 through En, compute the bounded candidate closure from
// the declared nominal and generic-bound graphs allowed by the conversion model. A candidate C is
// viable only if every Ei converts to C, C is legal for the target storage kind, and all generic
// constraints can be satisfied. Select C only when one unique most-specific viable type remains.
//
// The closure does not include a numeric widening rule, an implicit user conversion, a guessed
// Object fallback, the first declaration in source order, or the first base returned by a hash set.
// If two unrelated interfaces are equally specific, the standalone common type is unresolved.
//
// Nullable element candidates are constructed as one nullable layer. If any element is nullable,
// every non-null candidate is considered with nullable lifting only where the intrinsic conversion
// rules permit it. Nested nullability is not flattened: `[]String?` and `([]String)?` remain
// different types. An array whose elements are nullable is not itself a nullable array.
//
// Ref-like element values cannot escape into an array representation that may not contain them.
// A candidate whose legal-storage constraint is indeterminate remains deferred. The solver cannot
// choose Object and rely on boxing to erase a ref-like restriction.

// Literal value and numeric context
// ---------------------------------
//
// An integer or floating literal keeps its original token value until a selected element type is
// known. A contextual literal can fit an element type when the exact value is representable in that
// type. No intermediate machine-width parse may overflow and then wrap before the fit check.
//
// An already typed variable does not acquire literal flexibility. In `[smallByte, 1]`, the integer
// token may be checked against the selected byte element type, but the variable keeps its declared
// byte type. In `[smallByte, largeInt]`, the compiler cannot widen the first variable merely to
// make a larger array element type appear applicable.
//
// Binary128 literals, suffixes, separators, signed-minimum edge cases, and exact decimal-to-binary
// rounding are governed by the literal contract. Array inference must call that one representability
// service; it cannot maintain a second numeric parser.

// Nested literal inference
// ------------------------
//
// A nested literal is first interpreted at its immediate formal rank. If the target is `[][]T`,
// each non-empty row contributes element evidence for T after preserving its row boundary. A nested
// empty row has no independent leaf evidence, but can be checked later when another row or argument
// closes T.
//
// If no non-empty row, expected result, explicit type argument, or independent argument supplies T,
// an empty inner row does not infer it. A nested empty literal is not a wildcard of every array
// type. Failure to obtain evidence remains a missing-evidence outcome.
//
// `[[1], [2]]` and `[[1, 2], []]` preserve two outer elements. No flattening step may convert the
// nested sequence into `[1, 2]`. A rectangular interpretation requires an explicit rectangular
// type contract, which is outside the current language surface.
//
// Recursive matching has a 128-level depth budget. A nested shape failure rejects the current
// candidate; it does not erase evidence from another overload's private solver state.

// Empty arrays
// ------------
//
// `[]` has no element evidence. It can be used when another independent source closes the formal
// element type, including an explicit type argument, expected result, or another call argument.
// Once the candidate is selected, recheck the empty literal against the completed target type.
//
// The empty literal does not infer `[]Object`, `[0]Bottom`, an untyped sentinel, a process-wide
// singleton array, or a zero-length fixed array when the syntax has no fixed length. Empty array
// behavior must not depend on the target's source declaration order.

// Varargs and spread
// ------------------
//
// A positional vararg element maps directly to the declared element type and contributes ordinary
// element evidence. A named array-valued vararg argument maps to the array parameter contract and
// requires the invariant element identity after substitution. A spread array likewise has a source
// array type; it is not expanded during type inference into speculative runtime element values.
//
// A spread argument that is not an array rejects the candidate. A literal spread retains its array
// syntax and follows the mapped array rule. Empty varargs contribute no evidence. Empty literal
// arrays used for named or spread arguments also contribute no element evidence.
//
// Named and spread mapping is established before inference. The same source literal cannot be
// treated as positional elements by one inference pass and as one array by another pass of the same
// candidate.

// Conversion and invariance matrix
// ---------------------------------
//
// | Source | Formal | Inference relation | Required check |
// |---|---|---|---|
// | typed array `[]E` | ordinary `[]T` | nested input/exact shape | One supported array conversion |
// | typed array `[]E` | spread `...T` | exact element evidence | Same invariant element type |
// | typed array `[]E` | named vararg | exact array contract | Same invariant element type |
// | non-empty literal `[e1, e2]` | `[]T` | one lower fact per element | Unique closed T after solving |
// | non-empty literal `[e1, e2]` | `[N]T` | one lower fact per element | Count N and unique closed T |
// | non-empty literal `[e1, e2]` | open T | actual `[]E` fact | Unique standalone E |
// | empty literal `[]` | `[]T` | no element fact | T must close independently |
// | empty literal `[]` | open T | no actual array fact | T must close independently |
// | typed numeric local | numeric array literal | its declared type | No literal-style widening |
// | integer token | selected numeric E | representability check | Exact token range |
// | `Box<Derived>` element | `Box<Base>` element | invariant nested mismatch | Reject unless exact shape |
// | nullable element | non-null element target | no nullable erasure | Reject |
// | ref-like element | heap array target | storage constraint | Reject or remain incomplete |

// Diagnostics and candidate recovery
// -----------------------------------
//
// A wrong fixed length, incompatible nested shape, missing expression type, failed conversion, or
// conflicting generic bound belongs to one candidate. Keep the source array span and the smallest
// failing element span. Do not emit one diagnostic per speculative candidate if the call-level
// overload resolver owns the final error.
//
// If a candidate has a known applicable conversion while another candidate remains incomplete and
// could rank better, the call remains deferred. If every candidate is rejected, ordinary no
// applicable-call recovery reports a primary diagnostic and may include bounded candidate reasons.
//
// A candidate graph or type-interning cap yields a resource-limited/incomplete outcome. It does not
// treat unvisited base types as absent and does not infer the first discovered parent type.

// Required invariants
// -------------------
//
// - Every array literal level has one source element type per array rank.
// - Fixed length is checked before element evidence is collected.
// - An element is typed once per selected source execution; candidate inspection is speculative.
// - Candidate order and file enumeration order do not affect common-type selection.
// - Literal range checking uses exact source token data.
// - Nullable element and nullable container shapes stay distinct.
// - Generic nominal arguments remain invariant.
// - Empty literals never fabricate type evidence.
// - Vararg elements, named arrays, and spread arrays retain separate mapping semantics.
// - Resource exhaustion never returns a partial common type as if it were unique.

// Current implementation boundary
// -------------------------------
//
// BodyAnalysis has bounded common-type closure and candidate-local evidence for the covered
// managed-array/fixed-array forms. Full multidimensional and jagged contextual typing, all nested
// empty-literal cases, rectangular arrays, user-defined conversions, and native layout/lowering
// remain unimplemented. Syntax acceptance alone does not prove these rules.
