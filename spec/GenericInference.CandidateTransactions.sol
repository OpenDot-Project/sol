// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative candidate transaction contract; complete replay of every body form is pending.
module source.spec.generic_inference.candidate_transactions

// Candidate-local analysis transactions
// =====================================
//
// Overload resolution is speculative until a unique candidate has been selected. The source
// expression itself is immutable, but analysis tables, type interners, flow snapshots, and
// diagnostics may be mutable implementation details. Their lifetime must not leak across
// candidates.

// Transaction boundary
// --------------------
//
// A candidate transaction begins after lexical target lookup and source argument mapping have
// produced a candidate-specific formal index. It ends when that candidate has an Applicable,
// Rejected, Deferred, ResourceLimit, or Canceled outcome. The transaction owns:
//
// - the candidate's explicit and inferred generic substitution;
// - all lower, exact, expected, array, lambda-result, parameter, and effect constraints;
// - constructed receiver projections and member path facts;
// - lambda expected types and lambda-local parameter symbols;
// - candidate-local nested call selections and expression types;
// - definite assignment, narrowing, mutation invalidation, and cleanup exits;
// - checked effect observations and their source origins;
// - candidate-specific diagnostics and unverified nodes;
// - recursion depth, visited candidate identities, and bounded work counters.
//
// Immutable source declaration tables, AST identity, source text, and shared canonical type facts
// may be read. A transaction cannot mutate source declarations or another candidate's result.

// State-entry rules
// -----------------
//
// Every candidate starts from the same incoming caller state. It does not start from the state
// after a previous candidate's lambda was analyzed. A rejected candidate's assignment, nullable
// narrowing, checked effect, and synthetic out variable are discarded.
//
// Every lambda argument in one candidate also starts from the state appropriate to its lexical
// position. If the language says arguments are evaluated left to right, prior explicit argument
// expressions affect the eventual selected execution state, but candidate inspection does not
// commit those effects. A later lambda cannot observe an earlier candidate's speculative write.
//
// An implicit receiver retains its source static type. A generic method owner substitution is
// local to the current receiver candidate. Bound-path substitutions for a second overload do not
// overwrite the first candidate's owner map.

// State-exit rules
// ----------------
//
// Candidate inspection returns a value record and an applicability record. It does not leave
// records in the selected-call table. On unique selection, the winner's argument/lambda bodies are
// analyzed against the closed signature and publish the ordinary BodyAnalysis facts.
//
// On ambiguity, no candidate's lambda type becomes the source-level expression type. The analysis
// retains candidate-specific facts under the call result for inspection, or reports an ambiguous
// result with all surviving candidate identities. It cannot choose the first cached lambda result.
//
// On deferred selection, candidate completeness remains false. A known applicable candidate can
// win only when every unresolved candidate is proven unable to outrank it. A candidate that might
// have a better conversion rank cannot be assigned an artificial worst rank.

// Snapshot/restore contract
// -------------------------
//
// A mutable-table implementation may snapshot dictionaries and sets before replay. Restoration
// must preserve existing values and identity comparers. Clearing and restoring a candidate table
// must not replace the global `SourceAnalysis`, `TypeAnalysis`, or canonical source snapshots.
//
// Restore the previous diagnostic count and de-duplication keys after speculation. A user-supplied
// earlier diagnostic is not owned by the candidate and cannot be truncated. For an all-candidates-
// rejected result, the compiler can publish one chosen primary reason plus structured candidate
// outcomes; the reasons are collected outside discarded speculative buffers.
//
// Restore active recursion markers and analysis depth in a finally path, including when a nested
// type conversion, cancellation, parser error-recovery node, or resource exception exits the
// candidate early. Cancellation is propagated after cleanup; it is not converted into a candidate
// rejection.
//
// Type interning can be monotonic across candidate transactions only if it is deterministic,
// bounded, and semantically invisible. Speculative types must not be exported as selected call
// results, and exhausted interning budget produces a resource outcome rather than success.

// Source expression evaluation
// ----------------------------
//
// Type inspection is not runtime evaluation. Candidate analysis may inspect a literal token, local
// symbol, declared property type, constructed base, or selected nested call shape. It may not:
//
// - execute a getter or setter;
// - call a function or constructor at runtime;
// - execute a default expression;
// - allocate an array or collection value;
// - run a lambda body as a program;
// - start a process, network operation, task, thread, or GC action;
// - consume a random value, wall clock, or mutable global.
//
// The selected call evaluates receiver, explicit arguments, and omitted defaults according to
// their source-language order. Each expression is evaluated once. Candidate replay must not add
// side effects to the generated program or to source flow facts.

// Nested overloads
// ----------------
//
// A lambda body can contain another overloaded call. The nested call receives the outer candidate's
// expected function parameter and result types. Its candidate table is nested under the current
// transaction; a rejected inner candidate cannot escape into another outer candidate.
//
// If the nested call depends on an outer open type variable, evidence can flow outward only through
// an explicit result/effect/parameter relation retained by the outer lambda analysis. Arbitrary
// search of unrelated call sites or whole-program reverse propagation is not permitted.
//
// Recursive lambda/call inference uses a finite worklist keyed by declaration-owned parameter and
// source candidate. A repeated unchanged state reaches a stable fixed point or an explicit cycle
// status. It cannot recursively allocate unbounded fresh substitution maps.

// Candidate cache
// ---------------
//
// A candidate-local analysis cache, if present, includes at least source lambda identity, formal
// function identity, generic substitution identity, flow snapshot identity, visible declaration
// set/version, and analysis options. A source offset alone is insufficient. A printed type name is
// insufficient. Cache entries are immutable and are never reused across a different source
// snapshot or TypeAnalysis owner.
//
// Cache invalidation follows dependency changes: edits to a base edge, accessor effect, imported
// signature, default, constraint, source argument order, or lambda body invalidate dependent
// candidate facts. A cache hit returns the exact same completeness and diagnostic origins as the
// uncached computation.

// Result model
// ------------
//
// | State | Meaning | May rank? | May publish body facts? |
// |-------|---------|-----------|-------------------------|
// | Applicable | Complete closed signature and constraints | Yes | Only after unique selection |
// | Rejected | Proven inapplicable for this candidate | No | No |
// | Deferred | Missing proof can affect applicability/rank | Blocks when relevant | No |
// | Ambiguous | Multiple complete candidates tie | No winner | No winner facts |
// | ResourceLimit | Candidate set may be incomplete | No | No |
// | Canceled | Owner stopped analysis | No | No result claiming complete |
//
// No state may be converted to another solely to make an inspection API look simpler. The CLI may
// abbreviate wording, but machine-readable status preserves the distinction.

// Required isolation checks
// -------------------------
//
// - candidate A narrows a nullable local; candidate B still sees the original flow state;
// - candidate A assigns an out local; candidate B cannot observe that assignment;
// - candidate A adds IOException; candidate B's empty effect budget remains empty;
// - candidate A infers lambda parameter String; candidate B receives its own Object context;
// - candidate A fails; no expression/call mapping from A remains published;
// - candidate A and B tie; no candidate-local lambda type is chosen globally;
// - candidate work reaches a limit; previously collected candidates do not appear complete;
// - cancellation during nested lambda analysis restores tables and propagates;
// - source declaration order permutation preserves candidate statuses and selected result;
// - analysis reuse after adding an unrelated source file preserves stable existing bindings.

// Current implementation boundary
// -------------------------------
//
// BodyBindingBuilder uses bounded snapshot/restore around candidate-local lambda and array
// expression inspection. The shared type specialization workspace remains monotonic. Direct
// CallAnalysis does not own flow or lambda body state and remains a smaller query. Full persistent
// candidate result APIs, all nested conversions, incremental invalidation, and semantic regression
// qualification are still required before advertising complete overload analysis.
