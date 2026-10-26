// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative candidate evidence ledger; public diagnostic serialization remains pending.
module source.spec.generic_inference.candidate_evidence_ledger

// Generic candidate evidence ledger
// =================================
//
// A generic call candidate owns a ledger of type facts gathered from its receiver, explicit type
// arguments, mapped values, expected result, array expressions, and candidate-local lambdas. The
// ledger is an analysis artifact, not an execution log or a public stable type-identity format.

// Evidence record
// ---------------
//
// One fact records:
//
// - declaration-owned type parameter identity;
// - actual type identity in the same type-analysis workspace;
// - relation: input/lower, exact, or expected/upper;
// - source argument index and source span;
// - structural path within a generic application or function signature;
// - origin category: receiver, explicit, ordinary argument, ref, out, expected result, array element,
//   lambda parameter, lambda result, or supported effect pattern;
// - analysis round/candidate provenance when a contextual replay produced the fact.
//
// Display names and integer IDs are derived data. Equality uses workspace-owned type identity and
// declaration ownership. Origins are sorted only for stable diagnostics; their order does not
// select a type.

// Relations
// ---------
//
// Input evidence gives a lower-bound conversion relation. Exact evidence requires identical
// storage or invariant nested argument identity. Expected-result evidence constrains a return type
// from the surrounding expression context. A candidate type must satisfy every fact under the
// declared conversion and constraint rules.
//
// A lower bound is not an exact assignment. A solver may choose one unique common type if all lower
// facts convert and all upper/expected facts are met. It may not widen numeric locals, box ref-like
// values, or change invariant generic arguments to manufacture such a type.
//
// Ref and out facts are exact and also require a writable source location, matching mode, and exact
// storage type after substitution. A syntactic `out var` is typed only after candidate selection;
// its unresolved local type cannot decide the candidate that creates it.

// Evidence lifecycle
// ------------------
//
// 1. Create an empty ledger for each independently mapped candidate.
// 2. Add receiver substitutions and explicit method/type arguments as fixed entries.
// 3. Add independently typed argument and expected-result facts.
// 4. Add array-element facts only after literal shape and argument mapping are known.
// 5. Solve; reject conflicts before speculative lambda bodies are analyzed.
// 6. Add lambda evidence under private candidate transactions and preserve its exact origin.
// 7. Re-solve open parameters at each bounded fixed-point round.
// 8. Check owner/method constraints, conversions, modes, effects, and completeness.
// 9. Keep the ledger private until candidate selection; publish only the final selected bindings
//    and bounded explanatory outcome supported by the public analysis API.
//
// Candidate copies are logically immutable snapshots. Adding a fact to candidate A cannot alter
// the fixed substitution, local types, or evidence ordering of candidate B.

// Duplicate facts
// ---------------
//
// Replaying a lambda may rediscover an identical `(parameter, type, relation)` fact. The solver may
// deduplicate that bound for candidate choice while retaining its distinct source origins. Two
// facts with the same type but different relations are not identical: exact and input evidence have
// different obligations.
//
// Two equal type IDs from different analysis workspaces are not equal evidence. Foreign identities
// are rejected at the service boundary. Cancellation and resource exceptions are not ledger facts.

// Deterministic conflict explanation
// ----------------------------------
//
// When facts conflict, report a stable subset of relevant origins. The explanation is bounded so
// adversarial source does not create unbounded diagnostics. Omitted origins do not cease to
// participate in the solver; display truncation is not evidence truncation.
//
// Candidate explanation order uses source argument position, structural child position, relation,
// type display, and origin category. It does not use hash iteration order or whichever lambda was
// analyzed first.
//
// If a closed lower-bound solution conflicts with an exact fact, retain both in the explanation.
// If a nested invariant shape fails before reaching the parameter, identify the complete source
// argument and the first incompatible type-argument path. If an effect pattern remains ambiguous,
// report incomplete evidence rather than claiming an arbitrary mapping conflict.

// Ledger source matrix
// --------------------
//
// | Origin | Relation | Can close parameter? | Replayed? |
// |---|---|---:|---:|
// | constructed receiver | fixed substitution | already fixed | no |
// | explicit generic argument | fixed substitution | yes | no |
// | ordinary value `T` | input/lower | yes | no |
// | invariant `Box<T>` argument | exact nested | yes | no |
// | ref location | exact storage | yes | no |
// | out variable | none before selection | no | no |
// | expected call result | expected/upper | yes if unique | no |
// | array element | input/lower | yes | candidate-specific shape only |
// | typed lambda parameter | exact | yes | yes per candidate round |
// | untyped lambda parameter | contextual only | no by self-evidence | yes per candidate round |
// | lambda body result | input/lower | yes if independent and closed | yes per candidate round |
// | one open effect pattern | exact set residual | yes if unique | yes per candidate round |
// | multiple open effects | unresolved set | no current unique mapping | yes, bounded |
// | omitted default | none | no | evaluated after selection |

// Worked evidence ledger
// ----------------------
//
// For `func <T> choose(left: T, right: T): T`, a call `choose("a", "b")` records two input facts
// for the same method-owned T. The common-type solver chooses String if it is the unique
// most-specific type satisfying both. It does not pick one argument and ignore the other.
//
// For `func <T> exact(value: Box<T>, destination: ref Box<T>)`, the nested Box argument contributes
// exact T evidence and ref storage contributes exact whole-type identity. The call is valid only
// when both constructed arguments agree and the destination is writable.
//
// For `func <T> transform(value: T, mapper: (T) -> T): T`, the ordinary value may close T before
// lambda replay. The mapper then receives that closed function type and contributes no new type fact
// merely by using its contextual parameter. Its body must still be rechecked for applicability.
//
// For `func <T> transform(mapper: (T) -> T): T`, a lambda `{ $0 }` supplies no independent type
// fact. The parameter and returned value are both the same unknown T. The call remains unresolved
// unless an expected result, explicit argument, typed lambda parameter, or independent body result
// closes T.

// Bounds and rejection matrix
// ---------------------------
//
// | Evidence set | Expected result | Candidate disposition |
// |---|---|---|
// | no fact for T | T remains open | deferred/missing evidence |
// | one lower String | unique String | continue after constraints |
// | lower String, lower subtype | unique supported common type | continue |
// | lower unrelated interfaces | two minima | conflicting/ambiguous inference |
// | exact String, exact String | exact String | continue |
// | exact String, exact int | incompatible exact facts | reject |
// | lower String, expected subtype | no compatible candidate | reject |
// | invariant Box<String>, formal Box<T> | exact T=String | continue |
// | invariant Box<String>, formal Box<int> | shape mismatch | reject |
// | nullable String, non-null T | no nullable erasure | reject or defer per solver |
// | bottom actual | no useful lower fact | preserve language bottom rule |
// | error actual | recovery only | do not treat as successful closed evidence |
// | same bound repeated twice | deduplicated bound | origins remain deterministic |
// | one type identity from another analysis | invalid input | throw boundary error |

// Constraint phase
// -----------------
//
// Type parameter constraints are checked after the solver obtains a closed candidate substitution.
// A constraint is not itself permission to guess a type argument. Inline bounds and every matching
// where-clause bound must be checked with the completed owner/method map.
//
// A known-unsatisfied constraint rejects only the candidate. An indeterminate constraint keeps the
// candidate incomplete. Ref-like anti-constraints and nullable restrictions remain independent of
// nominal conversion evidence. A successful bound does not grant boxing, heap storage, or an
// unchecked effect conversion.

// Resource and cancellation
// --------------------------
//
// Structural collection is bounded by depth and graph-node caps. A parameter's distinct facts are
// bounded. Candidate replay is bounded separately. Failure at any budget yields an incomplete
// resource result; no partial ledger is labeled solved.
//
// Cancellation is observed while collecting, projecting, comparing, sorting explanatory origins,
// and replaying candidate expressions. The active speculative transaction restores its state in a
// finally path before cancellation escapes. Cancellation is never recorded as an ordinary candidate
// rejection.

// Required invariants
// -------------------
//
// - Every fact has a candidate owner, declaration-owned parameter, relation, and source origin.
// - Lower, exact, and expected relations are never silently converted into one another.
// - Replayed identical evidence does not change type choice.
// - Source order cannot break a conflict or tie.
// - Error nodes and foreign identities do not produce successful substitutions.
// - Unresolved evidence is not represented as Object or a default generic argument.
// - Constraint checks consume the completed map and never create missing evidence.
// - Diagnostic truncation does not truncate solver inputs.
// - Resource and cancellation outcomes cannot claim completeness.
// - Only selected call bindings become public analysis facts.

// Current implementation boundary
// -------------------------------
//
// The internal solver retains candidate facts and origins for bounded input/exact/expected,
// array, lambda, and supported effect inference. The public analysis SDK does not expose a stable
// evidence-ledger serialization format, FIR obligations, or a complete generic body checker.
