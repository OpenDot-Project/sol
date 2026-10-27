// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative identity, deterministic-worklist, and cache-invalidation contract.
module source.spec.generic_fir_cache_and_determinism

// Generic FIR cache identity and deterministic construction
// =========================================================
//
// This contract specifies compilation-local generic instance reuse and the conditions required
// before any future incremental or persistent cache may reuse a result. The current implementation
// has an in-memory worklist and interning tables only. It has no persistent specialization cache,
// no cross-process identity, and no parallel FIR specialization scheduler.
//
// Normative terms MUST, MUST NOT, SHOULD, SHOULD NOT, and MAY have their ordinary standards
// meaning. Current implementation notes describe a bounded development slice. They do not weaken
// the normative requirements or imply that a future cache already exists.

// 1. Scope and phase ownership
// ----------------------------
//
// Generic compilation has several distinct reusable results:
//
// 1. parsed source snapshots and lossless syntax data;
// 2. declaration, import, and type identity tables;
// 3. source-call candidate analysis and selected argument mapping;
// 4. a closed generic substitution and specialization key;
// 5. substituted and independently verified FIR;
// 6. target-specific LLVM modules, object files, bitcode, and linked artifacts;
// 7. optional package/container bytes and their manifest.
//
// A cache entry MUST name the phase whose result it stores. A PSI cache hit is not a body-analysis
// proof. A type-substitution hit is not a selected overload. A body-analysis hit is not verified
// FIR. A verified FIR hit is not a target-specific object file. A container digest is not a cache
// key for source semantics. No later phase may infer the validity of an earlier phase solely from
// the presence of a later-phase artifact.
//
// The current `FirBuilder` uses compilation-local dictionaries and a FIFO pending-specialization
// queue. The current `TypeSpecialization` owns a synchronized in-memory type interner extending one
// source type analysis. These are not persistent caches. The builder does not share generic
// specialization entries between processes or compilations.

// 2. Identity terms
// -----------------
//
// A source declaration identity identifies one declaration in one immutable source-analysis
// workspace. A simple function name is not an identity. The same spelling can occur in different
// modules, overload groups, partial owners, or source snapshots.
//
// A generic parameter identity consists of its declaration owner and its ordinal within that
// owner. A displayed parameter name such as `T` is not sufficient: a class's `T`, a method's `T`,
// and a nested function's `T` are distinct identities even if they have the same spelling.
//
// A resolved type identity is owned by exactly one `TypeSpecialization` workspace. Its semantic
// structure includes its kind, declaration identity or parameter owner/ordinal, ordered type
// arguments, nullable wrapper, function parameter modes, and effect set. Source names are display
// information and cannot replace those structured fields.
//
// A generic specialization key is the pair:
//
// - the canonical source declaration identity; and
// - the complete, declaration-ordered tuple of closed canonical type identities.
//
// A key has no meaning outside the workspace that owns its declaration and type identities. A
// numeric type ID is a workspace-local index. It is not a process-stable fingerprint, package ID,
// LLVM type identity, C symbol, or persistent cache key.

// 3. Closed key requirements
// --------------------------
//
// Before an entry is inserted into the current instance table, the compiler MUST establish all of
// the following:
//
// 1. overload selection has completed and has exactly one selected source declaration;
// 2. the selected declaration belongs to the active source workspace;
// 3. the supplied type-argument count equals the declaration's generic parameter count;
// 4. each argument belongs to the active type workspace;
// 5. every argument is closed and contains no unbound parameter owned by any declaration;
// 6. all declared constraints are satisfied for the complete ordered tuple;
// 7. nullable, ref-like, function-effect, array, tuple, and nominal structure has not been erased;
// 8. any target-specific native admission requirement is checked before verified FIR is minted.
//
// A partially solved argument list MUST NOT be used as a provisional key. A candidate that remains
// deferred, ambiguous, or rejected MUST NOT publish an instance. A losing overload MUST NOT leave
// behind a cache entry visible to the winner or to a later call site.
//
// Key construction MUST be injective over declaration identity and the ordered argument tuple. It
// MUST NOT concatenate ambiguous display strings, depend on current culture, rely on dictionary
// enumeration order, use a truncated hash without collision resolution, or normalize away a
// semantic wrapper.
//
// Hash tables MAY accelerate lookup, but key equality MUST compare the complete canonical identity.
// A hash collision is a normal lookup condition; it cannot cause reuse of another type tuple.

// 4. Type-key shape
// -----------------
//
// A future portable type fingerprint MUST encode structural fields with unambiguous framing and
// explicit versioning. At minimum it MUST distinguish:
//
// - primitive identity by the language type name and primitive-set version;
// - nominal identity by defining module identity, declaration identity, and generic arity;
// - nominal arguments in declaration order;
// - nullable from non-null values at each nesting position;
// - dynamic arrays from their element types and from fixed arrays or collection types;
// - tuple arity and each ordered element type;
// - function parameter types, passing modes, result type, and normalized checked-effect set;
// - generic parameter owner and ordinal when describing an open declaration signature;
// - ref-like classification and any future layout-affecting type modifier.
//
// The current native generic subset rejects open arguments and most of these shapes. The
// requirements above describe identity, not permission to emit all of them as native values.
//
// Canonical effect sets MUST have one documented order or a set encoding independent of source
// order. Function parameter lists and generic argument tuples remain ordered sequences. Nullable
// normalization may make repeated nullable wrappers equivalent only where source type analysis
// already defines that equivalence; the cache cannot invent additional normalization.
//
// Canonicalization MUST be performed by the source type system. A cache MUST NOT reconstruct type
// identity from a `Name` string, formatted diagnostic, LLVM printer output, reflection display
// name, or JSON field ordering.

// 5. In-memory interning
// ---------------------
//
// Within one compilation workspace, the same declaration identity plus the same ordered closed
// type tuple MUST map to one logical generic instance. Reusing the instance prevents duplicate FIR
// bodies and closes exact-key recursion. It does not authorize reuse across different source
// workspaces.
//
// The table MUST publish an entry in a state that can be resolved by recursive requests before the
// body is expanded, or use an equivalent explicit state machine. A state machine SHOULD distinguish
// queued, building, verified, failed, and cancelled instances. A queued/building entry cannot be
// returned to code generation as verified.
//
// A lookup hit MUST still be associated with the selected source binding at the call site. The
// verifier MUST confirm that the call's selected source declaration and ordered arguments equal the
// stored instance identity. It MUST NOT trust a target ID because it happens to exist in the table.
//
// Entries MUST be scoped to one compilation and one immutable source/type workspace. Disposal of
// that workspace invalidates all raw declaration references and type IDs. A live in-memory key
// cannot be retained in a global static table after its owning source snapshots are released.
//
// The current builder interns a new key before appending it to a FIFO work queue. It assigns a dense
// function ID at insertion and later serializes functions by that ID. This is a bounded
// compilation-local mechanism; it is not a thread-safe, persistent, or incremental cache contract.

// 6. Worklist closure and recursive graphs
// ----------------------------------------
//
// A direct call to a closed generic declaration creates a graph edge from its caller to its
// selected instance. A specialized body may produce further edges after applying its caller
// substitution to the callee's selected type arguments. The compiler MUST continue until every
// reachable instance is either completely built and verified or the compilation has failed.
//
// An exact recursive key is legal graph recursion. For example, `walk<int>` calling `walk<int>`
// must resolve to its existing node instead of enqueueing a duplicate body forever. Mutual
// recursion among a finite set of exact keys is also finite.
//
// A sequence of distinct keys is not exact recursion. `grow<T>` calling `grow<Box<T>>` creates a
// distinct instance at every step unless a defined substitution reaches a repeated canonical key.
// A depth or instance budget MUST stop unbounded growth and report a resource outcome; the compiler
// MUST NOT merge merely similar keys to force termination.
//
// If closure fails because of a limit, cancellation, unsupported shape, verifier error, or lowering
// error, no successful verified program may contain a reachable call whose target is absent or
// still pending. Partial tables MUST remain private to the failed compilation.

// 7. Deterministic scheduling
// ---------------------------
//
// For the same compiler build, target configuration, explicit source snapshot set, and options,
// repeated compilation MUST select the same source declarations, type tuples, diagnostic ordering,
// specialization graph, function ordering, and output bytes, except for fields explicitly
// documented as non-reproducible such as a timestamp in an optional external debug container.
//
// Determinism MUST NOT depend on:
//
// - hash-table bucket order or randomized process hash seeds;
// - iteration order of a `Dictionary`, `HashSet`, or concurrent collection;
// - thread scheduling, task completion order, or machine core count;
// - locale, current culture, timezone, environment variable order, or filesystem enumeration order;
// - when a progress event is emitted or when a cancellation token is observed;
// - memory addresses, object hash codes, pointer values, or process-local declaration IDs;
// - the order in which equal source facts happen to be inserted into a solver.
//
// Current scheduling is single-threaded and FIFO after initial ordinary functions are traversed in
// source order. This is deterministic for a fixed input ordering. It does not currently promise
// artifact byte identity after callers reorder an otherwise equivalent input file list. A future
// implementation MUST either make such order semantically relevant and document it, or canonicalize
// the source set before assigning externally visible order.
//
// Sorting is not universally correct: source declaration order can affect diagnostics and member
// merge rules if the language contract says so. Canonical scheduling MUST preserve language-defined
// source-order semantics while removing accidental host-container order.

// 8. Function numbering and symbol names
// --------------------------------------
//
// Function IDs in a `FirProgram` are dense indices for that one program. They are suitable for
// `FirCall.Target` and schema-local JSON links. They MUST NOT be persisted as a specialization key
// or reused across another compilation.
//
// Native symbol names MUST be generated from an explicit ABI-mangling contract. A name formed by
// appending a pretty-printed type to a source name is not an identity rule. Symbol uniqueness MUST
// be collision-checked after target-specific encoding. The symbol scheme MUST separate private
// monomorphic helpers from public C or Sol exports.
//
// Reordering the work queue may renumber local FIR functions without changing semantics. If a
// versioned external format includes function order, the serializer MUST either canonicalize it or
// explicitly declare IDs document-local and reject cross-document references.

// 9. Incremental compilation cache layers
// ---------------------------------------
//
// A future incremental cache SHOULD store phase-specific entries rather than one opaque
// "generic compilation" blob. The following inputs belong to distinct invalidation domains:
//
// | Cached result | Required semantic inputs |
// |---|---|
// | Parsed PSI/AST | Source bytes, source decoding policy, parser and grammar version, language edition |
// | Declaration graph | All relevant source declarations, module/import graph, visibility and partial-merge rules |
// | Selected call | Caller body, candidate declarations, argument mapping, expected type, inference and ranking version |
// | Substitution | Source declaration identity, ordered closed arguments, constraint declarations and type rules |
// | Specialized FIR | Substitution plus generic body, semantic analysis version, FIR schema and verifier version |
// | LLVM module/object | Verified FIR, target triple/ABI, LLVM 23.1.3 build/configuration, pass pipeline and codegen options |
// | Linked artifact | Objects, LLD version/configuration, SolRT ABI/layout/linkage, system libraries and artifact kind |
// | Sol container | Manifest schema, package metadata, payload bytes, digests, module/export graph and target compatibility |
//
// A downstream cache hit MUST be invalidated when any dependency of the cached result changes. A
// hash of the generic arguments alone is never enough to reuse FIR or machine code.

// 10. Minimum generic-FIR invalidation key
// ----------------------------------------
//
// A future specialized-FIR cache entry MUST depend on at least:
//
// - the canonical generic declaration identity and its owning module identity;
// - the full generic body source content and any referenced declaration whose binding affects it;
// - the ordered closed type-argument fingerprints and generic parameter ordinals;
// - every constraint, base projection, interface relation, and conversion rule consulted;
// - selected source overload identity and mapped argument/default/ref/out facts at each call;
// - all default values and inherited-default owners used by a call;
// - nullability, array/tuple/function/effect/ref-like identities present in the substituted body;
// - source language edition and semantic-analysis implementation version;
// - FIR node schema, origin/source-map contract, verifier rule version, and native admission policy;
// - analysis configuration affecting imports, platform declarations, or runtime built-ins.
//
// If a generic helper calls another generic helper, the transitive instance graph is part of the
// cache result. An entry MUST either include a dependency manifest for every reachable declaration
// and instance or be rebuilt whenever any reachable dependency changes.
//
// Rechecking a cache entry MUST validate its dependency digest and graph closure before granting a
// verified capability. A digest proves equality with bytes named by the digest; it does not prove
// that the producer was trusted or that a verifier rule still has the same meaning.

// 11. Native code invalidation
// ----------------------------
//
// A native object or bitcode cache MUST add all target and backend inputs not already captured by
// verified FIR. It MUST include target architecture, object format, ABI, data layout, calling
// convention, optimization/codegen pipeline, LLVM 23.1.3 exact build identity and relevant options.
//
// A linked executable or library MUST additionally include the LLD.COFF/LLD.Common version,
// runtime library ABI and layout version, dynamic or static SolRT selection, CRT mode, system
// library closure, artifact selection (`bin`, `cdylib`, `staticlib`, or `solib`), export set, and
// companion output names.
//
// ORC and AOT MUST NOT share an artifact cache key merely because both originated from the same
// source FIR. ORC's guest process/runtime transfer and AOT's link closure are distinct outputs.
// Process isolation between host GC and guest GC is an execution boundary, not a cache identity.
//
// A version string such as "LLVM 23" is not sufficiently precise when the pinned project version is
// LLVM 23.1.3. A cache MUST key the actual toolchain identity and configuration that produced its
// object bytes.

// 12. Generic constraint changes
// -------------------------------
//
// If a constraint declaration, inherited interface set, base type, or conversion relation changes,
// every specialization whose applicability or body binding depended on that relation MUST be
// reconsidered. The generic body source need not change for its selected set of types to change.
//
// Cache dependencies MUST distinguish:
//
// - a tuple proven to satisfy all constraints;
// - a tuple rejected by a known constraint;
// - a tuple deferred because required source facts are missing;
// - a tuple that was never a candidate because overload selection chose another declaration.
//
// A negative result may be cached only with the same dependency completeness as a positive result.
// Adding a new overload, changing visibility, or importing another module can turn a prior negative
// candidate set into a selected call. Negative caching therefore includes the complete relevant
// declaration set and lookup environment.

// 13. Source movement and origin data
// -----------------------------------
//
// A source span is diagnostic provenance, not generic declaration identity. Moving an unchanged
// function to another file may preserve its source semantics while changing module, visibility,
// partial-merge, import, or debug-origin behavior. A cache MUST know which of those facts are
// semantic dependencies.
//
// If debug information or FIR JSON contains source offsets, a cache that reuses those outputs MUST
// either include exact source snapshot identity or regenerate origin tables from a validated
// source map. It MUST NOT reuse offsets against different bytes and silently point diagnostics at
// unrelated source text.
//
// Normalized paths may be part of a reproducible build identity when the output includes paths, but
// absolute machine-local paths SHOULD be stripped from public symbols and package metadata. Path
// normalization must follow the Windows case and separator rules promised by the toolchain.

// 14. Cancellation, failure, and cache poisoning
// ----------------------------------------------
//
// Cancellation during candidate analysis, key construction, type substitution, queue expansion,
// verification, LLVM construction, or linking MUST NOT publish a partial successful cache entry.
// A cancelled entry is distinct from a semantically invalid entry and from a resource-limit result.
//
// Cache writes MUST be transactional. The producer first writes to an isolated staging location,
// validates all required metadata and payload digests, then atomically publishes the complete entry
// or commits it with an equivalent durable protocol. Failed verification or output must leave no
// entry that a later compiler could trust as complete.
//
// Readers MUST validate lengths, schema, ownership, dependencies, target compatibility, and hashes
// before deserializing untrusted cached type or FIR data into privileged compiler objects. A
// serialized `verified: true` flag is never a verifier capability.
//
// Cache directory ownership and concurrent writers MUST be handled without treating a partially
// written directory as a hit. Locking, leases, atomic rename, or content-addressed publication may
// be used, but the observable outcome is a complete verified entry or a cache miss.

// 15. Diagnostic determinism
// --------------------------
//
// A cache hit and a fresh build MUST report equivalent diagnostics for the same inputs. A cached
// message MUST be remapped to the current source snapshot only after source identity and span
// mapping are checked.
//
// Multiple errors discovered during unordered traversal MUST be sorted by a stable tuple such as
// normalized source identity, byte offset, phase order, diagnostic code, and deterministic local
// ordinal. Sorting MUST preserve causal note order and language-defined candidate ordering.
//
// Resource diagnostics MUST name the phase and bound crossed. A resource error MUST NOT be cached as
// an invalid type, overload failure, or verifier failure. A cancellation request MUST NOT become a
// stable diagnostic that claims the input program is wrong.
//
// Diagnostic source origins and instance context SHOULD identify both the generic body declaration
// and the call chain that requested it. The order of call-chain notes must not depend on hash-set
// iteration or work stealing.

// 16. Concurrency and future parallel scheduling
// ----------------------------------------------
//
// The current FIR builder expands its queue on one thread. The current type interner protects its
// mutable table with a lock. This does not imply concurrent body construction, safe sharing of
// `AstFunction` or `FirBuilder`, deterministic ID assignment under parallel calls, or a
// parallel-specialization feature.
//
// A future parallel scheduler MUST use a single-flight state per canonical key or an equivalent
// protocol so that concurrent requests do not produce duplicate visible instances. It MUST avoid
// deadlock for mutually recursive keys and distinguish an in-progress cycle edge from an
// incomplete/failed target.
//
// Parallel completion order MUST NOT define externally visible function IDs, diagnostics, symbol
// names, archive order, or output bytes. IDs can be allocated in a deterministic discovery pass or
// remapped after closure using a stable canonical order. Cancellation and one worker's failure must
// cancel or safely drain dependents without publishing partial success.
//
// Lock acquisition order and work-stealing decisions MUST NOT affect semantic selection. Parallel
// candidate exploration must isolate candidate-local substitutions and diagnostics until one source
// winner is committed.

// 17. Cross-process and cross-workspace rejection
// -----------------------------------------------
//
// A `ResolvedType` or `AstFunction` reference from another workspace MUST be rejected even when its
// numeric ID and display name match a local item. A serialized integer ID is not an authority token.
//
// A `.solib` container's module ID, manifest digest, or producer-local type index cannot be used as
// a consumer's generic specialization key. Cross-container generic instantiation requires a
// versioned canonical identity and verified dependency contract. The current container format does
// not define that contract.
//
// An in-memory cache entry MUST be discarded when its owner workspace is disposed. A persistent
// cache entry MUST be parsed into newly owned identities and reverified. Rehydration cannot retain
// references to object instances from another process.

// 18. Resource accounting
// -----------------------
//
// Every key lookup, dependency traversal, and materialized body consumes bounded compiler work.
// Limits must apply to distinct closed keys, total copied specialized body nodes, structural type
// depth, dependency graph traversal, cache payload size, and waiting time for a competing build.
//
// Reusing an exact key does not consume a second body-node budget. Adding a new distinct key does.
// A failed duplicate request MUST NOT increment committed success counts twice. The current builder
// enforces 4,096 specialized function instances, a total 16,000,000 source-AST-node budget across
// copied specialized bodies, and 128 structural type levels. It has no persistent-cache byte
// budget or concurrent waiter policy because those features are not implemented.
//
// A future cache MUST distinguish bytes read from bytes trusted. Size checks run before large
// allocation or decompression, and a cache hit does not waive compilation cancellation.

// 19. Reproducible package order
// ------------------------------
//
// If generic instances are serialized into a future package, the manifest MUST contain a stable
// relation from each payload to a versioned declaration/type identity. Archive entry ordering,
// timestamps, compression options, generated symbols, and payload digests MUST follow the package
// reproducibility contract.
//
// Producers MUST NOT serialize dictionary enumeration order. Consumers MUST reject duplicate
// canonical keys with differing payloads, missing transitive dependencies, unsupported schema
// versions, and incompatible target/runtime identities.
//
// The current `.solib` schema carries closed nongeneric subset metadata and does not define external
// generic instantiation. This section is a future contract; it does not add a current payload type.

// 20. Conformance and review requirements
// ---------------------------------------
//
// A conforming implementation MUST include evidence for:
//
// - exact-key reuse at repeated call sites and recursive self/mutual calls;
// - distinction for argument permutations, nullable wrappers, owners, and function effects;
// - rejection of foreign workspace identities and open arguments;
// - dependency invalidation when a body, constraint, overload, import, target, ABI, runtime, or
//   schema changes;
// - stable diagnostics and artifact order across repeated identical builds;
// - equivalent results when worker completion order changes, once parallel specialization exists;
// - cancellation and resource exhaustion without publishing a partial cache entry;
// - hash collision handling by full canonical equality;
// - schema downgrade or toolchain change causing a miss rather than unsafe reuse;
// - source-origin remapping that never applies old byte spans to new source bytes.
//
// Fixture declarations and outcome annotations are in
// [GenericFirCacheAndDeterminism.sol examples](examples/semantics/GenericFirCacheAndDeterminism.sol).
// The normative artifact, JSON, and recursion contracts remain separate because each owns a
// distinct phase boundary.

// 21. Completion status
// ---------------------
//
// The current code implements compilation-local instance interning and a FIFO work queue, with
// bounded expansion and same-workspace type ownership. It does not implement persistent generic
// FIR caching, incremental invalidation, cache deserialization, parallel instance scheduling, or a
// stable cross-process specialization fingerprint.
//
// This specification MUST NOT be summarized as claiming those future mechanisms are present. A
// cache feature is complete only when its versioned key, dependency graph, validation rules,
// cancellation and transactional publication are implemented and qualified independently from
// the existing per-compilation specialization path.
