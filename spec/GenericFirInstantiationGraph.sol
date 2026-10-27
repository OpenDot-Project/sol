// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative graph contract; the current FIR builder uses an exact-key map and bounded FIFO worklist.
module source.spec.generic_fir_instantiation_graph

// Generic FIR instantiation graph
// ===============================
//
// This contract defines the finite graph that connects ordinary root functions to closed generic
// function instances. It complements `GenericFirSpecialization.sol` and
// `GenericFirSpecializationLimits.sol`: those files define admission, substitution, and aggregate
// budgets; this file defines instance identity, graph closure, recursion, deterministic scheduling,
// and publication. An implementation must apply all three contracts together.
//
// Current implementation status
// ------------------------------
//
// The current FIR builder implements a narrower mechanism than the complete contract below:
//
// - It creates instances only for selected module-level generic free-function calls that pass the
//   existing closed-type, source-body, constraint, and native verifier checks.
// - It keys an in-memory map by the source `AstFunction` object identity and the ordered, comma-
//   delimited invariant-culture numeric IDs of the substituted type arguments.
// - It inserts the map entry and reserves an integer FIR function ID before enqueueing a new body.
//   A later request with the same key returns the reserved ID. This permits an exact-key recursive
//   call to form a reference to the instance whose body is currently being processed.
// - It drains a FIFO queue. Ordinary functions are allocated and built in declaration enumeration
//   order; selected generic targets are discovered while those bodies are lowered and are enqueued
//   in the order the builder encounters their calls. A dequeued generic body can enqueue more work.
// - It applies limits of 4,096 distinct generic instances, 16,000,000 accounted source-body AST
//   nodes, and 128 structural type levels. Resource exhaustion is reported with `SOL2403`.
// - The queue is bounded by the instance budget and finite body accounting. There is no separate
//   configurable recursive-call-depth budget in the current queue implementation.
//
// These facts do not imply canonical IDs across reordered source inputs, persistent-cache identity,
// full source rebinding for each instance, all graph diagnostics described here, or support for all
// source-level generic functions. The details labeled normative below are requirements for a fully
// conforming implementation. They are not claims that each requirement already ships.

// 1. Graph vocabulary
// -------------------
//
// A generic declaration is one canonical module-level function declaration with at least one
// function-owned type parameter. A root is an ordinary, non-generic function included by the
// selected artifact closure policy. An instance key is the pair of one canonical declaration
// identity and one complete ordered tuple of closed type argument identities. An instance is the
// unique graph node for that key within one compilation closure. An edge is a selected generic call
// from an ordinary root or an instance body to a target instance. An origin is the source call site
// that caused or refers to an edge. A back edge is an edge to a node already reserved on the active
// construction path. A sharing edge is an edge to an already reserved node not currently active.
//
// A graph is rooted: every instance in the graph is reachable from at least one artifact root by
// zero or more selected call edges. An instance is not rooted merely because its declaration is
// public, because its type is interned, because it was mentioned as an explicit type argument, or
// because a stale cache entry exists. Roots may vary by artifact kind, but the chosen root policy is
// explicit and part of artifact semantic identity.
//
// A closed argument tuple contains exactly one argument for each function-owned parameter in
// declaration ordinal order. No tuple element may contain an unresolved parameter, error recovery
// type, inference variable, or workspace-external identity. Constraints are proven before key
// reservation. A body cannot add a partially known node and repair its key later.
//
// An edge records the selected declaration, exact closed type-argument tuple, argument mapping,
// call origin, and the target instance key. It also preserves source evaluation order independently
// of generic parameter order. Multiple call origins may refer to one target node. Node reuse never
// merges call edges or erases their source origins.

// 2. Canonical key and identity domains
// -------------------------------------
//
// The semantic key is declaration identity plus an ordered sequence of type identities. It is not
// a function's printed name, generic arity alone, display signature, source line, path, module
// spelling alone, overload position, generated FIR ID, pointer address, hash code, or argument
// spelling. Two overload declarations remain different key owners even if their displayed names
// and signatures happen to match. A declaration's parameter names are not type identities.
//
// The argument tuple is ordered by the callee declaration's parameter ordinal, not by source
// argument order, named argument order, inference visitation order, hash-table order, or the caller's
// parameter ordinal. Every unused generic parameter remains in the key. For example, if `f<A, B>`
// never mentions `B` in its body, `f<int, byte>` and `f<int, long>` remain distinct instances.
//
// Type identity is structural and owned by the current source/type-analysis snapshot. Equal closed
// source types in one ownership domain compare as the same semantic identity. Nullability, array
// rank, tuple order, nominal declaration identity, generic argument order, function passing modes,
// result type, and effect set all participate wherever they occur. Effect members are compared as a
// mathematical set under the language's effect identity rules, not as insertion-ordered strings.
//
// Current FIR uses workspace-local `ResolvedType.Id` values in its temporary key string. These IDs
// are valid only inside the owning `TypeSpecialization`/analysis workspace and current process.
// Their integer values are allocation details. They are adequate for the present dictionary because
// the builder consumes one owned analysis snapshot and retains exact `ResolvedType` objects. They
// are not a stable cache key, serialized identity, ABI name, cross-process token, or reproducibility
// guarantee. A numeric ID from another analysis cannot identify a type by equality alone.
//
// A future persistent or distributed cache key MUST encode the canonical declaration identity and
// canonical structural type identities, along with language/ABI/backend version, target, relevant
// feature gates, and the input semantic dependency digest. It MUST use an unambiguous encoding with
// explicit lengths or a canonical structured format. Delimiter-concatenated display text is not a
// canonical encoding. A cache entry is data to validate, not authority to skip current source
// analysis, constraints, graph closure, or verifier checks.
//
// Hashes accelerate lookup only. Implementations MUST compare full canonical identity after a hash
// match and MUST remain correct under hash collision. A hash collision cannot merge two overloads or
// different argument tuples. If an entry's declaration or type ownership is no longer valid, the
// entry is a miss or a diagnosed invalid cache record; it is never rebound by numeric ID.

// 3. Instance state machine
// -------------------------
//
// Each distinct key follows one monotonic state machine. A state cannot move backward, and a
// failure cannot be converted into a successful node by another call site.
//
// - Absent: no key has been requested in this compilation closure.
// - Reserved: the declaration and complete closed tuple passed admission; one local FIR ID has
//   been allocated and the exact key is present in the lookup table. The body may not yet exist.
// - Queued: one work item for the reserved key is awaiting body construction. A key occurs at most
//   once in the queue, even if many edges refer to it.
// - Building: the worklist has removed the item and is constructing the substituted body. Recursive
//   exact-key requests resolve to this reserved node and add edges; they do not queue a duplicate.
// - Built: the complete body and outgoing selected-call edges have been constructed. This state is
//   provisional until graph closure and verification complete.
// - Verified: graph closure, exact edge resolution, type and body checks, and independent FIR
//   verification all succeed for the complete artifact closure.
// - Failed: an admission, resource, cancellation, body, edge, or verifier failure prevents success.
// - Cancelled: a cancellation request terminated the compilation before verified publication.
//
// Reservation MUST be atomic with respect to lookup and queue insertion. On one compilation thread,
// a single uninterrupted operation may suffice. In parallel construction, synchronization must
// ensure exactly one winner reserves a key and every other request observes its state. The winner
// owns one body build; no duplicate body may race to overwrite the node.
//
// A body builder may publish a provisional reference to a reserved ID inside its private staging
// graph. It may not expose that reference as verified or executable until the target reaches Built,
// every node is closed, and the whole required graph reaches Verified. Self-reference is legal only
// when source typing, control flow, effects, and target ABI independently permit the call.

// 4. Exact-key recursion and mutual cycles
// ----------------------------------------
//
// Exact-key recursion occurs when a body calls the same declaration with exactly the same closed
// ordered tuple. Reservation happens before body traversal. The recursive edge therefore targets
// the existing Reserved or Building node. It creates one node and one back edge, not an endlessly
// expanding chain. The function body's recursive semantics remain ordinary language semantics;
// compile-time key reuse does not prove termination of the program at runtime.
//
// Example:
//
//     func <T> repeatUntil(value: T, stop: bool): T {
//         if (stop) return value
//         return repeatUntil(value, true)
//     }
//
// For a root call `repeatUntil<int>(1, false)`, the graph has one `repeatUntil<int>` node and a
// self-edge. The node's key is complete before its body is scanned. If the source call instead
// produces a semantically different type argument, this exact-key rule no longer applies.
//
// Mutual recursion is handled by the same rule without a special recursion-name table. If A calls
// B with tuple X, B calls C with tuple Y, and C calls A with the original tuple X, the final edge
// targets the already reserved A node. The graph has one node per distinct key, regardless of how
// many source call sites close the cycle. A two-function cycle is not required to be a two-node
// graph: if arguments differ, each distinct tuple creates its own key.
//
// A recursive call must pass normal source overload resolution, candidate selection, constraints,
// argument mapping, and conversion rules. The graph builder cannot assume a self-call by name, skip
// the selected target, or synthesize a recursive edge from syntax alone. Failed overload selection
// produces no edge. An unsupported selected target fails after selection; the compiler cannot fall
// back to another overload based on whether its body is cheaper to specialize.
//
// When a cycle contains an instance that fails body substitution, verifier validation, or resource
// accounting, all nodes whose verified status depends on the failed edge are failed. A cycle does
// not weaken type checking or permit a missing return, effect, operation, or call target. No cyclic
// strongly connected component may be marked Verified while one of its required members remains
// Reserved, Queued, Building, Failed, or Cancelled.

// 5. Distinct closed arguments and type growth
// --------------------------------------------
//
// The declaration alone is not an instance identity. Each different complete tuple creates a
// different node, even when the generated native signatures happen to be ABI-equivalent or the body
// does not read one of the parameters. Examples include `f<int>`, `f<String>`, `f<int?>`,
// `f<[]int>`, and `f<(int, String)>`; these are different keys when each type is admitted.
//
// Type-growing recursion can create a sequence such as `grow<int>`, `grow<[]int>`,
// `grow<[][]int>`, and so on. The first instance does not make every later instance recursive reuse.
// Each next tuple is a distinct key. The graph is not assumed to terminate merely because one
// declaration name repeats or because every edge is recursive.
//
// Finite hard budgets bound work even for a source that continually constructs new arguments. The
// current FIR implementation stops at 4,096 distinct generic instances, 16,000,000 specialized
// source-body AST nodes, or 128 structural levels in an argument. It reports `SOL2403` on the
// applicable generic specialization resource failure and does not claim successful closure.
//
// The 128-level argument check measures the current supported structural type traversal in
// `SupportedArgument`; it is not a general maximum call-chain length. A long finite chain of
// different but shallow closed types is governed by the instance and body-node budgets. Conversely,
// one deeply nested argument may reach the type-depth limit with only a small number of instances.
// The implementation checks the relevant limit at the point where it has an exact count.
//
// Normative implementations may additionally enforce a maximum active instantiation depth of 128
// edges from a root, as described by the resource appendix. This graph-path maximum is distinct from
// the maximum structural nesting inside a type. Until that separate check exists, do not describe
// the implemented type-depth cap as an implemented recursion-depth cap.
//
// Resource limits are checked before allocating or publishing work beyond the configured maximum.
// The compiler cannot truncate later instances, substitute Object or `!`, discard edges, or emit a
// partially specialized artifact. A stable `SOL2403` diagnostic identifies the exhausted budget,
// observed count or attempted next unit, configured maximum, source origin, and bounded chain when
// available. Diagnostic text must not reveal absolute local paths or unstable type IDs.

// 6. Worklist and deterministic order
// -----------------------------------
//
// The worklist holds each newly reserved instance exactly once. The key table is authoritative for
// deduplication; the queue is only a scheduling structure. A repeated request MUST NOT append a
// second item or increment distinct-instance and body-node accounting again.
//
// The normative scheduler uses a FIFO queue for serial builds. It seeds roots in the artifact's
// canonical root order. When one body is processed, selected outgoing generic edges are enumerated
// in source evaluation order, with stable child order and a defined policy for compiler-generated
// edges. A newly discovered key is inserted immediately and enqueued at the tail. A later duplicate
// edge retains its own origin but does not enqueue work.
//
// Canonical root order is based on semantic declaration identity, not file-system enumeration,
// platform path separator, absolute path, directory scan order, hash-map order, or thread schedule.
// Canonical outgoing-edge order follows source evaluation order and then the source ordinal of any
// generated edge. Distinct edges with identical origins use a stable declaration/argument key
// tiebreaker. This ordering defines deterministic local IDs where local IDs are observable in a
// compiler dump; it is not itself persistent identity.
//
// A canonical scheduler MUST produce the same closed key set, graph edge set, per-key body result,
// diagnostics, and normalized FIR for the same semantic inputs even when source files are supplied
// in a different enumeration order or worker scheduling differs. If the language contract chooses
// to preserve source order as semantically observable for a diagnostic or side effect, the relevant
// semantic order is retained; incidental file discovery order is not promoted into language
// semantics.
//
// Parallel scheduling may build independent nodes concurrently, but it must commit results in a
// stable order or normalize them by canonical key before assigning externally visible order. A
// parallel worker may not race to decide the winner of an overload, mutate another key's
// substitution, or make a resource failure depend on thread interleaving. Budget accounting is
// deterministic: the resource unit and tie policy for competing requests are specified and stable.
//
// Current behavior is narrower. It uses a FIFO queue, but initial ordinary-function IDs and queue
// discovery order follow the current declaration/call traversal. Repeating an identical source
// input under a fixed runtime traversal is deterministic. Reordering files or bodies can alter local
// numeric IDs and discovery order; the current IDs are not canonical cross-input IDs. This is an
// implementation limitation, not permission to persist those IDs in a cache.

// 7. Reachability, closure, and artifact boundary
// -----------------------------------------------
//
// Graph closure begins from the root set for one artifact request. The scheduler repeatedly
// processes queued nodes until the queue is empty or a terminal failure/cancellation occurs. Empty
// queue alone is not proof of success: every reserved key must have one completed body, every
// selected edge must target an existing exact key, every target must be reachable from a root, and
// every required node must pass the independent verifier.
//
// A generic declaration that has no selected reachable call is not specialized just because it is
// present in a source file. A generic declaration cannot serve as the entry point under the current
// `Main.main([]String)` rule. An ordinary function that is included by the artifact root policy may
// contain a selected call that creates one or more generic nodes.
//
// Library closure must state whether ordinary non-exported source functions are roots, whether
// exported ordinary functions are roots, and how dead-code elimination changes reachability. A
// generic source declaration is not a stable exported symbol by this graph contract. Exporting a
// closed instantiation requires a separate artifact ABI rule and stable symbol naming contract.
//
// Cross-compilation containers cannot use an instance solely because another artifact used an
// equal display signature. They require a verified interface or a separately specified generic
// import artifact that carries declaration identity, full type identity, ABI compatibility, source
// dependency/version data, and verifier evidence. No such persistent instance interchange is
// implied by the current native subset.
//
// A binary or library commit is atomic with respect to graph validity. The compiler stages output,
// closes and verifies the graph, completes lowering and linking, then commits the artifact. On
// graph failure or cancellation it must not replace an older successful output with a partial file.
// Any staging files follow the established artifact cleanup and diagnostics policy.

// 8. Cancellation and transaction boundary
// -----------------------------------------
//
// Cancellation is a distinct terminal outcome, not a source diagnostic and not successful partial
// compilation. The generic graph builder checks cancellation before each root, each worklist
// dequeue, each body traversal, each substitution descent at the required polling interval, each
// constraint/projection operation that can be long, each verifier traversal, and before artifact
// publication. Long native or external operations require their own cancellation boundary where
// the host API permits one.
//
// Cancellation stops accepting new reservations as soon as the token is observed. Workers must
// stop cooperatively, release owned locks, and discard provisional state. If a native API cannot
// cancel an operation already in progress, the host waits for a safe boundary and still suppresses
// publication. The builder may leave immutable diagnostics for already observed errors according
// to the compiler's cancellation reporting policy, but it must not convert cancellation into
// `SOL2403` or a normal invalid-program result.
//
// A partially built graph is local staging state. It cannot be inserted into a verified cache,
// emitted as an executable, reported as a successful FIR dump, or reused by a later request as if
// its nodes had passed verification. A later compilation starts with a fresh closure or validates a
// complete immutable cache entry against the new semantic inputs.
//
// Cancellation must not corrupt a concurrently compiling independent artifact. Per-compilation
// queues, key tables, budgets, type ownership checks, diagnostics, and staging paths are isolated.
// Shared immutable source/type facts may outlive a cancelled closure; mutable graph state may not be
// shared without an explicit transactional ownership protocol.

// 9. `SOL2403` resource diagnostic
// --------------------------------
//
// `SOL2403` identifies a generic specialization resource boundary. It must be emitted for a
// specialization graph limit or specialized-body accounting limit that prevents closure, including
// a failed attempt to add one more distinct key than the 4,096-instance cap. Current exact wording
// may evolve, but code and meaning remain stable for tooling.
//
// A diagnostic should name the exhausted dimension in ordinary source terms: distinct instance
// count, structural type depth, specialized body node count, or a separately implemented graph
// depth/queue limit. It records the configured cap and the count that caused rejection, or a clear
// lower-bound value if traversal was cancelled before an exact total was available. It associates
// the primary location with the call that requests the rejected work; a fallback declaration span
// is used only if a precise call origin does not exist.
//
// The instantiation trace is bounded by a documented diagnostic limit. It contains a sequence of
// source declaration names and shortened, source-rendered type arguments, with call origins where
// practical. It must not expose numeric `ResolvedType.Id`, pointer values, process IDs, machine
// paths, credentials, cache directory names, or untrusted serialized fields without validation.
//
// One primary limit diagnostic must survive diagnostic truncation. Repeated requests for an
// already reserved exact key do not generate repeated limit errors. Once the graph closure has
// failed, dependent errors may be suppressed or summarized, but the compiler must not report a
// successful artifact or present one `SOL2403` per recursive back edge.
//
// `SOL2403` is separate from a `TypeSpecializationException` generated while inference or source
// type construction runs. A type interner limit may be translated by its owning source-analysis
// operation into the appropriate source-analysis diagnostic. The generic FIR builder's 4,096
// instance limit is not the interner's 100,000 additional-type limit. The 128 structural-type
// nesting limit is independently observed by the relevant operation; equal numeric values do not
// make their budgets interchangeable.

// 10. Separate resource domains
// ------------------------------
//
// `TypeSpecialization` owns a synchronized structural type interner extending one `TypeAnalysis`.
// Its default capacity is 100,000 additional interned type objects and its default structural
// rewrite depth is 128. These counters cover type construction/rewrite in that workspace. They do
// not count generic function bodies, graph nodes, machine code, or function instances.
//
// Source generic inference has its own candidate, constraint, structural matching, projection,
// graph-node, lambda fixed-point, and body-binding budgets. These budgets bound the effort required
// to decide source meaning. A candidate failure or deferred result is not evidence that a function
// instance exists. Inference may allocate types before FIR begins; those allocations do not consume
// the separate 4,096 generic-instance cap.
//
// Generic FIR specialization owns at least three independent initial counters:
//
// 1. At most 4,096 distinct generic function keys for one compilation closure.
// 2. At most 16,000,000 source-body AST nodes accounted across those distinct instances.
// 3. At most 128 structural levels in an admitted type argument under the current traversal.
//
// A normative complete implementation may add an active graph path limit of 128 edges. That is a
// fourth counter and does not replace structural type depth. Every counter is charged to its owning
// phase, has a documented unit, resets at its documented compilation/workspace boundary, and has an
// independently testable diagnostic. A configuration lowering one budget cannot silently raise or
// reset another.
//
// The same source body is charged once per distinct key even when multiple call edges share it.
// Repeated edges do not multiply its charge. A generic `f<int>` and `f<String>` charge separate
// body traversals. If implementation sharing reuses internal immutable syntax data, semantic body
// accounting still charges each logical specialization to prevent storage-sharing tricks from
// making distinct code generation work unbounded.

// 11. Conformance invariants
// --------------------------
//
// A conforming graph implementation demonstrates all of the following in inspectable FIR and
// source diagnostics, not only by producing a native executable:
//
// - One key reservation for repeated exact calls from multiple roots.
// - One self-edge for direct same-key recursion and one closed component for mutual same-key cycles.
// - Separate nodes for every difference in declaration owner or ordered closed argument tuple.
// - A distinct node for each accepted type-growing recursive tuple until a specified hard limit.
// - Stable closed-key sets and normalized output under source-file enumeration changes.
// - Collision-safe equality even when test hash functions deliberately collide.
// - No duplicate queue item, no open key, and no target ID that refers to another key.
// - Independent enforcement of instance count, body-node count, type depth, and type-interner cap.
// - A bounded `SOL2403` diagnostic with a useful requesting-call origin on exhaustion.
// - Prompt cancellation without a Verified node, successful artifact, or cache publication.
// - Atomic failure when one edge or node in a recursive component fails verification.
// - Rejection of stale numeric IDs and foreign type-analysis ownership.
// - Explicit distinction between a planned active-path budget and the implemented type-depth cap.
//
// Positive graph closure means every reachable selected generic call has exactly one target key,
// the target is closed and Built, all required verification succeeds, and every node is reachable
// from an authorized root. Negative closure means an unresolved, unsupported, over-budget,
// cancelled, orphaned, or verifier-rejected target prevents the artifact from being committed.
// There is no success state for an incomplete worklist.

// 12. Worked graph traces
// -----------------------
//
// The following traces describe graph semantics independently of current native body support. They
// are compact review examples; their `node` names denote canonical keys, not the temporary integer
// IDs printed by a local FIR dump.
//
// A. Repeated exact call from two roots
//
//     func <T> identity(value: T): T = value
//     func first(): int = identity(1)
//     func second(): int = identity(2)
//
// Roots are `first` and `second`. The first edge reserves `identity<int>` and queues one body. The
// second edge looks up the same key and adds a second origin-bearing edge. The graph has one
// identity node, two call edges, and two root-to-node paths. Body-node accounting charges the body
// once. Current exact-key reuse supports this key behavior within its native admission surface.
//
// B. Same function, distinct concrete arguments
//
//     func <T> identity(value: T): T = value
//     func roots(): () { identity(1); identity("one") }
//
// The key set contains `identity<int>` and `identity<String>`. Reusing one source declaration does
// not merge the instances. Each body has its own closed signature and body accounting. Whether both
// instances lower is decided by current supported-type and body verification rules.
//
// C. Direct self recursion
//
//     func <T> descend(value: T, again: bool): T =
//         if (again) descend(value, false) else value
//
// Processing `descend<int>` inserts the node before examining its expression. The recursive call
// creates an edge to that reserved key. When the work item completes, the edge target is built; it
// does not create another body. Runtime termination still depends on `again` and ordinary execution.
//
// D. Mutual recursion
//
//     func <T> even(value: T, flag: bool): T = odd(value, !flag)
//     func <U> odd(value: U, flag: bool): U = even(value, !flag)
//
// Starting at `even<int>` reserves `even<int>`, then discovers `odd<int>`, then `odd<int>` discovers
// the already reserved `even<int>`. The closed component has two nodes and two cross edges. `T` and
// `U` are distinct declaration-owned parameters; the edges map each to the concrete `int` identity.
//
// E. One declaration, expanding argument
//
//     func <T> wrap(value: T, more: bool): () {
//         if (more) wrap([value], false)
//     }
//
// If array construction and source inference admit the recursive argument, processing discovers
// `wrap<int>`, `wrap<[]int>`, `wrap<[][]int>`, and subsequent distinct keys. Each tuple consumes one
// instance and its body charge. The process terminates with a resource failure rather than silently
// reusing `wrap<int>`. An implementation may stop earlier at a separately enforced type-depth
// boundary; the diagnostic reports the budget that actually rejected the next unit.
//
// F. A longer shallow chain
//
// A source or generated call chain that repeatedly alternates among a finite set of primitive types
// can have many graph edges without deeply nested types. The 128-level structural type check does
// not count those edges. The 4,096 instance cap or a separately implemented active graph depth cap
// governs the chain, and the diagnostic must name that actual dimension.
//
// G. Unused parameter still distinguishes keys
//
//     func <A, B> keep(first: A, ignored: B): A = first
//
// The calls `keep<int, String>(1, "x")` and `keep<int, byte>(1, 2)` have different complete tuples.
// The fact that the returned value only refers to `A` does not erase `B` from the function's source
// type, constraints, reflection metadata, or instance key.
//
// H. Reversed named arguments preserve semantic tuple order
//
//     func <A, B> pair(first: A, second: B): () = ()
//     pair(second = "s", first = 1)
//
// The instance tuple is `[int, String]`, because the key follows declaration order. The edge keeps
// the original expression evaluation order and its formal-argument map. Reversing named source
// tokens without changing their formal associations does not reverse the key tuple.
//
// These traces must remain useful when local FIR numbers change. A graph-dump test compares semantic
// keys and origins first, and treats an integer ID as a reference within one dump only. Cross-build
// equivalence compares normalized graph data rather than accidental traversal IDs.
