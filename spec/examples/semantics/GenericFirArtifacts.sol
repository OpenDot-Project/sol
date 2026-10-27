// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative artifact matrix; rows distinguish current development behavior from future obligations.
module spec.genericfir.artifacts

import std.stdio

// Generic FIR specialization at artifact boundaries
// ==================================================
//
// This fixture supplements spec/GenericFirArtifacts.sol. It is a review matrix, not a test-run
// report. Each case identifies a phase; source binding success is not equivalent to FIR success,
// and FIR success is not equivalent to native output or runtime qualification.
//
// Status labels:
//
// - SourceValid means the language-level program has a valid source interpretation.
// - SourceRejected means source binding must reject the case before native admission.
// - FirEligible means the described shape belongs to the current supported FIR slice, subject to
//   the complete implementation checks.
// - FirRejected means the verifier or current native admission must withhold verified FIR.
// - ArtifactRejected means source and perhaps FIR are valid, but the selected output boundary is
//   not supported.
// - NormativeFuture states a contract that does not claim current implementation support.
//
// Declarations used by the cases
// ------------------------------

func <T> identity(value: T): T = value

func <T> relay(value: T): T = identity(value)

func concreteInteger(value: int): int = identity(value)

func concreteString(value: String): String = identity(value)

func concreteNested(value: int): int = relay(value)

func add(left, right: int): int = left + right

object Main {
    func main(args: []String) {
        println(identity(42))
        println(relay("Sol"))
        println("Argument count: ", args.length)
    }
}

// A. Command selection does not change source semantics
// -----------------------------------------------------
//
// case: default-executable-selection
// source: Main.main is the sole entry; identity<int> and relay<String> are selected by ordinary
//         call binding.
// mode: --artifact-type=bin -C llvm-compiler=aot
// expected: SourceValid; FirEligible when all body and type shapes meet the current verifier;
//           output is an AOT executable only after backend, linker, and runtime gates pass.
// note: This line describes the development contract, not the published 0.0.1 package.

// case: static-runtime-executable
// source: identical snapshots, declarations, overload set, and selected generic arguments.
// mode: --artifact-type=bin --static-solrt-lib -C llvm-compiler=aot
// expected: same source bindings and specialization keys as default linkage; runtime closure is
//           statically linked; output remains a bin.
// invariant: the linkage flag cannot turn identity<String> into identity<Object>.

// case: orc-executable
// source: identical snapshots and source-selected FIR.
// mode: -C llvm-compiler=orc -- Alice 20
// expected: same language behavior for admitted FIR; separate guest process; no persistent output.
// invariant: application words after -- are guest arguments, not generic type arguments.

// case: library-type-does-not-change-resolution
// source: two overloads can be source-selected before output admission.
// mode: --artifact-type=cdylib
// expected: overload identity unchanged; unsupported export shape is an artifact diagnostic.
// invariant: no alternate overload is selected because one wrapper is easier to emit.

// case: orc-library-selection
// source: the same source set as the AOT library request.
// mode: -C llvm-compiler=orc --artifact-type=cdylib
// expected: command-line rejection; ORC execution does not produce a DLL or import library.
// invariant: the driver does not silently replace orc with aot.

// case: orc-link-output
// source: an otherwise supported executable.
// mode: -C llvm-compiler=orc --emit=link -o out/app.exe
// expected: command-line rejection; ORC JIT mode has no persistent output path.
// invariant: output path presence cannot change backend selection.

// case: default-backend
// source: an otherwise supported bin compilation without -C llvm-compiler.
// mode: --artifact-type=bin
// expected: AOT backend selection.
// invariant: defaulting is a CLI rule and does not claim that LLVM dependencies are installed.

// B. Closed instance identity and sharing
// ---------------------------------------
//
// case: one-integer-instance
// call: concreteInteger(7)
// selected: identity<int>
// expected: one closed instance key; source declaration identity plus the ordered tuple [int].
// fir: FirFunction.SpecializationArguments=[int]; call TypeArguments=[int].

// case: same-instance-two-call-sites
// call-sites: identity(1), identity(2)
// selected: identity<int> at both origins.
// expected: one logical function instance and two exact call edges.
// invariant: call-site span is provenance, not part of instance identity.

// case: two-concrete-instances
// call-sites: identity(1), identity("x")
// selected: identity<int> and identity<String>.
// expected: two distinct instances, each with a concrete signature.
// invariant: equal source spelling does not erase distinct type identity.

// case: nested-call-composition
// call: relay(7)
// source target: relay<int>
// nested target: identity<int>
// expected: outer substitution is composed before the inner specialization key is formed.
// fir: relay<int> has a call to exactly identity<int>.

// case: generic-call-inside-ordinary-wrapper
// declaration: concreteInteger
// body: identity(value)
// expected: caller substitution is empty; callee inference has already produced [int].
// fir: wrapper signature stays int -> int; target instance is identity<int>.

// case: explicit-argument
// call shape: identity<int>(7)
// expected: explicit argument is checked under ordinary source rules and stored at ordinal zero.
// note: Explicit syntax does not bypass constraints, conversions, or ABI admission.

// case: source-default-type-argument
// declaration: no source defaults are added by backend specialization.
// expected: missing type evidence remains a source inference result, not a guessed ABI type.

// case: name-does-not-identify-a-specialization
// declarations: two overloads named identity with distinct declaration owners.
// expected: keys differ by canonical declaration even if the selected argument tuple is equal.
// forbidden: using the text "identity<int>" alone as the unique cache key.

// case: source-order-independence
// input: same explicit source snapshots enumerated in different filesystem orders.
// expected: source declarations retain semantic module identity; instance identity does not depend
//           on absolute checkout path or username.
// future: serialized ordering should be deterministic after canonical declaration sorting.

// case: type-id-from-other-compilation
// mutation: replace a local type identity with a same-numbered identity owned by another workspace.
// expected: FirRejected(SOL2402); a numeric ID coincidence is not type ownership.

// case: duplicate-specialization-record
// mutation: duplicate identity<int> with a different FIR function index but the same source owner.
// expected: FirRejected(SOL2402); only one canonical key is permitted in one graph.

// C. Generic constructs that do not cross an ABI by themselves
// ------------------------------------------------------------
//
// case: unused-generic-declaration
// declaration: identity<T> with no selected reachable call.
// expected: no closed instance is required solely because the declaration exists.
// artifact: no generic machine symbol is created for an unreachable declaration.

// case: call-in-dead-ordinary-function
// declaration: an ordinary function never reachable from Main or an export calls identity<int>.
// expected: source binding may record the call; artifact liveness is determined from roots.
// note: Retaining ordinary functions for debugging cannot create an open generic body.

// case: open-caller-parameter
// declaration: relay<T> calls a nongeneric function whose parameter type depends on T.
// expected: NormativeFuture until per-instance source analysis proves the concrete call.
// current: detected dependent operations and member lookups are rejected from the native subset.

// case: generic-member
// declaration: a class method declares its own type parameter.
// expected: SourceValid may hold; current FIR specialization rejects generic members.
// reason: member owner, dispatch, layout, and receiver ABI need a separate proof.

// case: generic-owner
// declaration: object or class owner declares T and has a method using T.
// expected: SourceValid may hold; current native FIR subset rejects the generic owner.
// reason: substituting a free-function signature cannot establish object layout or dispatch.

// case: generic-constructor
// declaration: constructor requires a type argument for its containing type.
// expected: NormativeFuture; no constructor or generic object-layout lowering is implied.

// case: generic-property-accessor
// declaration: getter or setter uses an owner or method type parameter.
// expected: NormativeFuture; property reflection and accessor dispatch need their own ABI.

// case: generic-operator
// declaration: operator behavior depends on T after substitution.
// expected: source binding is distinct from specialization; current dependent-operation gate
//           rejects until the selected operator is rebound and verified for the concrete type.

// case: generic-function-value
// expression: val f = identity
// expected: no runtime instance request; function values and closures need a separate representation.

// case: generic-reflection-query
// expression: reflective lookup requests identity<T> with a runtime T.
// expected: NormativeFuture; reflection cannot bypass compile-time closedness or ABI selection.

// case: local-generic-function
// declaration: a function nested inside a block declares its own T.
// expected: outside the current module-level-only specialization boundary.

// case: unresolved-overload
// source: two applicable generic overloads remain equally ranked.
// expected: source ambiguity; no specialization key is allocated for either candidate.

// case: rejected-candidate-has-cheaper-backend
// source: one overload is native-supported and the better source overload is not.
// expected: source selection is performed first; backend capability cannot choose the other overload.

// D. Artifact root matrix
// -----------------------
//
// +--------------+----------------------------+-----------------------------------------------+
// | Artifact     | Generic root               | Result                                        |
// +--------------+----------------------------+-----------------------------------------------+
// | bin          | Main.main and direct calls  | Internal closed instances may be retained    |
// | cdylib       | Admitted C wrappers         | Closed internal helper may be private         |
// | staticlib    | Admitted C wrappers         | Helper may be archived as private object      |
// | solib        | Manifest module roots       | Schema controls payloads; no generic import  |
// | ORC bin       | Main.main in guest          | Same verified graph, separate process         |
// +--------------+----------------------------+-----------------------------------------------+
//
// case: generic-bin-helper
// source: Main calls identity<int>; identity contains only admitted operations.
// expected: FirEligible, then independent target and signature verification.
// output: bin only after LLVM, linker, runtime, and transaction success.

// case: generic-helper-behind-export
// source: one concrete exported wrapper calls identity<int>.
// expected: the wrapper is the external C symbol; identity<int> may be private implementation.
// boundary: the C caller cannot ask for identity<T> or choose T.

// case: generic-function-as-export
// source: generic identity declaration is public and directly exported.
// expected: ArtifactRejected unless a future explicit ABI publishes a concrete wrapper for it.
// forbidden: export one symbol whose implementation chooses a type at runtime.

// case: closed-instance-symbol
// output: generated internal symbol may encode a compiler-private unique identity.
// expected: symbol stays private and is scoped to this artifact build.
// forbidden: promise that mangled specialization spelling is a stable import-library ABI.

// case: direct-c-call-of-specialized-instance
// consumer: native C code looks up identity<int> by a guessed mangled name.
// expected: ArtifactRejected; no public export contract exists for the helper.

// case: generic-archive-member
// source: staticlib wrapper calls identity<int>.
// expected: archive may contain the helper object if reachability and ABI checks pass.
// consumer: sees only documented wrapper symbols and archive dependencies.

// case: dynamic-library-helper
// source: cdylib wrapper calls identity<String>.
// expected: current C scalar ABI rejects String wrapper shapes; generic helper presence cannot
//           widen the C ABI.

// case: unused-generic-helper-in-library
// source: exported nongeneric wrapper does not reach identity<T>.
// expected: no identity instance required in that library's retained graph.

// E. C ABI admission matrix
// -------------------------
//
// The following outcomes are normative boundaries. Current support is narrower than source
// generic validity and must be read together with docs/library-artifacts.md.
//
// +----------------------------------+-----------------------------------------------+
// | Wrapper shape                    | Expected artifact admission                    |
// +----------------------------------+-----------------------------------------------+
// | add(int, int): int               | Current scalar C wrapper candidate             |
// | update(ref int, int): ()         | Candidate when exact pointer mapping is proven |
// | read(out double): ()             | Candidate when out definite assignment is proven|
// | identity<T>(T): T                | Rejected as a generic C entry                  |
// | String -> String                 | Rejected by current C wrapper boundary         |
// | []int -> []int                   | Rejected by current C wrapper boundary         |
// | int? -> int?                     | Rejected pending nullable ABI layout           |
// | half -> half                     | Rejected by current scalar ABI                 |
// | quad -> quad                     | Rejected by current scalar ABI                 |
// | checked exception escapes        | Rejected without a C unwind contract          |
// +----------------------------------+-----------------------------------------------+
//
// case: concrete-wrapper-with-generic-body
// source: func normalize(value: int): int = identity(value)
// expected: source wrapper has a concrete C signature; internal instance may be private.
// condition: selected call and complete transitive body pass current verified FIR admission.

// case: C-overload-name-collision
// source: two wrappers map to the same final C export name.
// expected: ArtifactRejected; erasure cannot make duplicate C symbols unambiguous.

// case: ref-pointer-reinterpretation
// source: ref short passed to a wrapper admitted only for ref int.
// expected: ArtifactRejected; no implicit pointer cast, box, or storage widening.

// case: generic-argument-does-not-fix-export
// source: identity<int> exists internally.
// expected: still no external export; concrete internal type arguments are not ABI declarations.

// case: C-consumer-controls-generic-type
// source: foreign caller supplies a descriptor or integer tag selecting T.
// expected: rejected without a separately versioned runtime generic ABI.

// F. solib producer/consumer matrix
// ---------------------------------
//
// case: closed-nongeneric-module-metadata
// producer: serializes supported public nongeneric declarations.
// expected: schema-2 module metadata can describe only its specified fields.
// consumer: validates compatibility and names before exposing checked data.

// case: generic-declaration-metadata
// producer: module contains identity<T>.
// expected: declaration presence does not authorize a consumer instantiation.
// consumer: cannot ask the producer artifact for identity<SomeConsumerType>.

// case: generic-instance-id-import
// producer: FIR assigns local function ID 12 to identity<int>.
// consumer: has an unrelated function ID 12.
// expected: no identity relation; IDs are compilation-local.

// case: closed-helper-packaged-private
// producer: closed helper is transitively needed by a permitted payload.
// expected: current schema 2 rejects generic FIR instance payloads; future schema may admit a
//           private closed helper with a declared object/runtime closure.
// invariant: any future payload is not relabeled as a consumer-instantiable generic export.

// case: unversioned-generic-record
// mutation: add type parameter records to schema 2 without changing the schema version.
// expected: reject; an older reader cannot infer the new meaning.

// case: reader-trusts-verified-flag
// mutation: serialized FIR says verified=true but has forged call target.
// expected: reader returns untrusted data or rejects; it cannot mint VerifiedFirProgram.

// case: digest-mismatch
// mutation: change one payload byte without updating its declared digest.
// expected: container rejection before exposing the payload to compilation.

// case: digest-updated-by-producer
// mutation: change payload and recompute a manifest digest.
// expected: integrity check alone does not authenticate the publisher or prove code safety.

// case: traversal-entry
// archive: contains ../outside.obj or a case-colliding path.
// expected: reject without writing outside the selected staging directory.

// case: external-generic-instantiation
// producer: emits an open identity<T> body only.
// consumer: requests identity<int> without producer source.
// expected: ArtifactRejected until a future versioned generic export and verification contract.

// case: missing-transitive-symbol
// producer: records a helper call but omits helper body or declared external dependency.
// expected: linking/validation failure; consumer cannot invent a body by name.

// G. SolRT linkage does not change generic identity
// ------------------------------------------------
//
// +--------------+----------------------+-------------------------------+----------------------+
// | Artifact     | Linkage option       | Runtime ownership             | Type tuple impact    |
// +--------------+----------------------+-------------------------------+----------------------+
// | AOT bin      | default              | process imports SolRT.dll     | none                 |
// | AOT bin      | --static-solrt-lib   | process contains runtime      | none                 |
// | cdylib       | default              | DLL deployment includes SolRT | none                 |
// | staticlib    | declared linkage     | native consumer closes deps   | none                 |
// | ORC bin      | selected guest mode  | separate guest process        | none                 |
// +--------------+----------------------+-------------------------------+----------------------+
//
// case: generic-runtime-link-mode-pair
// builds: same source once with default linkage and once with --static-solrt-lib.
// expected: both select identity<int>; runtime packaging differs.
// invariant: generic function IDs do not enter source-visible behavior.

// case: host-and-guest-heap
// process: Solc CLR host launches native ORC guest.
// expected: host GC and Yellow Tea do not exchange roots or object pointers.
// invariant: dynamic/static SolRT choice remains inside the guest process.

// case: archive-with-dynamic-runtime
// consumer: native program links the generated staticlib.
// expected: consumer follows the declared runtime DLL and system library requirements.
// forbidden: claim that the archive is self-contained merely because it has .lib extension.

// case: runtime-abi-mismatch
// container: manifest pins runtime layout version 0; consumer has an incompatible runtime.
// expected: reject before link or execution.
// invariant: static linkage cannot erase the compatibility check.

// H. Failure and no-partial-output cases
// --------------------------------------
//
// case: unresolved-generic-argument
// source: generic call has no unique closed substitution.
// expected: source diagnostic or deferred result according to inference rules; no instance ID.

// case: unsupported-concrete-body
// source: the selected generic body contains a type-dependent member whose analysis was not rerun.
// expected: current native admission rejects; no empty body is substituted.

// case: native-type-not-supported
// source: identity<half> or identity<quad> reaches an unqualified operation.
// expected: SOL2400 or a more specific native capability diagnostic; no LLVM module commit.

// case: generic-depth-limit
// source: nested concrete type graph exceeds the current maximum depth.
// expected: SOL2403 with a useful origin; no truncated type argument.

// case: generic-instance-limit
// source: compilation would create more than 4,096 distinct instances.
// expected: SOL2403; no partial artifact is published.

// case: specialized-body-budget
// source: aggregate copied generic body nodes would exceed 16,000,000.
// expected: SOL2403; no body is replaced with an empty successful function.

// case: invalid-fir-metadata
// mutation: function or call has open, duplicate, null, or foreign type arguments.
// expected: verifier rejects with SOL2402; backend receives no verified capability.

// case: verifier-target-tamper
// mutation: generic FirCall target points at an overload with the same name but a different source
//           declaration or type tuple.
// expected: SOL2402 even when argument and result machine types happen to match.

// case: LLVM-verifier-failure
// stage: native module construction or post-optimization verification fails.
// expected: failure before link/package commit; an earlier valid file remains.

// case: companion-link-failure
// stage: DLL succeeds in staging but import library or SolRT companion fails.
// expected: no mixed old/new output set is reported as committed.

// case: cancellation-during-expansion
// stage: cancellation arrives during specialization worklist processing.
// expected: stop promptly, preserve source identity, publish no partial binary.

// case: cancellation-during-native-link
// stage: an embedded native linker operation cannot be forcibly interrupted.
// expected: driver reports cancellation only at the documented boundary and does not claim an
//           unverified output set was committed.

// I. Determinism and reproducibility
// ----------------------------------
//
// case: repeated-build-same-inputs
// inputs: same source snapshots, compiler identity, target, backend, artifact, runtime linkage,
//         and native dependencies.
// expected: deterministic specialization traversal and deterministic container ordering where
//           the backend/toolchain contract guarantees it.

// case: call-discovery-order
// input A discovers identity<int> before identity<String>.
// input B discovers identity<String> before identity<int>.
// expected: equal logical instance identities; serialized ordering is canonicalized independently.

// case: source-path-move
// change: same module and declaration moved under a different conventional directory.
// expected: source module identity follows the declaration, not absolute path or checkout root.

// case: compiler-version-change
// change: compiler semantic identity changes.
// expected: persistent native cache and solib compatibility are reevaluated; old machine code is
//           not reused solely because specialization display strings match.

// case: target-change
// change: target ABI changes.
// expected: native object and link cache entries are invalidated; source overload identity stays
//           governed by source semantics.

// case: output-directory-change
// change: only -o directory changes.
// expected: source specialization identity does not change.

// J. Reporting requirements
// -------------------------
//
// A conformance report distinguishes:
//
// - source parse and binding;
// - exact selected generic declaration and closed type tuple;
// - FIR construction and independent verification;
// - LLVM construction and verification;
// - object or bitcode emission;
// - link or container commit;
// - load or process execution;
// - runtime behavior and native import inspection.
//
// A source example cannot claim AOT/ORC parity without running both paths. A serialized schema
// example cannot claim reader conformance without exercising an implementation. A digest example
// cannot claim publisher authentication.
//
// The 0.0.1 release is the initial frontend component. Development artifact behavior described
// here is not a release claim.

// K. Path handling and output-set commit
// --------------------------------------
//
// case: bin-extension
// request: --artifact-type=bin -o out/app.dll
// expected: reject the conflicting primary extension before replacing existing files.

// case: cdylib-extension
// request: --artifact-type=cdylib -o out/library.bin
// expected: reject; the DLL and its import library must agree on the primary basename.

// case: solib-extension
// request: --artifact-type=solib -o out/library.solib
// expected: primary output name matches the container kind; staged payload validation still runs.

// case: output-overwrites-source
// request: output path resolves to one of the explicit .sol source snapshots.
// expected: reject during complete preflight; source is unchanged.

// case: companion-collision
// request: import library or SolRT companion path collides with another requested output.
// expected: reject the output set before commit; no companion overwrites another artifact.

// case: previous-output-present
// setup: a valid prior executable exists at the requested destination.
// failure: a later generic verifier or link phase fails.
// expected: preserve the prior output; do not report partial success.

// case: multiple-source-generic-graph
// input: multiple explicit source files in one compiler invocation, with one selected generic call.
// expected: one source compilation graph; the declared modules and source origins remain distinct.
// invariant: filesystem enumeration order does not create different overload resolution.

// case: same-basename-cst
// input: two CST-only files share a basename.
// expected: per-file CST output collision is a CLI/output error; this does not change how a
//           combined FIR/native compilation treats its explicit sources.

// case: source-changes-during-build
// event: a source snapshot changes after preflight but before output commit.
// expected: abort or revalidate according to the transaction contract; do not claim the artifact
//           corresponds to an input snapshot that was not compiled.

// case: staging-directory-isolation
// event: native backend writes an intermediate file or solib extraction candidate.
// expected: keep temporary paths within the selected staging root; do not place guest-controlled
//           archive paths directly in the output tree.

// case: partial-companion-rollback
// event: primary DLL is staged but import-library emission fails.
// expected: previous primary and companions remain as one consistent set.

// case: rollback-failure
// event: an ordinary filesystem error prevents complete restoration of prior companion files.
// expected: report incomplete rollback explicitly; do not report a successful transaction.

// L. Versioning and reader compatibility
// --------------------------------------
//
// case: FIR schema-four-reader
// input: serialized verified FIR inspection output containing ordered function and call type args.
// expected: a schema-aware reader recognizes schema 5; inspection alone is not trusted codegen.

// case: FIR schema-three-reader
// input: consumer only understands schema 3.
// expected: reject or report unsupported schema; it cannot silently ignore generic identity fields.

// case: solib-schema-two-reader
// input: manifest schema 2, supported compiler/target/runtime tuple, valid payloads.
// expected: structural and integrity validation only; not an external generic linker.

// case: future-schema-mandatory-field
// input: manifest marks a field required but the current reader does not understand its schema.
// expected: reject before reading code or extracting payloads.

// case: llvm-version-mismatch
// input: object or bitcode declares a compiler/backend identity not compatible with LLVM 23.1.3.
// expected: reject compatibility rather than guessing that the bytes are interchangeable.

// case: runtime-layout-mismatch
// input: payload expects a different runtime layout identity.
// expected: fail compatibility checks before linking or execution.

// case: module-name-match-only
// input: producer and consumer declare the same module name but differ in compiler or runtime ABI.
// expected: module name alone does not prove artifact compatibility.

// case: library-filename-match-only
// input: two DLLs share a basename but have different export and runtime contracts.
// expected: basename alone does not prove that an import library or cached object is compatible.

// case: option-change-rebuild
// change: AOT output switches from dynamic to static SolRT.
// expected: regenerate ABI-relevant objects/link output; source-selected generic tuple remains.

// case: debug-inspection-change
// change: user requests FIR inspection output for the same source.
// expected: report the selected FIR schema and identities; do not infer that an executable was linked.

// case: inspection-text-is-not-input
// input: saved LLVM printer output is supplied where the compiler expects Sol source.
// expected: no special LLVM parser path is implied by this artifact contract.

// M. Capability statements
// ------------------------
//
// case: positive-source-only
// report: "The generic call is source-bound."
// sufficient-evidence: source analysis records one selected declaration and closed tuple.
// not-established: verified FIR, LLVM output, artifact commit, runtime behavior.

// case: positive-fir
// report: "The generic instance is verified."
// sufficient-evidence: verifier accepts signature, effects, body bindings, constraints, and edges.
// not-established: LLVM correctness, link success, ABI compatibility, or runtime behavior.

// case: positive-link
// report: "The AOT artifact linked."
// sufficient-evidence: target object closure and linker completed into the staged artifact set.
// not-established: run-time behavior on every supported Windows environment.

// case: positive-orc-run
// report: "The ORC fixture ran."
// sufficient-evidence: one named source input executed through the separate guest process.
// not-established: ORC sandboxing, generic language completeness, AOT/ORC parity for other cases.

// case: release-availability
// report: "Sol version X ships this artifact support."
// sufficient-evidence: release manifest, published files, and corresponding release notes.
// not-established: development-only docs or local build output.
