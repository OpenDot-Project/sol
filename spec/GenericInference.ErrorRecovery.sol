// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative generic candidate recovery contract; all diagnostics/serialization are not yet complete.
module source.spec.generic_inference.error_recovery

// Generic inference outcomes and error recovery
// =============================================
//
// Recovery keeps analysis useful after source errors without turning uncertainty into a successful
// type. The inference service, overload resolver, diagnostic owner, and public inspection API must
// agree on distinct terminal states.

// Solved
// -----
//
// Solved means every requested inference parameter has one unique type identity after applying the
// fixed substitution and all constraints. It does not mean that the generic body has been
// specialized, every flow rule has been checked, FIR is verified, or code can be emitted.
//
// A solved type map is candidate-owned. It is published as a selected call binding only after
// overload ranking has one winner. A solved candidate in an ambiguous call does not create one
// global call result.

// Missing evidence
// ----------------
//
// Missing means a required parameter has no usable lower/exact/expected/contextual fact. Examples
// include an empty array element T, an omitted default-only T, an untyped lambda that only returns
// an unresolved input T, or an effect-only E with no matched actual effect.
//
// Missing is not Conflict: no incompatible fact exists. It is not `T = Object`, `T = !`, an empty
// string type, or the first base visited. The call becomes Deferred when a missing parameter could
// affect candidate applicability/ranking.

// Conflicting evidence
// --------------------
//
// Conflict means facts exist but no one type satisfies all exact/lower/upper constraints. Examples
// include two incompatible exact ref/out identities, one lower bound that cannot convert to any
// upper bound, or invariant `Box<int>` and `Box<String>` evidence for one Box<T> pattern.
//
// A conflict can reject one overload while leaving another candidate independently applicable.
// Keep all bounded relevant source origins. Do not discard the whole candidate set because one
// overload conflicted.

// Incompatible shape
// ------------------
//
// IncompatibleShape means structural matching reached a known mismatch: different nominal
// declaration under exact matching, tuple arity, fixed-array length, function parameter count/mode,
// nullable shape, or invariant generic constructor. The compiler can reject that candidate with a
// shape reason and continue other candidates.
//
// Missing type metadata is not a shape mismatch. Unavailable metadata or unsupported conversion
// should remain Deferred when it could change the answer.

// Ambiguous inference
// -------------------
//
// Ambiguous inference means two or more distinct minimal common types/mappings satisfy all known
// constraints. It is distinct from overload ambiguity: one candidate can have ambiguous type
// inference, and two fully solved candidates can be tied by overload ranking.
//
// Declaration order, base-list order, imported module order, numeric ID, hash iteration, and
// diagnostic sorting cannot resolve ambiguity. Explicit type arguments or an expected result may
// provide a new source constraint if that context is semantically immediate and valid.

// Deferred
// --------
//
// Deferred means an implementation or input fact is incomplete. A potentially applicable
// candidate remains a blocker if its unknown conversion vector can beat a known candidate. The
// call-level status is Deferred even when another candidate is fully Solved.
//
// Unsupported user conversion, unavailable nested lambda type, incomplete receiver metadata,
// unresolved constraint, unavailable expression return type, and candidate-specific effect proof
// are examples. Deferred does not mean the source is valid; it means this analysis cannot prove a
// final result.

// Resource limit
// --------------
//
// ResourceLimit identifies bounded compiler work such as type-depth, graph node count, path count,
// per-parameter bounds, nested lambda depth, or type interning capacity. Each limit has a specific
// owner and stable configured cap. It is not source inapplicability and not an unbounded retry.
//
// No partial evidence prefix is solved after a limit. A partial candidate table cannot establish a
// unique best overload. The source location remains unverified and the diagnostic reports which
// operation consumed the budget.

// Canceled
// -------
//
// Cancellation belongs to the analysis owner, not to Sol source semantics. It propagates through
// nested type matching, lambda replay, member lookup, array common-type search, and diagnostics.
// Cleanup/restoration occurs before propagation. No SOL2215/SOL2221/SOL2290 source error is
// synthesized solely because a user canceled.

// Candidate aggregation
// ---------------------
//
// Aggregate complete candidate outcomes after each candidate transaction ends:
//
// 1. If every candidate is proven rejected, report no applicable candidate with bounded reasons.
// 2. If one or more candidates are complete and one is uniquely better than every complete peer,
//    inspect deferred candidates for possible rank interference.
// 3. If any deferred candidate could tie or beat the apparent winner, keep call Deferred.
// 4. If one complete candidate wins and every deferred candidate is proven worse, select it.
// 5. If two complete candidates are incomparable/equal-ranked, report overload Ambiguous.
// 6. If a global resource/cancellation result prevents complete enumeration, publish no winner.
//
// A conflict within one candidate is not a global resource failure. A resource failure while
// enumerating the entire candidate set is not a rejection of only the last candidate.

// Diagnostic code ownership
// -------------------------
//
// Argument mapping owns duplicate/name/order errors. Generic solver owns Missing/Conflict/Shape
// reasons. Constraint checker owns bound mismatch/indeterminate status. Member resolver owns
// missing/ambiguous/resource path results. Access checker owns visibility. Expression binder owns
// conversion, nullable, storage, and lambda-body diagnostics. The call site combines those facts
// without changing their meaning.
//
// A single source expression can have more than one independent diagnostic, subject to the user
// diagnostic cap. Capping visible messages cannot set `IsComplete`, remove a blocking Deferred
// candidate, or select a different overload.

// Recovery expression types
// -------------------------
//
// Error types suppress cascades but are never successful inference evidence. Bottom `!` is a real
// source type and converts according to its language rule; Error is not Bottom. Nullable-bottom
// represents null and remains distinct from a missing type.
//
// If a subexpression has an error type, a candidate may continue collecting unrelated diagnostics,
// but its final status is not a complete successful binding. An unsupported expression without a
// concrete type is Deferred rather than Error when the source itself has no proven invalidity.

// Required status matrix
// ----------------------
//
// - no evidence for one parameter;
// - exact conflict and lower/upper conflict;
// - nested invariant shape mismatch;
// - unique and non-unique common type;
// - one candidate rejected and another solved;
// - one candidate deferred and another apparently better;
// - a deferred candidate proven worse by exact bound;
// - per-candidate limit and whole-set enumeration limit;
// - canceled lambda and canceled receiver path;
// - diagnostic cap reached with a remaining blocker;
// - error subexpression plus independent candidate constraints;
// - input source error with otherwise solved method substitution.

// Public API boundary
// -------------------
//
// Inspection APIs preserve `Bound`, `NoApplicableCandidate`, `Ambiguous`, `Deferred`, `Unresolved`,
// `ResourceLimit`, and `Canceled` as separate categories. A convenience method may return null for
// several categories only if the detailed status remains accessible. It cannot document null as
// proof of no applicable candidate.
//
// Serialized outcomes include schema version and owned source-file indexes. They do not expose
// private notes, current working directory, user name, machine path, or process-local object ID.

// Current implementation boundary
// -------------------------------
//
// Generic inference exposes Solved/Missing/Conflict/Shape/ResourceLimit internally, and call/body
// analysis distinguishes Bound/Deferred/Ambiguous/Rejected outcomes at its current scope. Complete
// public candidate reason serialization, whole-set cancellation status, robust diagnostic codes for
// every inference failure, and semantic regression qualification remain open.
