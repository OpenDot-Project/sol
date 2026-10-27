// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative example matrix; cache and scheduling outcomes are not all implemented.
module spec.examples.generic_fir_cache_and_determinism

// Generic identity and deterministic cache examples
// =================================================
//
// This file records expected identity, reuse, invalidation, and failure outcomes. An `expect`
// comment is a specification assertion, not a result from a completed test run. The compiler
// currently uses a bounded in-memory queue for one build and has no persistent generic FIR cache.
//
// Status vocabulary:
// - `Source` describes source binding and generic candidate selection.
// - `Key` describes the canonical declaration plus ordered closed type tuple.
// - `FIR` describes one compilation-local function instance or a required rejection.
// - `Cache` describes only the currently existing per-compilation instance table unless marked
//   `Future`.
// - `Artifact` describes the target output boundary, not generic source selection.

func <T> identity(value: T): T = value

func <A, B> first(left: A, right: B): A = left

func <A, B> second(left: A, right: B): B = right

func <T> relay(value: T): T = identity<T>(value)

func <T> relayTwice(value: T): T {
    val once = identity<T>(value)
    return identity<T>(once)
}

func intRoot(): int = identity<int>(1)

func stringRoot(value: String): String = identity<String>(value)

func <T> recursiveExact(value: T, remaining: int): T {
    if (remaining == 0) { return value }
    return recursiveExact<T>(value, remaining - 1)
}

func <T> mutualLeft(value: T, remaining: int): T {
    if (remaining == 0) { return value }
    return mutualRight<T>(value, remaining - 1)
}

func <T> mutualRight(value: T, remaining: int): T {
    if (remaining == 0) { return value }
    return mutualLeft<T>(value, remaining - 1)
}

// 1. Source names are not declaration identities.

// In separate source files these declarations may both be named sameName. The fixture uses suffixes
// so it remains one valid logical module; the expect record describes the cross-module identity case.
// Scenario group: alpha
func <T> sameNameAlpha(value: T): T = value

// Scenario group: beta
func <T> sameNameBeta(value: T): T = value

// expect: alpha.sameName<String> and beta.sameName<String> are distinct source declaration keys.
// expect: equal simple names and equal type tuples do not merge FIR declarations.

// 2. Overloads have separate declaration identities even when their generic names match.

// Scenario group: overload_identity
func <T> choose(value: T): String = "one"
func <T> choose(value: T, label: String): String = label

// expect: choose(value) and choose(value,label) select different source declarations.
// expect: arity and declaration identity are compared before considering type tuple equality.

// 3. Ordered generic arguments distinguish permuted specializations.

// Scenario group: ordered_arguments
func <A, B> describe(first: A, second: B): String = "ordered"

func orderedStringInt(left: String, right: int): String = describe<String, int>(left, right)
func orderedIntString(left: int, right: String): String = describe<int, String>(left, right)

// expect: describe<String,int> and describe<int,String> are distinct keys.
// expect: source parameter order, not call-site named-argument order, defines tuple order.

// 4. Repeated exact keys share one compilation-local instance.

// Scenario group: repeated_sites
func firstUse(value: int): int = identity<int>(value)
func secondUse(value: int): int = identity<int>(value)

// expect: Source: both calls select the same declaration and [int].
// expect: FIR: one identity<int> body is interned for this compilation.
// expect: FunctionId: both FirCall.Target values refer to the same local function index.

// 5. Repeated calls do not allow the function's display name to become a cache key.

// Scenario group: same_display
func <T> echo(value: T): T = value
func echoText(value: String): String = value

func genericEchoText(value: String): String = echo<String>(value)
func ordinaryEchoText(value: String): String = echoText(value)

// expect: Key: generic echo<String> includes its generic declaration identity and argument tuple.
// expect: The nongeneric echoText declaration does not collide with generic echo<String>.

// 6. Nullable wrappers are part of type identity.

// Scenario group: nullable_keys
func <T> nullableEcho(value: T?): T? = value

func echoNonNull(value: String): String = identity<String>(value)
func echoNullable(value: String?): String? = nullableEcho<String>(value)

// expect: nullableEcho<String> is a closed type tuple [String]; its substituted signature is nullable.
// expect: nullableEcho<String?> and nullableEcho<String> are distinct tuples if both are admitted.
// expect: Nullable(String) and String are never merged by a key formatter.
// expect: FIR: Nullable native representation is outside the current admitted generic ABI.

// 7. Array and nested generic constructors remain structurally distinguishable.

// Scenario group: nested_shape_keys
func <T> arrayEcho(value: []T): []T = value
func <T> nestedEcho(value: ([]T, T?)): ([]T, T?) = value

func stringArray(value: []String): []String = arrayEcho<String>(value)
func nestedString(value: ([]String, String?)): ([]String, String?) =
    nestedEcho<String>(value)

// expect: []String and ([]String,String?) have different structural fingerprints.
// expect: Array(String) and Nullable(Array(String)) are separate shapes.
// expect: Source substitution is structural; names such as "Array<String?>" are display-only.

// 8. Owner and ordinal, not the parameter spelling, define open parameter identities.

// Scenario group: owner_ordinal
class Owner<T> {
    func <T> method(value: T): T = value
}

func <T> topLevel(value: T): T = value

// expect: Owner.T, Owner.method.T, and topLevel.T are different parameter identities.
// expect: A future owner/method key includes owner identity and both declaration ordinals.
// expect: Current native FIR rejects generic owners and generic members before instance publication.

// 9. Function-value shape contains modes and effects.

// Scenario group: function_shape
func <T> idFunction(value: T): () -> T = { value }

// expect: Function parameter modes, result, and checked effects participate in the type shape.
// expect: A function-value key cannot be formed by serializing only a function's printed result.
// expect: Current native generic FIR rejects function-value representations.

// 10. Exact recursion closes on one key.

// Scenario group: exact_recursion
func <T> recurse(value: T, n: int): T {
    if (n == 0) { return value }
    return recurse<T>(value, n - 1)
}

func recursiveRoot(value: String): String = recurse<String>(value, 4)

// expect: Queue: recurse<String> is inserted once; its recursive edge resolves to that exact key.
// expect: Graph: one node with a self-edge; the verifier still checks the edge and target signature.
// expect: Recursion itself does not consume an unbounded new-instance budget.

// 11. Mutual recursion over a finite set of exact keys closes.

// Scenario group: mutual_recursion
func <T> left(value: T, n: int): T {
    if (n == 0) { return value }
    return right<T>(value, n - 1)
}

func <T> right(value: T, n: int): T {
    if (n == 0) { return value }
    return left<T>(value, n - 1)
}

func mutualRoot(value: int): int = left<int>(value, 10)

// expect: Queue: left<int> enqueues right<int>; right<int> points back to left<int>.
// expect: Instance count is two, not one per recursive edge.

// 12. Distinct growing keys are not collapsed into exact recursion.

// Scenario group: expanding_recursion
class Box<T>(val value: T)

}

func <T> grow(value: T): () {
    grow<Box<T>>(Box<T>(value))
}

// expect: Keys form T, Box<T>, Box<Box<T>>, and so on; they are structurally distinct.
// expect: This growth is terminated by a bounded-resource outcome, not key approximation.
// expect: Current native support rejects Box<T> before this body can produce native FIR.

// 13. Caller substitution composes before a nested callee key is formed.

// Scenario group: composed_substitution
func <T> outer(value: T): T = inner<T>(value)
func <U> inner(value: U): U = identity<U>(value)

func composedRoot(value: String): String = outer<String>(value)

// expect: outer<String> substitutes its own T; the nested request becomes inner<String>.
// expect: inner<String> then requests identity<String>, not identity<outer.T>.
// expect: A still-open parameter at the nested boundary remains unresolved and is not cached.

// 14. Constraint proof and selected declaration participate in invalidation.

// Scenario group: constraint_change
interface Marker {
    func marker(): String
}

class Marked: Object, Marker {
    override func marker(): String = "marked"
}

func <T: Marker> constrained(value: T): String = value.marker()

func constraintRoot(value: Marked): String = constrained<Marked>(value)

// expect: A future FIR cache depends on Marker and the selected member binding, not only this body.
// expect: Removing Marked's implementation or changing member resolution invalidates the cached result.
// expect: Current FIR rejects member lookup whose proof depends on the generic parameter.

// 15. Overload-set changes can invalidate a selected source call.

// Scenario group: overload_invalidation
func <T> select(value: T): String = "generic"
func chooseText(value: String): String = "text"

func genericWinner(value: String): String = select<String>(value)

// expect: Adding/removing a competing applicable overload changes call binding dependencies.
// expect: A body specialization cache cannot reuse the old call target without re-resolution.

// 16. Source argument mapping is part of the selected call dependency.

// Scenario group: mapping_invalidation
func <A, B> combine(left: A, right: B): String = "combined"

func mapped(value: String, count: int): String = combine<String, int>(right = count, left = value)

// expect: Named source order affects evaluation order but declaration order determines type tuple.
// expect: A change to parameter names/defaults/vararg mapping invalidates the caller's selected call.

// 17. Default-value changes belong to caller call-binding dependencies.

// Scenario group: default_invalidation
func <T> withDefault(value: T, suffix: String = "old"): String = suffix

func defaultRoot(value: int): String = withDefault<int>(value)

// expect: Replacing the default expression from "old" to "new" invalidates call FIR and any artifact.
// expect: A cache key with only the generic argument [int] is insufficient.

// 18. Generic body changes invalidate every body-derived layer.

// Scenario group: body_change
func <T> transform(value: T): T = value

func transformRoot(value: String): String = transform<String>(value)

// expect: Editing the generic body changes specialized FIR even if declaration name and tuple stay fixed.
// expect: Body byte digest or a complete dependency graph must participate in a future FIR cache key.

// 19. Moving a declaration may change semantic ownership and source origins.

// Scenario group: source_movement
func <T> sourceIdentity(value: T): T = value

// expect: Moving this declaration to another module changes declaration identity and may change visibility.
// expect: Moving within the same module may preserve semantic identity only if partial/import rules agree.
// expect: Old source offsets cannot be applied to different source bytes after cache reuse.

// 20. Generic parameter ordinal changes invalidate declaration identity.

// Scenario group: ordinal_change
func <A, B> ordered(left: A, right: B): (A, B) = (left, right)

// expect: Reordering generic parameters changes [A,B] correspondence and invalidates the key.
// expect: Renaming A to X while preserving owner and ordinal need not change semantic type identity.

// 21. The target triple belongs to code-generation results.

// Scenario group: target_invalidation
func <T> targetIdentity(value: T): T = value

// expect: Verified source/FIR may be target-independent only where its contract says so.
// expect: LLVM modules and objects include target triple, data layout, COFF ABI, and calling convention.
// expect: Current product target is Windows x86-64; another target requires a distinct output key.

// 22. LLVM and pass-pipeline changes invalidate native objects.

// Scenario group: llvm_invalidation
func <T> llvmIdentity(value: T): T = value

// expect: LLVM exact version/build and codegen options are part of a future object-cache key.
// expect: LLVM 23.1.3 C++ API is pinned; a generic FIR instance alone cannot reuse a .obj.

// 23. Runtime layout and linkage changes invalidate linked outputs.

// Scenario group: runtime_invalidation
func <T> runtimeIdentity(value: T): T = value

// expect: SolRT ABI/layout version, dynamic versus static linkage, CRT mode, and system libraries matter.
// expect: --static-solrt-lib changes link closure, not source inference or generic key identity.

// 24. Artifact kind and root set invalidate packaging.

// Scenario group: artifact_invalidation
func <T> artifactIdentity(value: T): T = value

// expect: bin, cdylib, staticlib, and solib have distinct output contracts and roots.
// expect: A private generic helper may be reachable in one artifact but not exported in another.
// expect: AOT and ORC outputs do not share a final artifact cache entry.

// 25. Schema changes invalidate serialized cache payloads.

// Scenario group: schema_invalidation
func <T> schemaIdentity(value: T): T = value

// expect: FIR JSON schema 4 is an inspection contract, not a persistent generic cache schema.
// expect: A future cache payload requires explicit format version and verifier compatibility.

// 26. Source-language edition and semantic rules belong to binding dependencies.

// Scenario group: edition_invalidation
func <T> editionIdentity(value: T): T = value

// expect: An edition change that alters overload, nullability, generic, or effect rules rebinds callers.
// expect: A textual language version cannot replace the semantic ruleset identity.

// 27. Current-culture formatting cannot affect a canonical key.

// Scenario group: culture_independence
func <T> cultureIdentity(value: T): T = value

// expect: Setting process culture to tr-TR or en-US leaves key equality and graph edges unchanged.
// expect: Numeric IDs are rendered invariantly only for internal diagnostic/key text.
// expect: A future portable hash encodes bytes and lengths, not locale-sensitive string formatting.

// 28. Hash collisions are resolved by complete equality.

// Scenario group: collision_handling
func <T> collisionIdentity(value: T): T = value

// expect: Two unequal canonical tuples may have the same hash without becoming the same key.
// expect: The dictionary compares declaration identity and every ordered type identity after hashing.

// 29. Dictionary order cannot define the public graph order.

// Scenario group: dictionary_order
func <T> dictionaryIdentity(value: T): T = value

// expect: Rehashing or changing bucket capacity leaves target identity and semantics unchanged.
// expect: A serializer must not enumerate the internal specialization dictionary to number functions.

// 30. Current FIFO discovery order is compilation-local.

// Scenario group: fifo_order
func <T> leftChild(value: T): T = identity<T>(value)
func <T> rightChild(value: T): T = identity<T>(value)
func fifoRoot(value: int): int = leftChild<int>(rightChild<int>(value))

// expect: Current worklist appends discovered keys and drains FIFO on one thread.
// expect: IDs are dense indices local to the FIR program, not persistent identities.
// expect: Equivalent reordered input files are not yet promised identical byte-for-byte output.

// 31. File enumeration must not become a semantic tie breaker.

// Scenario group: file_order
func <T> fileOrderIdentity(value: T): T = value

// expect: Reordering an explicit source list must not change overload meaning.
// expect: If output ordering changes, it is an implementation determinism issue unless the language defines it.
// expect: Module segments are semantic; directory names and module-path conventions are not identity substitutes.

// 32. In-memory type IDs are local to one specialization workspace.

// Scenario group: workspace_identity
func <T> workspaceIdentity(value: T): T = value

// expect: Two analysis workspaces may both assign numeric type ID 7 to different types.
// expect: Type ownership validation rejects a foreign ResolvedType even if IDs and names match.
// expect: A global cache cannot store raw AstFunction or ResolvedType object references.

// 33. Type interning is synchronized but does not prove parallel FIR construction.

// Scenario group: interner_lock
func <T> internerIdentity(value: T): T = value

// expect: TypeSpecialization guards its mutable interning table with a lock.
// expect: Current FirBuilder's pending queue and function-ID assignment are single-threaded.
// expect: Thread-safe type interning does not authorize sharing one FirBuilder concurrently.

// 34. Future parallel scheduling must preserve single-flight identity.

// Scenario group: single_flight
func <T> singleFlightIdentity(value: T): T = value

// expect: Two workers requesting the same key observe one queued/building state.
// expect: No worker can publish an unverified FIR function as a successful cache hit.
// expect: Completion order cannot determine symbol names, diagnostic order, or artifact bytes.

// 35. Candidate speculation must not publish losing cache entries.

// Scenario group: candidate_isolation
func <T> candidate(value: T): String = "generic"
func candidate(value: String): String = "specific"

func selectedCandidate(value: String): String = candidate(value)

// expect: Candidate analysis is isolated until one overload wins.
// expect: A losing generic candidate must not leave a published instance unless selected elsewhere.
// expect: Native backend rejection cannot cause a second overload to become the source winner.

// 36. Constraint result changes require rebinding.

// Scenario group: constraint_dependency
interface Capability {
    func use(): String
}

class Capable: Object, Capability {
    override func use(): String = "capable"
}

func <T: Capability> useCapability(value: T): String = value.use()

// expect: Adding/removing an implementation changes constraint and member-binding dependencies.
// expect: Current generic native body rejects the type-dependent member lookup.

// 37. Import and visibility changes invalidate lookup.

// Scenario group: import_dependency
func <T> importIdentity(value: T): T = value

// expect: Import aliases, selective imports, public re-exports, and visibility can change candidates.
// expect: A future negative lookup cache records the complete module graph it searched.

// 38. Defaults and inherited defaults are caller-side dependencies.

// Scenario group: inherited_default_dependency
func <T> defaulted(value: T, label: String = "base"): T = value

// expect: An inherited or overridden default owner is included in selected call metadata.
// expect: Changing a default invalidates call mapping even when the target specialization key is unchanged.

// 39. Function effects participate in specialized body validity.

// Scenario group: effect_dependency
func <E: Throwable, T> effectful(value: T): T throws E = value

// expect: Effect type identities are checked after substitution.
// expect: A change to effect hierarchy or checked/unchecked classification invalidates body analysis.
// expect: Function effect sets are not flattened to display names in a future cache key.

// 40. Source origin identity is not a byte offset.

// Scenario group: origin_dependency
func <T> originIdentity(value: T): T = value

// expect: A cached FIR origin maps to the exact immutable source snapshot that created it.
// expect: The same byte offset in another file is not the same source origin.
// expect: Relocation requires a validated origin map; stale ranges never point into new source text.

// 41. Cancellation cannot publish a partial instance.

// Scenario group: cancellation
func <T> cancellable(value: T): T = value

// expect: Cancellation during queue expansion yields Cancelled, not Verified or SourceRejected.
// expect: Partial dictionary entries and function IDs remain private to the abandoned builder.
// expect: A later compilation starts with a new builder and may build the same key afresh.

// 42. Resource exhaustion is not a negative semantic cache result.

// Scenario group: resource_failure
func <T> bounded(value: T): T = value

// expect: More than 4096 unique instances or more than 16,000,000 copied body nodes reports SOL2403.
// expect: More than 128 structural type levels reports SOL2403 where the phase owns the bound.
// expect: A larger later budget may succeed; the earlier resource outcome is not a type failure.

// 43. Verifier failure cannot poison a future successful cache.

// Scenario group: verifier_failure
func <T> verifierIdentity(value: T): T = value

// expect: A corrupt call target or mismatched expression type fails verification with SOL2402.
// expect: The invalid function cannot be retained as a VerifiedFirProgram or artifact cache hit.
// expect: A fresh build from unchanged source can retry and independently verify its own graph.

// 44. Failed linking preserves prior output and cache contents.

// Scenario group: output_transaction
func <T> transactionIdentity(value: T): T = value

// expect: Failed object/link/container publication does not replace a previous complete output set.
// expect: A temporary file is not a successful persistent cache entry.

// 45. Cache reads validate metadata before object construction.

// Scenario group: untrusted_cache
func <T> cachedIdentity(value: T): T = value

// expect: Future cache input checks bounds, schema, target compatibility, dependency digests, and ownership.
// expect: A JSON field `verified = true` cannot mint the compiler's verifier capability.
// expect: A digest proves byte integrity only relative to the named digest; it is not publisher authentication.

// 46. Diagnostic sorting is independent of discovery races.

// Scenario group: diagnostic_order
func <T> diagnosticIdentity(value: T): T = value

// expect: Multiple diagnostics sort by stable source identity, byte position, phase, code, and local ordinal.
// expect: Candidate provenance and causal note order are preserved after sorting.

// 47. Error phases remain distinguishable in cached results.

// Scenario group: error_phase
func <T> errorIdentity(value: T): T = value

// expect: SOL2400 is native admission; SOL2402 is independent FIR verification; SOL2403 is resource exhaustion.
// expect: A future cache does not rewrite one phase's failure as another phase's failure.

// 48. Cache schema and language edition are different versions.

// Scenario group: version_axes
func <T> versionIdentity(value: T): T = value

// expect: FIR JSON schema 4, language edition 2026, LLVM 23.1.3, and SolRT ABI/layout have separate identities.
// expect: Bumping one version invalidates only the outputs that depend on it, but never weakens verification.

// 49. AOT and ORC share semantic selection but not final code cache identity.

// Scenario group: backend_axes
func <T> backendIdentity(value: T): T = value

// expect: Both backends begin from the same verified source semantics.
// expect: LLVM codegen options, target process, runtime closure, and artifact roots differ by backend.
// expect: AOT object bytes are not ORC guest execution cache entries.

// 50. Cross-container IDs are never source keys.

// Scenario group: container_identity
func <T> containerIdentity(value: T): T = value

// expect: A producer's module/type/function ID is not a consumer's declaration identity.
// expect: Current solib schema 2 does not define external generic FIR instantiation.
// expect: A future container contract must version identities, constraints, ABI, and verification rules.

// 51. Reproducible package serialization is separate from cache lookup.

// Scenario group: package_order
func <T> packageIdentity(value: T): T = value

// expect: Fixed ZIP timestamps and ordinal entry order help reproducibility but do not define semantic keys.
// expect: Package payload hashes are computed after canonical serialization and compatibility metadata.

// 52. Required conformance evidence
// ---------------------------------
//
// A future implementation must exercise each of the following as independent oracles:
//
// - equality of identical keys and inequality of distinct owner/argument tuples;
// - recursive closure on exact keys and bounded rejection of growing keys;
// - caller substitution before nested instance creation;
// - invalidation after every semantic, ABI, runtime, target, schema, and artifact dependency change;
// - deterministic diagnostics, graph order, and bytes for repeated identical inputs;
// - independent rejection of a foreign type or source declaration reference;
// - cancellation, resource limits, verifier failure, and failed output transaction;
// - cache collision handling and tampered/truncated future payload rejection;
// - identical result despite randomized internal hash seed and worker schedule.
//
// No entry in this matrix reports that the required future cache tests have run. Current code has no
// persistent cache or parallel FIR scheduler to exercise.
