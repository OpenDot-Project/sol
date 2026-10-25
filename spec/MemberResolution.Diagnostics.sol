// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative diagnostics and recovery; exact user-facing wording can evolve without changing codes.
module source.spec.member_resolution.diagnostics

// Member resolution diagnostics and recovery
// ==========================================
//
// Diagnostics describe the phase that failed. A compiler should avoid replacing every receiver
// problem with “no such member,” because a declaration may exist but be ambiguous, inaccessible,
// nullable, incompatible, or beyond the supported analysis limit.

// Missing declaration
// -------------------
//
// A missing-member result means the receiver category and constructed type were resolved, all
// required bases/bounds were traversed within budget, and no visible name declaration exists.
// Report the member token. Do not report missing if a lookup path is deferred or incomplete.
//
//     class Empty { }
//     func missing(value: Empty): () {
//         value.refresh()
//     }
//
// The error belongs at `refresh`, not at Empty's declaration or the entire call expression.

// Ambiguous value/member name
// ---------------------------
//
// A value ambiguity exists when multiple non-callable declarations or a mixed method/value group
// survive the name merge. A callable overload set is not ambiguous until argument mapping,
// inference, constraints, and conversion ranking have run.
//
//     interface First { val id: int }
//     interface Second { val id: int }
//     class Combined: First, Second { }
//
// If those properties resolve to distinct declaration identities, reading Combined.id is
// ambiguous unless a valid explicit override/resolution declaration is present. Two paths to the
// same constructed property identity are deduplicated.

// Inaccessible declaration
// ------------------------
//
// Accessibility failure identifies the declaration or accessor selected by lookup. It does not
// claim that no declaration exists. It also does not retry an ancestor with the same spelling to
// bypass a private/protected nearest declaration.
//
// A read may select a public property whose getter is private; the diagnostic belongs to the get
// operation. A write may select a public property with private setter; the diagnostic belongs to
// the set operation. Compound update can produce one primary access diagnostic while retaining
// other independent operator/type errors under the normal diagnostic cap.

// Nullable receiver
// -----------------
//
// A nullable receiver diagnostic means a member exists on the non-null payload but no flow proof,
// safe operation, or explicit assertion is available. It does not become missing-member. Safe
// access changes the control-flow and result type; it is not merely a diagnostic suppression flag.

// Ambiguous inherited contract
// -----------------------------
//
// Declaration validation reports incompatible base applications or unresolved same-signature
// inherited defaults at the derived declaration. A call-site overload ambiguity is separate and
// points at the call whose argument types leave multiple candidates applicable.
//
// Recovery may continue with a candidate after a declaration error to find additional independent
// diagnostics. Such a candidate must carry Invalid/Deferred status; it cannot make `IsComplete`
// true. In particular, the most-derived same-signature method can be used to avoid repeated member
// errors only while preserving the missing/invalid override diagnostic.

// Resource and cancellation
// -------------------------
//
// A depth/node limit is a compiler resource diagnostic (currently in the SOL2290 family). The
// result contains no complete candidate set. The call/member source node remains Unverified. A
// caller cannot transform this state into Missing, because doing so would mislead tooling and could
// select a less-specific overload.
//
// Cancellation is not a language error. It propagates to the analysis owner and prevents a partial
// immutable result from being returned as complete. If an API later exposes partial results, it
// must label them partial and keep their candidate completeness bit false.

// Stable diagnostic ordering
// --------------------------
//
// Diagnostics sort by source path ordinal, byte-span start, then code. Multiple diagnostics at one
// span sort by code and canonical declaration signature. This order is reproducibility metadata;
// it does not mean the first error has semantic priority over a different candidate.
//
// Source spans use the immutable source object's identity and byte coordinates. A diagnostic from
// a foreign source snapshot is rejected when queried through an owned analysis API. Equal paths or
// equal node numeric IDs in different snapshots cannot transfer ownership.

// Diagnostic code families
// -------------------------
//
// SOL2210: unavailable current instance receiver.
// SOL2211: unresolved lexical value.
// SOL2212: ambiguous qualified value or member name group.
// SOL2214: inaccessible declaration, getter, setter, or constructor.
// SOL2215: complete lookup with no member declaration.
// SOL2221: no applicable callable signature.
// SOL2222: multiple equally best callable candidates.
// SOL2243: nullable receiver used without flow proof/safe access/assertion.
// SOL2244: ref/out argument marker or exact-storage contract mismatch.
// SOL2290: bounded type/member analysis resource exhaustion.
//
// The codes are stable API categories. Wording may improve, but tooling can distinguish missing,
// access, nullable, overload, and resource outcomes without parsing localized prose.

// Candidate inspection data
// -------------------------
//
// For an ambiguous/deferred member call, candidate inspection should retain:
//
// - source declaration and constructed declaring type;
// - constructed inheritance path/bound origin;
// - parameter, result, and checked effect identities after substitutions;
// - named/default/vararg mapping and source argument order;
// - each conversion rank and constraint result;
// - generic lower/exact/upper evidence origin;
// - rejection or defer reason;
// - accessibility result and accessor selected for value operations.
//
// JSON serialization must use schema versioning, explicit source-file indexes, and workspace-owned
// type references. It must not include `.codex` paths, user environment state, source secrets, or
// an unstable type ID presented as a cross-process identity.

// Diagnostics are part of developer experience, but concise wording cannot replace structured
// semantic evidence. A future Sol-Tidy or language server may consume these results independently;
// this specification does not merge their CLI/configuration contracts into Solc.
