<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Generic FIR cache identity and determinism

This guide documents how the current compiler reuses generic FIR instances within one compilation,
what currently affects ordering, and what a future incremental or persistent cache would need to
validate. The [normative contract](../../spec/GenericFirCacheAndDeterminism.sol) and its
[scenario matrix](../../spec/examples/semantics/GenericFirCacheAndDeterminism.sol) define the full
requirements. The current implementation remains a bounded single-build mechanism.

## Current implementation

`FirBuilder` keeps an in-memory table from a source `AstFunction` identity and ordered type-argument
IDs to a dense FIR function ID. A newly discovered closed generic key is entered once and appended
to a FIFO queue. The builder drains the queue, specializes the selected body, and may discover more
generic calls. A repeated exact key reuses the existing ID, which closes recursive calls without
adding a second body. The completed `FirProgram` stores functions in dense-ID order.

The type-argument ID text is formatted with invariant culture. The key still uses the source
function object from the active analysis workspace; the text component is only a compact
compilation-local tuple. The key is not serializable identity. A type ID is owned by one
`TypeSpecialization` instance and is meaningful only within that workspace.

The worklist is single-threaded. It uses dictionaries for lookup, but does not enumerate the
specialization dictionary to produce functions. The function list is read by dense ID after the
queue is drained. This gives deterministic behavior for the same source snapshots, same explicit
input ordering, same compiler build, and same options. The current implementation does not promise
byte-identical output if equivalent source files are supplied in a different order.

`TypeSpecialization` maintains a synchronized in-memory interner. It starts from one source
`TypeAnalysis`, owns new substituted types, checks that input and replacement types belong to that
workspace, and interns structural results under a lock. That lock makes type construction table
updates safe from concurrent access; it does not make `FirBuilder` parallel or make its ID
assignment deterministic under concurrent body construction.

The current system does **not** implement:

- persistent or cross-process generic FIR caching;
- incremental specialization reuse across separate compiler invocations;
- cache serialization or cache deserialization;
- a stable portable fingerprint for source declarations or resolved types;
- a parallel FIR work queue or single-flight state shared between builders;
- stable external IDs for FIR functions or types;
- reuse of LLVM objects, ORC machine code, or linked artifacts by the generic key alone.

## Instance identity

One instance key identifies one source generic declaration and one complete ordered tuple of
closed resolved type identities. For:

```sol
module docs.generic_fir_specialization
func <Key, Value> select(key: Key, value: Value): Value = value
```

`select<String, int>` and `select<int, String>` are different instances. The tuple follows the
declaration's parameter order, even if the call uses named arguments in a different order. The
simple name `select`, the source span, and a printed type name do not identify the instance.

The declaration side of the key is anchored to the actual declaration object in the source-analysis
workspace. This distinguishes declarations with the same spelling in different modules or overload
sets. A generic parameter is identified by owner declaration and ordinal, not only by its display
name. Thus an owner parameter named `T` and a method parameter named `T` remain distinct.

The type side is owned by the active `TypeSpecialization` workspace. The semantic shape may include
primitive or nominal identity, ordered generic arguments, nullable wrappers, array and tuple
constructors, function parameter modes, result and effects, and ref-like classification. The
current native admission set accepts only a subset of these shapes. Preserving identity is separate
from claiming native representation.

Nullability and structure cannot be discarded for cache convenience. `String`, `String?`, `[]String`,
`[]String?`, and `([]String)?` describe different positions and constructors. Function effects,
passing modes, nominal declaration ownership, and generic argument order also matter. A formatted
name is appropriate for display; it is not a canonical key.

## Local IDs and ownership

FIR function IDs are dense indices in one `FirProgram`. JSON `FirCall.Target` values refer to those
indices. Resolved type IDs belong to one analysis workspace. Both are local references, not
cross-build identities. A consumer must not persist them and compare them with values from another
compiler process.

The verifier checks that each type reference belongs to the current `TypeSpecialization` owner. A
foreign `ResolvedType` with the same integer ID and name is still foreign. A raw `AstFunction` object
from an old source workspace is likewise not a valid key in a new build.

This ownership rule is why a global dictionary keyed by `(function name, type ID)` would be unsafe.
It would conflate same-named functions and permit stale references to survive their source snapshot.
The active design discards the builder-local tables when that compilation ends.

## Queue behavior and recursion

When a selected generic call has a supported, closed type tuple, the builder performs these steps:

1. apply the caller's substitution to the selected type arguments;
2. validate the target declaration, argument count, ownership, closedness, native shape, and constraints;
3. construct the declaration-plus-ordered-ID key;
4. return the existing function ID for an exact hit;
5. otherwise allocate a new dense ID and append a pending specialization to the FIFO queue;
6. build queued bodies until there are no pending instances or a diagnostic/cancellation stops the build.

An exact recursive edge is finite. `walk<int>` calling `walk<int>` points back to the same instance.
Mutual recursion also closes when it cycles over a finite set of exact keys. A growing sequence such as
`grow<T> -> grow<Box<T>> -> grow<Box<Box<T>>>` is not exact recursion. Similar names or ABI layouts
cannot be used to merge distinct keys.

Current resource bounds limit one compilation to 4,096 generated generic functions, 16,000,000
source AST nodes counted across copied specialized bodies, and 128 structural type levels. Exceeding
a bound produces `SOL2403`. Reuse of an exact key does not copy another body; a distinct key does.
The compilation does not emit a verified program if required work remains incomplete.

The queue's insertion and drain order currently follows discovery from ordinary functions and their
source body traversal. The implementation is single-threaded and uses source ordering for that
discovery. A function ID may therefore change when source files are presented in a different order,
even if source selection and runtime behavior are equivalent. These IDs are document-local and must
not be used as cache keys or stable native symbol names.

## Determinism requirements

For one fixed build identity, source snapshot set, input ordering, and options, a repeated build
should select the same source declarations, ordered type tuples, graph edges, diagnostics, and output
bytes. Any intentionally varying output field, such as a permitted debug timestamp, must be
documented separately.

Hash tables may speed up lookup. Their bucket order, randomized hash seeds, growth strategy, and
enumeration order cannot affect semantic selection or public output. Dictionary values must not be
serialized by enumerating a hash map. Hash collisions are resolved by full canonical equality; a
matching hash is not a matching instance.

Current culture and timezone cannot affect keys, type selection, diagnostic order, symbol names, or
serialized numeric IDs. The builder formats local type IDs with invariant culture. A future portable
cache key should be encoded as framed bytes with an explicit schema, not a culture-formatted string.

The language's defined source order still matters where the language says it does. Deterministic
canonicalization cannot reorder declarations to change overloads, partial-type merging, default
inheritance, or diagnostics. A future build system can canonicalize an explicit input set for
reproducible output only after proving that the canonical order preserves those semantic rules.

## Cache layers must remain separate

The compilation pipeline has several results that may eventually be cached. Each result has a
different dependency set:

| Result | Examples of required inputs |
|---|---|
| PSI/AST | Source bytes, decoder policy, language edition, grammar and parser implementation version |
| Declarations | Source set, module/import graph, visibility, partial-type merge rules |
| Call binding | Caller body, candidate set, argument names/order/modes, expected result, inference and overload rules |
| Type substitution | Source declaration, ordered closed type tuple, type identity and constraints |
| Specialized FIR | Substituted body and transitive selected calls, semantic-analysis rules, FIR schema, verifier version |
| LLVM module/object | Verified FIR, target triple/data layout, ABI, LLVM 23.1.3 build, codegen and pass options |
| Linked artifact | Objects, LLD.COFF/LLD.Common, SolRT ABI/layout, linkage, CRT and system library closure |
| Sol container | Container schema, compiler/target/runtime compatibility, ordered payloads and digests |

A key for an earlier stage cannot stand in for all later inputs. The generic tuple alone may identify
which source-level instance was requested. It cannot determine if the body has changed, if a
constraint is still satisfied, if a target ABI has changed, or if the final artifact has a different
root or runtime closure.

The JSON FIR inspector uses schema 6 and local IDs, but this does not make it a persistent cache
format. The current `.solib` manifest schema 2 does not define external generic FIR instantiation.
Package digests check payload integrity relative to the manifest; they do not establish source
identity, publisher authenticity, or verifier compatibility.

## Future specialized-FIR dependencies

A future FIR cache must include, or derive from a complete dependency graph, at least:

- the canonical generic declaration and module identity;
- the full body content and every referenced declaration that can affect binding;
- ordered type arguments with owner-aware, nullable, effect, and ref-like structural identity;
- all constraints, inherited bases/interfaces, conversions, and lookup facts consulted;
- selected overload identity and argument mapping at each nested generic call;
- default expression values and the owner from which an inherited default is taken;
- the source language edition and semantic-analysis rule version;
- the FIR node schema, source-origin contract, verifier rules, and native admission policy;
- imported modules, platform declarations, runtime built-ins, and configuration that affects lookup.

If a body calls another generic function, the transitive selected instance graph must also be
validated. An implementation can record direct dependencies and invalidate transitively, or store a
closed graph fingerprint. It cannot reuse a parent body while overlooking a changed callee,
constraint, or member selected by source analysis.

Negative results need a complete dependency set too. A prior "no overload" or "constraint failed"
result can become successful after a declaration, import, visibility, base type, or candidate is
added. A negative cache entry without the searched declaration environment is unsound.

## Target and runtime invalidation

LLVM modules and object files require a target-specific key beyond source-level FIR. Include the
target triple, COFF ABI, data layout, calling convention, LLVM exact build/configuration, optimization
pipeline, relocation options, and code-generation switches.

Linked outputs need further inputs: exact LLD.COFF and LLD.Common identity, runtime layout and ABI,
dynamic or static SolRT linkage, CRT ownership, Windows system libraries, artifact kind, export list,
companion names, and output root. A string that only says `LLVM 23` is too broad when this repository
pins LLVM 23.1.3.

`bin`, `cdylib`, `staticlib`, and `solib` have different roots and consumer contracts. AOT emits
native artifacts. ORC runs a `bin` in a process-isolated guest and has no persistent output path.
They may share verified source semantics, but they do not share final artifact cache entries.

`--static-solrt-lib` changes runtime link closure. It does not change source overload resolution or
the generic key. The CLR host and ORC guest own different heaps and processes; process isolation does
not collapse their runtime or root-layout inputs into one cache identity.

## Source origins and path changes

Source spans are offsets into immutable UTF-8 snapshots. They provide diagnostic provenance but do
not define a function identity. A cached FIR body with source offsets can be reused only if the
compiler proves the offsets still refer to the exact source bytes or regenerates an origin map from
validated identity data.

Moving a declaration between files may change module, visibility, partial declaration grouping,
imports, or debug origin. Moving it within one module may preserve some semantic facts but still
change source locations. The dependency model must represent the actual rules rather than treating
every move as either always harmless or always invalidating.

Absolute machine-local paths should not leak into public symbols or package manifests. If normalized
paths participate in a build key because debug output records them, the normalization algorithm,
Windows case behavior, and separator policy must be versioned.

## Cancellation and failure atomicity

The current builder keeps partial instances inside the active builder. If cancellation interrupts
queue expansion, the build is abandoned; no `VerifiedFirProgram` is returned from that incomplete
graph. The next compilation creates a fresh builder.

A future cache must make publication transactional. Build into a private staging location, validate
all metadata, dependency fingerprints, byte lengths, hashes, target compatibility, and verifier
results, then publish the complete entry. A failed or cancelled write leaves a cache miss. A
partially written directory, temporary file, or `verified: true` JSON field cannot be trusted.

Readers of persistent entries must bound archive and payload sizes before allocation or
decompression, reject unsupported schemas, check workspace rehydration, and independently verify
FIR before native lowering. A digest confirms bytes relative to the declared digest; it does not
authenticate who created the entry.

Output publication follows the artifact transaction. A failed object, linker, or container step must
not replace a previously committed output set. The cache result and output transaction are related
but separate: a valid FIR cache entry can survive an artifact-specific failure only if its own
validation and publication succeeded.

## Diagnostics and resource results

A cache hit and a fresh build should produce equivalent diagnostics. Messages tied to source spans
must be remapped only after the source snapshot match is verified. A changed source location cannot
inherit a stale byte range.

When future parallel traversal discovers diagnostics in different orders, sort by a stable
source-identity and byte-position tuple, then by phase, diagnostic code, and deterministic local
ordinal. Preserve causal note order and language-defined overload order. Do not use hash-set
iteration to choose the first error.

Keep failure phases distinct:

| Diagnostic | Owner | Cache meaning |
|---|---|---|
| `SOL2400` | Native admission | Source-bound shape is outside the current native subset |
| `SOL2402` | FIR verifier | Constructed FIR violates a source/proof invariant |
| `SOL2403` | Specialization resource accounting | An explicit expansion or depth budget was crossed |
| Cancellation | Build coordinator | Work was stopped; source validity is undecided |

Resource exhaustion is not a stable semantic rejection. A later compilation with a different
resource budget may succeed. Cancellation must not be cached as an invalid generic argument.

## Parallel scheduling requirements

The current FIR queue is single-threaded. `TypeSpecialization` synchronization covers its interner;
it does not establish concurrent safety for candidate-local body binding, `FirBuilder`, ID
assignment, verifier state, or diagnostic aggregation.

If generic specialization is parallelized, all workers requesting one canonical key must join one
single-flight state or otherwise publish exactly one result. The state must distinguish queued,
building, verified, failed, and cancelled. A caller that observes a building instance cannot pass it
to lowering. Mutual recursion must not deadlock waiting on a worker that is itself waiting for the
caller.

Parallel completion order cannot define public function IDs, machine symbols, archive entry order,
diagnostics, or binary output. Allocate IDs in a deterministic discovery pass or remap after graph
closure using a canonical order. A worker failure or cancellation must leave dependents unverified
and prevent partial success.

Candidate analysis must also remain isolated. Parallel candidate checks cannot share mutable
substitutions or publish losing generic candidates before overload selection commits one winner.

## Portable fingerprint design constraints

A future persistent cache will need a portable fingerprint, but this repository does not yet define
one. The design must first choose a canonical byte encoding for declaration and type identity, then
choose a digest over those bytes. Picking a digest algorithm before defining the encoded fields does
not solve identity.

The encoding must be length framed. Concatenating text without lengths or separators that are
unambiguous can make distinct field sequences collide before the digest is computed. Each node
should carry a schema tag, node kind, field count, and ordered child records. Lists need an explicit
count. Optional fields need an explicit presence marker. Integers need a fixed-width or canonical
varint representation with a documented byte order.

At minimum, a portable source declaration record needs:

- defining logical module identity;
- declaration kind and overload-signature identity;
- generic parameter count and owner-relative ordinal model;
- language edition and declaration normalization version;
- source dependency fingerprints for referenced signatures and body facts;
- access/visibility information when it affects candidate selection;
- an origin mapping version if serialized diagnostics or debug data are cached.

At minimum, a portable closed type record needs:

- the primitive-set version and canonical primitive tag;
- nominal declaration identity and arity for nominal types;
- ordered type-argument records;
- explicit tags for nullability, dynamic array, tuple, function, and any future constructors;
- function parameter modes, result, and normalized effects;
- layout-affecting modifiers and ref-like classification;
- child counts and unambiguous framing.

The encoding must reject or represent cycles deliberately. Source type graphs are expected to be
finite, but the serializer must not recurse indefinitely if it encounters a malformed or hostile
graph. A cycle marker is safe only if the language defines a canonical graph identity that handles
recursive nominal references. Otherwise the implementation should reject a cyclic type graph before
cache publication.

Do not use a CLR object hash code, `GetHashCode()` output, pointer value, dictionary bucket, local
type ID, source line number, or absolute checkout path as a cross-process fingerprint. These values
can be useful for in-memory lookup, but are not stable identities.

## Version dimensions

Different compatibility inputs need separate version fields so a tool can reject an incompatible
entry for the right reason. One broad compiler version field is insufficient when some phases remain
compatible and others do not.

| Version dimension | Invalidates or constrains |
|---|---|
| Language edition | Parsing, declarations, source typing, overload selection and generic rules |
| Parser/AST schema | Cached syntax and source-origin links |
| Declaration/type identity schema | Binding, substitutions, constraints and portable type fingerprints |
| Body analysis rules | Selected call bindings, effects, flow and specialized body facts |
| FIR schema | Serialized FIR node layout and call/specialization metadata |
| Verifier contract | Whether cached or rebuilt FIR can mint verified capability |
| Native admission policy | Which verified source shapes can proceed to LLVM |
| Target/COFF ABI | Layout, call convention and object code |
| LLVM exact build | LLVM modules, optimized bitcode and objects |
| LLD and CRT identity | Linked DLL, EXE and static archive |
| SolRT ABI/layout | Runtime imports, object roots and linked artifact closure |
| Artifact/container schema | Manifest fields, payload roles and reader compatibility |
| Debug-origin schema | Source paths, spans and machine-code locations |

A cache implementation can define a compatibility range only when it proves that the consumer's
rules preserve the producer's meaning. Merely parsing an older schema or recognizing a semantic
version string is not enough. If a verifier changed, an older `verified` claim must not bypass the
new verifier.

## Dependency graph completeness

Incremental invalidation is correct only if dependencies are complete. A direct declaration hash
does not capture names selected through an import, base projection, or overload group. A source
body hash does not capture a referenced default argument declared elsewhere. A type tuple does not
capture the constraint declarations that established its applicability.

For every specialized body, record the declarations and relations consulted during source analysis.
Depending on the feature, this can include:

- direct and inherited callable declarations considered during overload resolution;
- imported modules, aliases, selective imports, and public re-export chains;
- generic constraint declarations, implemented interfaces, and substituted bases;
- extension or companion candidates if those are part of the language contract;
- default argument expressions and inherited default owners;
- conversion operators or intrinsic conversion tables used for ranking;
- checked exception hierarchy and generic effect substitutions;
- property getter/setter, indexer, and operator declarations selected by the body;
- runtime catalog declarations and ABI metadata for compiler-owned intrinsics.

When a dependency changes, invalidation can be eager or lazy. Eager invalidation deletes stale
entries at update time. Lazy invalidation compares a stored digest or generation when an entry is
requested. Either approach must prevent a stale verified body from being returned. If dependency
tracking is incomplete or uncertain, the safe action is a cache miss and source reanalysis.

## Negative and deferred results

Positive and negative caching have different failure modes. A positive entry can be unsafe if a
selected declaration or body changed. A negative entry can be unsafe if a new declaration or import
adds a candidate that was absent from the original search.

Do not collapse these outcomes:

| Outcome | Reuse rule |
|---|---|
| Selected and closed | Reuse only with complete positive dependencies and verifier compatibility |
| No applicable candidate | Requires the complete searched scope and candidate-set dependencies |
| Constraint rejected | Requires the same constraint graph and substituted type identities |
| Ambiguous | Requires the same complete candidate set and ranking rules |
| Deferred | Usually requires reanalysis; missing evidence may now be available |
| Native admission rejected | Source binding may remain valid while target policy changes |
| Resource limit | Not a semantic negative; a later budget may permit success |
| Cancelled | No validity conclusion; never cache as source rejection |

An implementation may cache a negative source result if its dependency set is as complete and
versioned as the corresponding positive result. It must store the phase, reason, dependencies, and
source origin. A generic `failure` entry without phase ownership is not sufficient.

## Revalidation before returning cached FIR

A cache reader should perform validation in a deliberate order before it makes a cache hit visible:

1. check total record and payload sizes before allocating large buffers;
2. parse the outer container and reject unsupported schema versions;
3. verify the declared byte lengths and content digests;
4. resolve compiler, language edition, target, LLVM, runtime, and artifact compatibility;
5. compare the current dependency fingerprints with the stored dependency manifest;
6. reconstruct source and type identities in the new owning workspace;
7. reject missing declarations, foreign IDs, open generic arguments, and duplicate keys;
8. validate graph closure and every call edge;
9. run the current independent FIR verifier;
10. only then return a verified capability to LLVM lowering.

The order may differ internally if equivalent safety is established, but no step can be skipped by
trusting a serialized boolean or file name. Parser and cache input are data. They cannot issue
instructions to the compiler or override local policy.

The cache reader should also reject path traversal, Windows case-colliding entry names, duplicate
canonical keys with different payloads, undeclared payloads, truncated streams, and excessive
compression ratios. Container-format checks do not replace semantic FIR verification.

## Deterministic diagnostic records

If diagnostics are cached, store a stable phase code and semantic cause separately from localized
presentation text. Presentation can change without changing source validity. However, the diagnostic
code and meaning are versioned inputs when a build tool or IDE consumes them as machine-readable
results.

Record an origin by source snapshot identity and byte range, not byte range alone. A generic-body
diagnostic should identify both the body expression and the specialization call chain when that
context is available. If a cached result is reused from a body called at multiple sites, the
compiler must produce the current call-site notes rather than returning notes from whichever call
first populated the cache.

Sort independent diagnostics by an explicit stable key. Preserve source-defined candidate order and
cause/related-location relationships. Sorting all notes lexicographically can destroy causal
structure, so diagnostics need typed primary and related locations rather than an unstructured flat
list.

Numeric IDs in FIR JSON are local references. A deterministic serializer should number records in a
document-local canonical order and ensure every reference resolves within that document. The
current serializer preserves the verified program's function IDs; it does not define a durable
canonical numbering across builds.

## Current resource accounting versus cache quotas

The current generic builder has three explicit expansion bounds: 4,096 instances, 16,000,000 copied
source body nodes, and 128 nested structural type levels. These bounds limit one compiler build.
They do not bound a future persistent cache directory, one malicious serialized payload, or the sum
of concurrent compiler processes.

A persistent cache needs separate quotas for:

- maximum entry metadata bytes;
- maximum serialized FIR bytes before and after decompression;
- maximum dependency count and graph depth;
- maximum number of entries or total storage use;
- maximum time spent validating a candidate entry;
- maximum number of concurrent writers/readers per key;
- maximum wait for a single-flight producer before fallback or cancellation.

The quota owner must define eviction and cleanup without deleting a valid entry that another
process has opened. Cache pressure may evict reusable work, but it cannot cause the compiler to
accept an unchecked entry or omit required specialization work.

## Failure-atomic publication

A persistent cache writer should use a staging key that is not discoverable as a hit. It writes the
payload and manifest, flushes according to the durability promise, reopens or validates the staged
entry, checks hashes and semantic dependencies, then publishes by atomic rename or an equivalent
protocol. A crash before publication leaves an orphan that can be safely removed after ownership
checks; it must not appear complete.

For content-addressed storage, the content digest is computed only after deterministic serialization.
The manifest must include the schema and all semantic inputs needed to interpret those bytes. If two
writers race to publish the same key, both payloads must be equivalent under the canonical format;
otherwise the cache reports a conflict or discards both. “Last writer wins” is not a substitute for
identity validation.

Cache publication and final artifact publication are separate transactions. A successful verified
FIR entry can remain useful if a later linker step fails. An object or package cannot be committed
without the correct complete inputs. The previous user output remains intact after failed linking or
container creation.

## Review and qualification scenarios

Before enabling a persistent cache, reviewers should require evidence for each independent risk:

- same declaration and same ordered tuple reuse one instance;
- argument permutation, owner identity, nullability, effects, and ref-like modifiers stay distinct;
- exact recursion closes while growing keys stop at a bounded resource error;
- candidate rollback leaves no losing or ambiguous entry visible;
- changing a body, overload, import, constraint, default, effect hierarchy, or base relation invalidates;
- changing target, LLVM/LLD, ABI, SolRT linkage, artifact kind, export set, or schema invalidates the right layer;
- equivalent source inputs under different worker schedules produce equivalent public results;
- current culture, timezone, path separator, and hash seed do not affect semantic keys;
- foreign workspace IDs, stale offsets, duplicate payloads, tampering, and truncation are rejected;
- cancellation and process crash do not publish partial verified entries;
- a cache hit is independently checked by the current verifier before native code generation.

These are future cache qualification requirements, not a statement that a persistent cache exists or
that these scenarios have been tested in this work.

## Review checklist

Before claiming an incremental generic FIR cache, verify that the implementation has:

- one versioned canonical declaration identity and structural type fingerprint;
- full ordered tuple equality after hash lookup, including nullable and effect shapes;
- per-workspace ownership checks and safe rehydration into new objects;
- complete transitive dependencies for bodies, constraints, overloads, imports, and defaults;
- separate keys for source binding, FIR, target code, linking, and containers;
- exact LLVM/LLD, target, ABI, SolRT, CRT, artifact, and export inputs where they apply;
- deterministic graph, diagnostic, symbol, and serialization ordering;
- no dependence on dictionary iteration, thread schedule, current culture, or process IDs;
- bounded resource use, cancellation, and single-flight behavior if parallel;
- atomic cache publication and independent verification of every deserialized FIR entry;
- a cache miss after schema, compiler, semantic rule, target, runtime, or dependency changes;
- tests and qualification for collisions, recursive keys, tampering, partial writes, and invalidation.

Until these conditions exist, describe the current behavior as compilation-local generic instance
interning with a bounded FIFO worklist. Do not describe it as a persistent cache, an incremental
build cache, a parallel specialization system, or stable cross-process identity.
