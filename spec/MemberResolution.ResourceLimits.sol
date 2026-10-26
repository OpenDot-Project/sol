// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative bounded lookup contract; resource tuning does not define semantic selection.
module source.spec.member_resolution.resource_limits

// Bounded receiver lookup
// =======================
//
// Member lookup can traverse generic base graphs, type-parameter bounds, partial declarations, and
// diamonds. The compiler places explicit bounds on that work so malformed or adversarial source
// cannot consume unbounded memory/time or produce a false complete candidate group.

// Budget dimensions
// -----------------
//
// The development resolver uses independent limits:
//
// - maximum inheritance depth: 128 nodes from the starting receiver;
// - maximum distinct full path states: 8,192 per lookup;
// - maximum retained paths for one declaration/constructed owner: 128;
// - generic projection/common-type graph nodes: 4,096 per bounded operation;
// - generic structural nesting depth: 128;
// - inference facts/bounds per type parameter: 256;
// - TypeSpecialization new identity budget: 100,000 per workspace.
//
// Each budget has one owner. A member resolver does not consume the generic solver's remaining
// count as if the limits were interchangeable. Callers report the operation and limit dimension.

// Full path state
// ---------------
//
// A path state is the start receiver plus the ordered sequence of constructed base/bound identities
// traversed so far. The same final node reached via another prefix is another state because it
// carries distinct hiding/override/projection provenance.
//
// A path cannot revisit a constructed identity already in its own prefix. This closes a cycle. It
// does not prohibit the same type from being reached through a different prefix. A new generic
// identity reached at every step eventually reaches the total path/depth cap.
//
// Candidate path count and path-state count measure different structures. One declaration/owner
// can accumulate several paths from the queue; several owners can each retain several paths.

// Limit check placement
// ---------------------
//
// Check depth before expanding a node. Check path-state count before enqueuing a new full route.
// Check per-candidate route count before appending provenance. Check common-type/type-projection
// node count before expanding each unique constructed identity. Check inference bound count before
// retaining the next distinct fact.
//
// A limit check after allocating all child nodes defeats the memory budget. A limit checked only
// after candidate collection can return a partial result that callers may accidentally rank.
//
// Counts are deterministic for equivalent source identity graphs. Hash table order must not decide
// which route survives at the boundary. If deterministic resource behavior cannot be guaranteed,
// the operation must fail incomplete rather than truncate an arbitrary suffix.

// Limit result
// ------------
//
// A limit result includes status, owner operation, fixed cap, count or depth observed, source
// location, and whether any partial data exists for internal recovery. Public callers cannot treat
// partial data as a complete group. `Missing` and `NoApplicableCandidate` require complete search.
//
// The diagnostic family for member lookup is SOL2290. Generic specialization also has explicit
// resource errors translated at the source operation. A limit is not a malformed-language
// diagnostic, so tools can distinguish it from a user's invalid member reference.
//
// No automatic retry with doubled budgets occurs during ordinary analysis. A service may let its
// owner configure budgets before analysis; the selected values are part of analysis options and any
// cache key that depends on completeness.

// Cancellation
// ------------
//
// Check cancellation before dequeue, before base expansion, while merging name groups, and during
// generic override signature comparison. Cancellation unwinds candidate transactions and does not
// emit a source invalidity result.
//
// A canceled lookup cannot return the candidates seen before cancellation as complete. If an API
// supports partial UI results, it must carry an explicit partial/canceled bit and forbid overload
// selection from those candidates.

// Cache interaction
// -----------------
//
// A cache entry includes exact source/module/import versions, type-analysis owner, budgets, and
// cancellation-independent completion status. A partial result from a smaller budget cannot be
// reused as a complete result under a larger budget. A complete result under a larger budget may
// be reused for a smaller budget only if its recorded resource consumption proves the smaller cap
// was not crossed.
//
// Cache serialization uses logical source identities and schema version. It does not include
// absolute checkout paths or private notes. Process-local constructed type IDs are not persistent
// cache identities.

// Required stress matrix
// ----------------------
//
// - depth exactly below, at, and above 128;
// - path-state count exactly below, at, and above 8,192;
// - per-candidate routes exactly below, at, and above 128;
// - duplicate path versus distinct-prefix same-node route;
// - same symbol at multiple generic constructed owners;
// - cyclic graph with no new identities and recursively growing identity graph;
// - type projection at 4,096 nodes and beyond;
// - 256 repeated and 257 distinct inference bounds;
// - 100,000 identity interner boundary;
// - cancellation before start, mid queue, mid merge, and during signature compare;
// - source-file order permutation at each boundary;
// - a pending overload whose ranking would change if a partial path were ignored.

// Resource accounting
// -------------------
//
// The compiler may count visited identities and estimated work for telemetry, but metric export is
// opt-in and does not change source behavior. A telemetry failure cannot alter a complete result.
// Resource usage is not a GC pause guarantee or a benchmark result.
//
// Build cache records may include semantic budget identities. They must not include environment
// process IDs, username, wall clock, or nondeterministic queue-order counters.

// Current implementation boundary
// -------------------------------
//
// MemberLookupResolver enforces 128 levels, 8,192 path states, and 128 routes per declaration/
// constructed owner. GenericInferenceSolver and TypeSpecialization use their own depth/node/bound
// caps. Full shared resource accounting, end-to-end cancellation fixtures, telemetry export, and
// incremental cache proofs remain open.
