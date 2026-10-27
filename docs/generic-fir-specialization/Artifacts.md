<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Generic FIR instances at artifact boundaries

This guide explains how the current bounded generic-function FIR slice relates to Sol's four
native artifact selections. It separates source binding, FIR verification, backend lowering,
linking, and package consumption. Success in one phase does not imply success in a later phase.

The normative contract and case matrix are [in the spec](../../spec/GenericFirArtifacts.sol) and
[its examples](../../spec/examples/semantics/GenericFirArtifacts.sol). The current compiler supports
only a development subset. Sol 0.0.1 remains the initial frontend component.

## Current capability

Development builds currently include:

- Windows x86-64 AOT output for the verified bootstrap subset;
- ORC execution of eligible bin programs in a separate native guest process;
- scalar C ABI cdylib and staticlib output;
- a closed-subset solib container and reader;
- bounded specialization of selected module-level generic free-function calls in FIR.

For an admitted generic call, source binding records closed type arguments in declaration order.
FIR builds a monomorphic body for each canonical declaration-and-argument tuple. The verifier
checks declaration identity, type ownership and closedness, constraints, substituted types and
effects, call mapping, exact target, uniqueness, and reachability.

This is not full generic semantics. The current body builder substitutes already-bound source
facts; it does not repeat complete overload and body analysis for each concrete type. Generic owners,
members, constructors, properties, operators, local functions, function values, and externally
requested instantiations are outside this slice. Operations or members whose meaning depends on a
substituted type are rejected until the necessary source analysis can be repeated.

An eligible instance must also fit the selected artifact's ABI, root, and runtime rules. A source
call that binds successfully may still be rejected before verified FIR, LLVM construction, link, or
container commit. The examples describe expected boundaries; they are not reports that every case
has been run.

## The decisions are independent

The relevant command-line selections are:

| Decision | Solc option | Default | What it changes |
|---|---|---|---|
| Artifact | --artifact-type=bin\|cdylib\|staticlib\|solib | bin | Output and root contract |
| LLVM backend | -C llvm-compiler=aot\|orc | aot | Native link or guest execution |
| SolRT linkage | --static-solrt-lib | dynamic | Runtime deployment and link closure |

Artifact selection does not alter source parsing, name resolution, overload ranking, generic
inference, conversion choice, checked-effect analysis, or the selected source target. Solc chooses
the source overload first. The later backend may reject the selected program if its shape is outside
the verified subset; it cannot silently choose another overload.

For example, identity(42) selects a closed identity<int> instance when the source binder has enough
evidence and the selected candidate is unique. Asking for a DLL instead of an executable does not
change that tuple. It can change whether there is a permitted root or ABI wrapper capable of using
the instance.

## Artifact summary

| Artifact | AOT behavior | ORC behavior | Generic boundary |
|---|---|---|---|
| bin | Links a Windows executable | Runs eligible bin in a guest; no output path | Closed instances may be private reachable helpers |
| cdylib | Links DLL and import library | Rejected as an ORC output | A concrete C wrapper may call a private closed helper |
| staticlib | Builds COFF archive | Rejected as an ORC output | Archive may contain private helper objects |
| solib | Writes the versioned Sol container | Rejected as an ORC output | No consumer-requested generic instantiation |

The table is a boundary summary. It does not mean every supported source shape is implemented in
every artifact. Consult [the native guide](../fir-native.md) and
[library artifact contract](../library-artifacts.md) for the complete current bootstrap limits.

## bin: reachable closed instances

A bin requires exactly one admissible non-generic entry: Main.main(args: []String): (). The entry
receives the process arguments excluding the executable path. It is not itself a generic
specialization.

The entry and each admitted ordinary function form the initial root graph. Directly selected
generic calls add closed instances to that graph. Calls between generic functions compose the
outer substitution before forming the nested instance key. If relay<T> calls identity<T>, then
relay<int> targets identity<int> after the outer T has been substituted.

The compiler interns a single logical instance for the same canonical declaration and ordered
closed tuple within one compilation. Two call sites to identity<int> share one function body.
identity<String> remains a different instance. Instance IDs are local to the FIR program and
should not be interpreted as stable symbol names.

Every concrete instance is checked independently. A body that is valid for int does not become
valid for every T. The selected tuple must satisfy declared constraints, supported native type
rules, body-operation rules, call/effect verification, and runtime root obligations.

Current limitations include generic owners and members, open generic callers that require
type-dependent rebinding, function values, closures, ref-like arguments, unsupported nullable
layouts, generic calls through reflection, and unsupported operations over T. Source validity does
not bypass these limits. FIR failure prevents LLVM lowering.

## C libraries: concrete wrapper, private helper

cdylib and staticlib expose concrete C ABI wrapper functions. A generic source declaration is not a
C callable: C has no Sol type parameter, constraint, overload, or inference representation.

A nongeneric wrapper with an admitted native signature may call a generic helper internally. The
compiler still needs one uniquely selected closed tuple and a verified transitive body. The helper
is private implementation detail. It does not appear as a generic export, import-library symbol,
or stable cross-build ABI.

Current C wrapper admission is limited to qualified scalar integer, single, and double values and
supported scalar ref/out locations. The current boundary rejects String, Object, arrays, nullable
types, tuples, half, quad, generic parameters, unsupported checked effects, and stateful receiver
exports. See [library artifacts](../library-artifacts.md) for symbol rules and native consumer
requirements.

An export collision is an artifact error. Erased generic arguments cannot distinguish two wrappers
that resolve to the same C symbol. Backend capability must not resolve a source overload.

The wrapper's source declaration remains the public semantic entry. FIR records the private helper
call and its concrete tuple, while the export table records only the wrapper's native symbol.
Changing an internal instance name must not require a C consumer to change its declarations.

The DLL's import library and its runtime companions are part of one output transaction. A static
archive may include private generic helper objects when they are reachable from an admitted
wrapper, but it does not export those helpers as Sol functions. Foreign consumers see the
documented concrete wrappers only.

## solib: container is not a generic linker

The current development solib format uses manifest schema 2. It can carry module export metadata,
the currently defined closed nongeneric FIR payloads, native objects, binary LLVM bitcode where
permitted, and integrity digests. The manifest pins compiler identity, edition, target,
LLVM 23.1.3, runtime layout/linkage, module IDs, payload lengths, and SHA-256 values.

The reader checks archive limits, entry count, normalized paths, duplicate and Windows
case-colliding names, declared payload membership, byte lengths, digests, schema, and compatibility.
It returns checked container data. A serialized verified flag cannot mint the compiler's
VerifiedFirProgram capability, and a reader does not execute code.

A solib consumer cannot request identity<int> from an open identity<T> declaration in another
container. Producer-local declaration, function, and type IDs are not consumer identities.
External generic instantiation, dependency resolution, and --extern consumption remain
unimplemented.

A producer may retain a closed helper privately only when the active container payload contract
allows it and the full dependency/runtime closure is declared. Schema 2 currently does not define
generic FIR instance payloads, so the condition is not met for a closed generic helper. A future
version may permit private closed instances without making them generic exports.

Adding generic exports would require a new versioned contract for declaration and type identity,
calling convention, layout, constraints, compiler/runtime compatibility, dependencies, visibility,
duplicate ownership, cache invalidation, and consumer verification. The current schema cannot gain
that meaning through an unversioned manifest field.

Digests provide integrity relative to the manifest, not publisher authentication, trust, or a
digital signature. A container reader validates untrusted names and sizes before extraction, and
must keep extraction within the selected destination.

## AOT and ORC

The LLVM selection is -C llvm-compiler=aot|orc. AOT is the default and produces linked artifacts
when requested and supported. ORC defaults to --emit=jit and runs only an eligible bin in a
separate native process. Arguments after -- are program arguments. JIT mode has no output path;
-o and --out-dir are invalid. ORC cannot produce a DLL, archive, or solib.

Both paths consume the same verified FIR semantics. AOT emits Windows AMD64 COFF and links via the
embedded LLD.COFF and LLD.Common components. ORC receives binary bitcode from the constructed LLVM
module, verifies it, then owns its LLJIT context and code in the native guest. Solc does not invoke
guest function pointers in its CLR process.

The CLR host heap and guest Yellow Tea heap are process-isolated. They do not exchange object
references or roots. Process isolation is an execution boundary; it is not by itself an operating
system sandbox. The guest import allowlist and IPC validation remain required.

## SolRT linkage

Dynamic SolRT linkage is the default. For an AOT executable, the output deployment includes the
runtime DLL. A cdylib deploys its runtime companion according to the artifact contract. A
staticlib consumer must satisfy the selected runtime closure and Windows system-library
requirements.

The --static-solrt-lib flag selects static SolRT linkage. It does not select an artifact, switch
between AOT and ORC, alter generic type arguments, or change source semantics. A static runtime
does not make an archive freestanding.

For ORC, the selected runtime lives within the guest process. Static or dynamic guest linkage never
loads guest objects into Solc or makes guest symbols callable from CLR code. Runtime linkage and
runtime layout are artifact compatibility inputs.

The development runtime layout is provisional. It does not establish a permanent SolRT ABI or
complete object/GC layout.

## End-to-end specialization path

Consider this source:

    module example.math

    func <T> identity(value: T): T = value

    func useInteger(value: int): int = identity(value)

For the call in useInteger, the source binder first resolves the name and candidate set. It maps
the argument to the formal parameter, performs generic inference, checks constraints and
conversions, and selects one target. The selected binding stores the concrete type argument at the
declaration ordinal. Backend capability does not participate in this decision.

FIR construction then reserves or reuses the instance key consisting of the canonical
identity declaration and the ordered type tuple [int]. The generic body is copied into an ordinary
monomorphic function. Nested generic calls repeat this process after the caller substitution has
been composed.

The independent verifier checks the function against the original source declaration and selected
body bindings. It verifies that the type argument belongs to the same analysis workspace, is
closed, meets constraints, and yields the recorded signature and effects. It checks every FIR
expression type against the specialized source binding and verifies that each call targets exactly
the selected instance.

Only a verified graph can proceed to LLVM. The output policy then determines roots and public
symbols. For a bin, the entry retains reachable helpers. For a C library, the exports retain
concrete wrappers and any private helper closure. For a solib, the schema controls which module and
payload records can be emitted. If any stage fails, the driver does not publish a partial output.

This sequence is important for nested specialization. A caller such as relay<T> calling
identity<T> has an open type parameter during source binding. After the compiler has constructed
relay<int>, it must use the composed concrete mapping to request identity<int>. It cannot preserve
the caller's open T in FIR, guess an identity from a display name, or allow LLVM to resolve the
type later.

## Artifact and backend compatibility

Some command combinations are intentionally invalid:

| Request | Expected result |
|---|---|
| AOT with bin | Linked executable if all source and native gates pass |
| AOT with cdylib | DLL and import library if wrappers pass C ABI admission |
| AOT with staticlib | COFF archive if wrappers and dependencies pass |
| AOT with solib | Versioned container if its payload set passes validation |
| ORC with bin and emit=jit | Execute through the native guest process |
| ORC with cdylib, staticlib, or solib | Reject; ORC is an execution mode |
| ORC with emit=link | Reject; it does not create a persistent linked artifact |
| ORC JIT with -o or --out-dir | Reject; JIT has no output path |

The command parser validates incompatible option combinations before output creation. A request
for ORC plus a library does not invoke AOT behind the user's back. A target mismatch, missing
backend dependency, invalid source, failed verification, or link failure must not overwrite an
existing artifact.

The compiler still requires the separately built native backend component for native artifact work.
Frontend inspection modes remain distinct from code generation. FIR JSON is an inspection
serialization; it is not a codegen input format. LLVM text output is printed from a constructed
LLVM module for inspection; it is not parsed back as the compile pipeline.

## Export and visibility examples

The following relationships are intentional:

| Source relationship | bin | cdylib/staticlib | solib |
|---|---|---|---|
| Main calls identity<int> | Private reachable helper | Not relevant unless wrapper root reaches it | Internal payload only if schema permits |
| Concrete wrapper calls identity<int> | Ordinary reachable call | Private helper behind concrete wrapper | No generic consumer API |
| identity<T> is public in source | Not an entry by itself | Not a generic C export | Not an instantiable template |
| Consumer asks for identity<int> from artifact | Not a source compilation case | No C generic ABI | Unsupported external instantiation |
| Unused generic declaration | No instance required | No wrapper root; no retained helper required | Metadata does not authorize a body |
| Generic function stored as a value | Unsupported in current direct-call slice | Requires separate closure/call ABI | Requires separate serialized representation |

Public source visibility and artifact visibility are different concepts. A public source declaration
can appear in language metadata without becoming an exported native symbol. Conversely, an
artifact's export table contains only names admitted by its ABI contract.

The C symbol rule is checked on wrapper exports after source overload resolution. Generic arguments
are not a fallback mangling mechanism for C. Two exported source functions that collide after
wrapper naming are an artifact error, even if their Sol signatures differ.

## Verification evidence by phase

When reviewing an artifact result, record which phase the evidence covers:

| Phase | Evidence | Does not establish |
|---|---|---|
| Source parse | CST/PSI/AST exists without syntax errors | Bound names or concrete generic arguments |
| Source analysis | Selected call binding and ordered type tuple | Native type support or verified FIR |
| FIR build | Monomorphic function graph was constructed | Independent verification or LLVM validity |
| FIR verifier | Source-bound signatures, effects, calls, and flow passed | Correct LLVM lowering or linking |
| LLVM verification | Constructed module passed LLVM verification | Correct runtime ABI or program output |
| Object emission | Target object or bitcode was emitted | Complete symbol closure |
| Link/package | Staged artifact and companions committed | Runtime behavior or external ABI qualification |
| Execution | One selected program completed through a mode | General language, ABI, or platform guarantees |
| Runtime qualification | Named runtime scenario passed | Full Sol or GC correctness |

A statement that generic support is implemented should name the exact phase and admitted source
subset. “The call inferred T” does not mean “the native generic feature is complete.” A golden
FirJson snapshot can show metadata shape but does not independently prove the verifier. A successful
link cannot substitute for AOT/ORC runtime comparison.

## Diagnostics and failure stages

Current diagnostics separate several kinds of failure:

| Code | Meaning at this boundary |
|---|---|
| SOL2400 | Source shape is outside the verified native subset |
| SOL2401 | Bin entry contract is not satisfied |
| SOL2402 | FIR verification found inconsistent proof metadata |
| SOL2403 | Generic specialization resource limit was exceeded |

Source errors retain their own source-analysis diagnostic codes. A failed inference should not be
misreported as a linker problem. A closed tuple whose type is not admitted can be source-valid but
native-unsupported. A caller cannot hide such a failure by deleting the FIR call edge or emitting a
stub.

Resource diagnostics currently include limits for distinct function instances, copied generic body
AST nodes, and nested type depth. The limits are per compilation. They protect compilation work;
they are not language syntax limits. A larger limit does not enable a type or operation that is
otherwise unsupported.

On cancellation, the driver observes cancellation at the documented managed/native boundaries.
Embedded native linking may not be forcibly interrupted at every instruction. The compiler must
not report a successful commit when cancellation or a later companion failure leaves an incomplete
output set.

## Determinism and reuse

The specialization key is based on canonical declaration identity and the ordered tuple of
canonical closed type identities. Source path, username, call-site span, candidate enumeration
position, and display type spelling are not key fields.

Within one compilation, repeated requests for the same key reuse one logical instance. Calls with
different tuples remain distinct. A compiler may assign local integer IDs, but those numbers can
change when source declarations or deterministic traversal order change. They are never suitable
for external ABI.

The current implementation does not promise a persistent specialization cache. Any future cache
must include all source and imported dependency digests, compiler semantic identity, edition and
features, target ABI, LLVM/backend version, runtime identity, artifact policy, and codegen options
that affect the result. A digest hit is only a lookup hint; the cache record must match the full
canonical key and input content.

Changing only an output directory does not change source binding or specialization identity.
Changing target, runtime ABI, compiler semantics, or ABI-relevant artifact policy invalidates
native-object and linked-output cache entries.

## Container compatibility and trust

FIR serialization and the solib manifest are separate formats. FirJson schema 4 records ordered
function and call type arguments. The current solib manifest is schema 2 and independently pins
its compiler, target, runtime, and payload compatibility. Updating one schema does not imply that
the other schema changed.

A consumer must reject an unsupported mandatory schema version. It must not guess that an unknown
field is optional. It must validate each path and payload before extracting or forwarding the data.
Windows case-insensitive path collisions and traversal names are rejected.

The manifest's digest is an integrity field, not a signature. If a producer changes a payload and
also changes the digest, the reader has verified only the stated bytes. Authentication and publisher
trust require a separately specified signing and key policy.

Most importantly, data that was read from a solib does not gain the compiler's verification
capability. A consumer must bind or validate its own inputs through the defined compilation
pipeline. Producer-local type IDs cannot be adopted into another analysis workspace.

## Runtime and garbage collector ownership

Solc is a .NET 10 process. The native Sol program and Yellow Tea heap belong to a native runtime.
They are not one managed heap. AOT executables contain or import the native runtime according to
the selected linkage. ORC places the native runtime and generated code in a child guest process.

The generated code's root metadata and calls to SolRT are part of the native backend contract.
Static linking does not remove rooting, barriers, polling, or lifetime obligations. Dynamic linking
does not make the runtime's object layout stable. A generic instance over a reference type is
admitted only if the type layout, safe points, and root behavior are supported for that concrete
body.

GC tests, sanitizer runs, and runtime stress are separate evidence from FIR verification. A
successful generic FIR build does not claim Yellow Tea latency, completeness, or safety for every
object type.

## Developer workflow

Use these checks as phase boundaries when investigating a new generic artifact request:

1. Confirm the command parser selected the intended backend, artifact, and runtime linkage.
2. Confirm source analysis selected one target and recorded a complete ordered type tuple.
3. Inspect FIR JSON schema 4 for function SpecializationArguments and call TypeArguments.
4. Confirm the verifier binds each FIR expression and call back to the selected source analysis.
5. Check that graph roots match the requested bin entry or concrete library wrappers.
6. Confirm that no open generic type reaches an LLVM operation or exported symbol.
7. Verify LLVM 23.1.3 module validation before object emission or ORC transfer.
8. Inspect staged outputs and all required companions before the transaction commits.
9. For ORC, confirm guest-process ownership, binary bitcode framing, and the explicit symbol list.
10. For solib, validate the schema, full inventory, compatibility, and byte digests.
11. Record whether a later runtime run, native ABI consumer, or sanitizer qualification was actually
    performed.

These steps identify where evidence is needed. They do not assert that a particular build or
qualification has run.

## Current exclusions

The current bounded slice does not establish:

- specialization of generic owner/member layouts, constructors, accessors, or operators;
- per-instance complete rebinding and rechecking of every generic body;
- generic function values, closures, runtime type arguments, or reflective construction;
- every built-in type's native layout or C ABI;
- a stable public SolRT ABI or Yellow Tea object-layout version;
- a generic symbol ABI for DLLs, archives, or solib consumers;
- solib dependency resolution or external generic instantiation;
- generic specializations across independent module compilations;
- persistent specialization cache compatibility;
- full source exception unwinding in generated AOT/ORC code;
- OS sandbox guarantees for the ORC guest;
- production support merely because a normative example describes it.

These exclusions do not weaken the source-level language contract. They define the current backend
admission boundary.

## Status reporting

When describing progress, name both the feature and the furthest completed phase. For example:

- “Source binding retains the ordered type tuple” reports analysis metadata.
- “FIR builder creates closed identity<int>” reports construction.
- “The independent verifier accepts the selected instance” reports a verified FIR result.
- “AOT links the admitted bin” reports native link output.
- “The linked executable runs the input” reports one runtime execution.
- “Both AOT and ORC match for the named fixture” reports a specific parity observation.

Do not collapse these statements into a claim of complete generic support. The currently implemented
slice is direct, module-level, free-function specialization for closed supported types and bodies.
The complete normative semantics remain broader.

## Native consumer responsibilities

The artifacts serve different consumer models:

| Consumer | Input it consumes | Responsibility it retains |
|---|---|---|
| Windows process loader | AOT bin and imported DLLs | Load compatible runtime companions |
| Native C linker | cdylib import library or staticlib archive | Use declared symbol names and platform libraries |
| Sol compiler | Source snapshots and, in a future contract, imported Sol modules | Resolve source modules and verify its own FIR |
| solib reader | Versioned container data | Validate schema, paths, compatibility, and payloads |
| ORC guest | Binary bitcode and allowlisted runtime imports | Execute generated code in the guest process |

A C consumer does not participate in Sol overload resolution. Its declaration must match the
concrete wrapper ABI: integer width and signedness, floating width, calling convention, pointer
mode, and return convention. The current wrapper set does not provide a general object, String,
array, nullable, or generic representation.

For a cdylib, the import library names the deployed DLL basename. Renaming the DLL independently
can break loading even when the import library still links. The deployment set includes the runtime
dependency required by the selected linkage. Solc stages the DLL, import library, and runtime
companions together.

For a staticlib, the archive is not proof that all dependencies were absorbed. Consumers still
provide the required MSVC/Windows libraries and the selected runtime closure. A dynamic-runtime
archive still depends on the runtime DLL. A static-runtime archive changes runtime object placement,
not the wrapper ABI.

Consumers must not call compiler-private helper symbols. A private specialization helper may be
renamed, duplicated, removed, or have its ABI changed by another compiler build. Only declared
exports are part of the library boundary.

## Review checklist for generic artifact changes

Before accepting a change to generic artifact support, check each independent boundary:

1. Source overload resolution is unchanged by AOT, ORC, artifact type, or SolRT linkage.
2. Every selected generic call retains one complete declaration-ordered type tuple.
3. The specialization key uses canonical declaration and type identities, not display names.
4. Nested calls compose the caller substitution before constructing the callee key.
5. Every expression type and effect is checked against source analysis under that substitution.
6. Dependent operations are rebound for the concrete type or rejected.
7. Generic owners, members, function values, and external requests stay outside the admitted slice
   until their separate contracts are implemented.
8. Generic FIR instances remain private unless a concrete, versioned export contract says otherwise.
9. C wrappers expose concrete types only and preserve passing mode and evaluation order.
10. Solib records cannot be used to mint verified FIR or import a generic instance by local ID.
11. Root sets retain all required helpers and no unresolved candidate bodies.
12. Each artifact rejects unsupported types and effects before LLVM emission.
13. AOT and ORC receive semantically identical verified FIR.
14. LLVM module verification happens before and after optimization.
15. SolRT linkage is recorded and affects the staged link closure.
16. All companion files share one validated output transaction.
17. Resource limits and cancellation fail closed without emitting partial code.
18. Serialization and container schema changes are versioned independently.
19. Cache keys include compiler, source, target, runtime, backend, and artifact identity.
20. Evidence reports state whether the work reached analysis, FIR, verification, codegen, link,
    packaging, or execution.

This checklist is an implementation review aid. It does not replace source conformance, verifier
validation, native ABI qualification, runtime testing, or release checks.

## Native symbol and runtime boundary

An exported C name is selected by the wrapper naming policy, not by FIR's integer function ID.
The native module may use private symbols for internal calls, but those names are not promised to
remain stable across compiler revisions. Export collision detection occurs after wrapper names are
final.

The current runtime uses a provisional layout identity. A Sol object pointer is meaningful only in
the guest runtime process that owns its heap. Passing such a pointer to the CLR host or a C caller
without a specific handle ABI is unsupported. The same restriction applies to a generic type
descriptor that contains compiler-private or workspace-local pointers.

Generic reference types add runtime obligations beyond parameter width. The concrete layout must
be known; generated stores must publish references through the runtime barrier; roots must stay
visible at safe points; and object lifetime must remain valid through native calls and exceptions.
The current subset admits only the type and body shapes for which those obligations are already
represented. It does not generalize every successful scalar instance to arbitrary objects.

An exception effect is also part of the function identity and call graph. A C wrapper cannot expose
a checked Sol exception as an uncontrolled C++ exception. Native unwinding and error translation
must be established before such a wrapper can be admitted. Generic substitution must preserve the
selected effect set rather than omit an effect to satisfy an export.

## Release and support status

The CLI can describe artifact and backend selections before every combination is implemented in a
published build. Help text, an accepted option, or a local development binary does not establish a
release promise by itself.

Release availability is determined by the versioned release manifest, packaged files, tested
installation path, native component compatibility, and release notes. A release statement should
identify the compiler version, target, supported artifact, runtime linkage, and source subset.

The 0.0.1 release remains the initial frontend component. The artifact and generic FIR behavior in
this guide describes current development work; it must not be attributed to 0.0.1 without published
release evidence.

## What each format proves

| Data or output | What it can establish | What it cannot establish |
|---|---|---|
| Source declaration | Declared module and callable syntax | Native symbol availability |
| FIR JSON | Inspection of a particular FIR snapshot | Trusted compiler capability or ABI |
| LLVM printer output | Readable view of a constructed module | Validity of an arbitrary text module |
| COFF object | Target-specific native code payload | Complete runtime or import closure |
| DLL and import library | Exported C wrappers and link names | Sol generic import semantics |
| Static archive | Packaged COFF members | Self-contained runtime or freestanding behavior |
| solib manifest | Declared schema metadata and payload digests | Publisher identity or verified generic template |
| ORC process exit | One guest execution result | OS sandboxing or universal runtime correctness |

Keep these claims narrow in documentation and release notes. The compiler's phases deliberately
produce distinct evidence so that a successful output operation does not mask an unsupported
source feature.

## Diagnosing a rejected request

Start by locating the earliest failed phase:

| Observation | First place to inspect | Typical next question |
|---|---|---|
| Option error | Parsed CLI options | Is the backend/artifact/emit combination legal? |
| Unresolved or ambiguous call | Source diagnostics and candidate set | Did source rules produce one winner? |
| Inference failure | Generic call binding and evidence | Is there a complete closed tuple? |
| SOL2400 | FIR builder native admission | Which operation, type, or body shape is outside the subset? |
| SOL2401 | Entry-point validation | Is there exactly one supported Main.main? |
| SOL2402 | Independent FIR verifier | Which source binding or FIR field disagrees? |
| SOL2403 | Specialization budgets | Which instance, body-copy, or type-depth bound was reached? |
| LLVM failure | Native module construction/verification | Did typed lowering preserve the verified signature? |
| Link failure | Object and symbol closure | Which required symbol or runtime companion is missing? |
| solib rejection | Manifest/archive validation | Which schema, path, digest, or compatibility field failed? |
| Runtime difference | Native process and runtime logs | Do AOT/ORC and linkage use the same selected graph? |

For source-level generic behavior, inspect the selected call binding before inspecting output files.
It should identify one declaration and one ordered type tuple. If that binding is absent, there is
no specialization request yet.

For FIR behavior, inspect both the specialized function and the originating call. The function's
SpecializationArguments and the call's TypeArguments must agree after composing the caller
substitution. Verify the target declaration identity as well as the displayed name.

For export behavior, inspect the wrapper/export table instead of searching for a private helper
name. A helper can be present in the object graph and still be intentionally absent from the
consumer-facing symbol table.

For solib behavior, separate structural validation from compatibility and code verification. A
reader may prove that an archive is well-formed and its digests match while still refusing to use it
as a source module or generic template.

For runtime behavior, record the artifact type, backend, linkage flag, target, compiler identity,
and exact source input. Comparing two runs without recording these inputs can conflate distinct
runtime closures or output contracts.
