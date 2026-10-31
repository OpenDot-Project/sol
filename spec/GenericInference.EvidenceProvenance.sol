// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative evidence provenance and inspection contract; complete candidate serialization is pending.
module source.spec.generic_inference.evidence_provenance

// Generic inference evidence provenance
// =====================================
//
// A substitution without its evidence is difficult to explain, validate, cache, or safely recover
// from. Every generic inference fact therefore retains its parameter identity, relation, actual
// type, source origin, and candidate owner until the candidate reaches a final status.

// Parameter identity
// ------------------
//
// The inference parameter key is the owning declaration identity plus ordinal. Type spelling is
// presentation only. A class `T`, method `T`, lambda's contextual T, and imported metadata T remain
// separate unless the structural type graph explicitly connects them.
//
// An analysis-session-local integer may index a parameter for performance. Serialization uses a
// declaration/file identity with schema version, generic parameter ordinal, and module/distribution
// identity. A local integer is not a durable cache key.

// Evidence relation
// -----------------
//
// Each fact records one relation:
//
// - Input: a source value contributes a lower/input bound to the formal type pattern;
// - Exact: ref/out, invariant nested identity, typed lambda parameter, or exact array element shape;
// - Expected: an enclosing result type constrains the generic result pattern from outside;
// - EffectSet: an unordered checked-effect relation, normalized independently of source order;
// - ContextualResult: a candidate lambda's independently known body result;
// - ArrayElement: a literal element contributes a type and element index;
// - Projection: a constructed base/bound route supplied a nested type identity.
//
// An implementation may store several specialized relation kinds as Input/Exact/Expected plus a
// reason code. It cannot discard the semantic direction and later infer it from a text message.

// Source origin
// -------------
//
// An origin identifies the immutable source snapshot, source file index, span, expression kind,
// argument index, formal parameter index, and evidence subpath. An array element records its zero-
// based element index. A lambda result identifies the lambda body exit; a lambda effect identifies
// the operation/catch route that emitted it.
//
// Receiver projection evidence records every constructed edge used to reach the matching base.
// Type parameter bounds record the source declaration and ordinal of each inline/where constraint.
// Expected-result evidence records the immediate contextual expression owner rather than an
// arbitrary outer caller discovered by whole-program search.
//
// File paths in the public inspection format are source-set-relative or file-indexed. `.codex`,
// home-directory, username, checkout root, environment variables, and absolute developer paths are
// never serialized.

// Stable provenance rules
// -----------------------
//
// Equivalent source sets loaded in another file enumeration order produce the same semantic facts.
// Presentation may sort by parameter owner/ordinal, relation, constructed type, and source span.
// Sorting cannot merge distinct parameter owners or select a unique type from an ambiguous set.
//
// Deduplicate facts only when parameter identity, relation, type identity, and origin identity all
// match. Two equal types from different arguments remain separate provenance facts because a later
// conflict diagnostic may need to identify both. Duplicate inheritance routes to one exact
// constructed identity can be summarized as one type bound with multiple path origins.
//
// A failed candidate preserves its rejection/defer facts in candidate inspection but publishes no
// selected substitution. A selected candidate retains the evidence supporting every closed generic
// argument. A serializer cannot emit an unproven open type argument as if it were closed.

// Conflict explanation
// --------------------
//
// A useful generic diagnostic identifies the parameter and the smallest conflicting origins. For
// example, an exact ref storage bound and a lower value bound that disagree should point to both
// arguments and state that exact storage cannot be widened. A result upper bound conflict points to
// the expected type expression and the input argument that made the lower bound too broad.
//
// A conflict is different from missing evidence. A parameter with no facts is Missing. Two exact
// identities that disagree are Conflict. Multiple valid most-specific candidates are Ambiguous.
// A traversal cap is ResourceLimit. Cancellation is not a source result.
//
// Candidate reason text may cap displayed origins (currently at eight) for concise diagnostics.
// Structured inspection retains all bounded facts or a separate “additional origins omitted” count.
// The message cap cannot change the inference result or candidate completeness.

// Evidence from call mapping
// -------------------------
//
// Map source arguments before collecting facts. Preserve source and formal indexes. Named argument
// order changes evaluation order only according to the source sequence; it does not change the
// formal parameter identity. Vararg elements, spread arrays, named arrays, defaults, and trailing
// lambdas use distinct origin kinds.
//
// Omitted defaults contribute no bound. An `out var` has no source storage type until one candidate
// wins, so it cannot choose that candidate. Explicit generic arguments are Fixed facts, not
// inferred evidence. Receiver substitutions are OwnerFixed facts and remain separate from method
// parameters.
//
// A literal has both a source default type and exact token value. Its type relation cannot erase the
// token value before representability has been checked. A null literal has nullable-bottom identity
// and does not invent a non-null nominal type.

// Evidence from contextual lambdas
// -------------------------------
//
// A typed lambda parameter contributes an exact shape fact. An untyped or positional parameter
// receives the expected type but contributes no self-evidence. An independently typed lambda body
// result contributes to an open result pattern. An unresolved result that simply echoes an open
// input remains an equation, not a closed type fact.
//
// Effect facts are sets. A source origin is attached to the thrown operation, but the order in which
// the operation was encountered cannot pair E and F. The solver records the set matching proof or
// the number of unresolved mappings.
//
// Lambda evidence is owned by one candidate transaction. On rejection, it remains available only
// as a rejection reason. It cannot be copied to another candidate's evidence table.

// Evidence from arrays
// --------------------
//
// For a formal array `[]T`, each independently typed element contributes an ArrayElement input fact
// to T. For a free T passed a non-empty literal, one unique common element type is first selected
// and the constructed `[]E` contributes an input fact to T. A named/spread vararg array uses exact
// element identity after common-type selection.
//
// Each fact preserves the original element expression and nested index path. A fixed array records
// every checked length layer. Empty array provides no element fact and does not create a common
// type.

// Evidence from member paths
// --------------------------
//
// A receiver base projection contributes the actual constructed type and path. If a source type
// reaches `Source<T>` through two routes, retain both routes. If the routes produce `Source<int>`
// and `Source<String>`, preserve both exact constructed facts and report a conflict.
//
// A bound from `where T: IReadable<String>` identifies the bound owner and substituted enclosing
// arguments. A type parameter with two bounds does not let the first visited bound win.

// Diagnostic provenance budget
// ----------------------------
//
// Evidence storage has a fixed per-parameter bound count. Common-type and base-projection searches
// have independent node/depth limits. If any cap is exceeded, return a resource status attached to
// the operation and retain the number/category of omitted facts when available.
//
// The compiler must not trim evidence first and solve from the prefix. The first 256 values may
// differ from the last 256 under file/argument ordering. Exceeding a cap invalidates completeness
// rather than selecting a result from a sample.
//
// Cancellation checks occur during nested type matching, path traversal, candidate loops, array
// element collection, lambda body replay, and diagnostic formatting. Canceled fact tables are not
// returned as immutable successful results.

// Analysis API and JSON
// ---------------------
//
// Public analysis exposes stable status, candidate symbol, specialized parameter/result/effect
// types, source mapping, and bounded rejection reason. A future detailed evidence API is versioned
// separately and uses file indexes and declaration-owned identities.
//
// JSON ordering is deterministic and ordinal. It includes null explicitly where a type is unknown,
// distinguishes absent evidence from empty evidence, and preserves Deferred versus Rejected. It
// does not serialize hash iteration order or internal object references.
//
// Deserialization cannot feed arbitrary type IDs back into `TypeSpecialization`. A read-only report
// is not a source binder input, cache database, executable artifact, or trusted proof signature.

// Required checks
// ---------------
//
// - named and positional argument origin retention;
// - fixed owner and inferred method parameter separation;
// - exact ref/out plus lower-bound conflict origins;
// - expected result upper bound and lambda lower-bound conflict;
// - two equal type facts at distinct source locations;
// - repeated path fact with multiple route origins;
// - array element index and nested-rank provenance;
// - lambda parameter/result/effect origins in a rejected candidate;
// - candidate rollback without deleting prior diagnostics;
// - deterministic output after source and hash-order permutation;
// - explicit omitted-fact count after diagnostic truncation;
// - cancellation and resource status without successful partial substitutions.

// Current implementation boundary
// -------------------------------
//
// The shared generic solver retains parameter, relation, type, and origin facts with bounded
// candidate reason output. Complete public evidence serialization, array/lambda origin expansion,
// incremental cache identity, and verified FIR consumption remain open.
