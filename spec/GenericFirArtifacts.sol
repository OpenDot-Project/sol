// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative artifact contract; current native support is a development subset.
module source.spec.generic_fir_artifacts

// Generic FIR instances and native artifact boundaries
// =====================================================
//
// This file defines the relationship between a closed generic FIR instance and a Sol native
// artifact. Source validity, FIR admission, backend lowering, linking, and container export are
// distinct decisions. A valid generic call does not imply that every requested output can contain
// it.
//
// The clauses below are normative for compiler implementations. MUST, MUST NOT, SHOULD,
// SHOULD NOT, and MAY have their ordinary standards meaning. A status note cannot weaken a
// normative clause. If a required invariant cannot be established, the compiler rejects before
// publishing output.
//
// Implementation status:
//
// - Development builds produce Windows x86-64 AOT output for a verified bootstrap subset, run an
//   eligible bin through a process-isolated ORC guest, and support scalar C ABI libraries plus a
//   closed-subset solib container.
// - The current FIR slice specializes direct calls to module-level generic free functions when
//   arguments are closed and supported. It preserves ordered type arguments, builds monomorphic
//   bodies, and verifies the selected declaration, types, constraints, effects, and call targets.
// - Current body specialization substitutes already-bound facts. It does not repeat complete
//   source body and overload analysis for every concrete type. Generic owners and members, generic
//   function values, type-dependent operations, unsupported native types, and external generic
//   instantiation remain outside the implemented boundary.
// - A generic declaration is not automatically a C export or stable solib symbol. Internal use of
//   a closed instance does not establish a cross-artifact ABI.
// - This status is a capability description, not a claim that every positive case in this file has
//   been built, linked, executed, or qualified.

// 1. Scope and terms
// -----------------
//
// A source compilation is one complete explicit set of Sol source snapshots plus compiler-owned
// declarations. Declared module identity is semantic; directory layout is conventional only.
//
// A generic declaration is a function with one or more type parameters. The implemented subset
// covered here is a module-level free function. A specialization is the pair of one canonical
// declaration identity and its complete ordered tuple of closed canonical type identities.
// Display names, parameter spellings, source spans, and worklist order are not identities.
//
// A verified generic instance is a FIR function bound to its source declaration, ordered type
// tuple, constraints, specialized signature and effects, argument mapping, and reachable call
// targets. A specialization graph is the finite closure of these instances reachable from the
// requested artifact roots.
//
// An artifact root is a function or exported symbol that makes code or data live. A bin has an
// entry point; a C library has admitted wrappers; a solib has declared modules and schema-permitted
// payloads. A container boundary is a handoff between independently compiled artifacts.
//
// A native ABI type is a closed source type with a supported target-specific value, storage,
// lifetime, and calling-convention representation. FIR type IDs are workspace-local identities;
// they are not native ABI names or stable serialized IDs.
//
// Artifact choice MUST NOT alter parsing, name resolution, overload ranking, inference,
// conversions, checked-effect analysis, or the selected source target. The compiler cannot prefer
// an overload because its implementation happens to be available in one backend.
//
// This contract does not define all generic language semantics, the complete SolRT ABI, a permanent
// object layout, a cross-module linker, ORC sandboxing, or a persistent specialization cache.
// Those require separate contracts.

// 2. Pipeline and proof boundary
// ------------------------------
//
// A native request follows this order:
//
// 1. Parse the explicit source set and retain stable source origins.
// 2. Resolve declarations, imports, overloads, constraints, and body bindings.
// 3. Select one source overload independently of backend availability.
// 4. Record selected generic type arguments in declaration order.
// 5. Construct and intern the reachable FIR specialization graph.
// 6. Independently verify every function, instance, call mapping, effect, and graph edge.
// 7. Apply output-specific root, export, and ABI rules.
// 8. Lower verified FIR through the LLVM 23.1.3 C++ API.
// 9. Verify the LLVM module and emit object or bitcode payloads.
// 10. Link or package into staging, validate companions, then commit the output set.
//
// Source inference is not FIR proof. A call binding marked Bound does not authorize an open generic
// body. A solib digest check does not construct verified FIR. Successful linking does not prove
// source semantics.
//
// A failure at any stage MUST NOT be repaired by dropping a call, effect, function, root, export,
// or unresolved type. Callers that require a failed instance are also invalid. A failed output set
// MUST NOT replace a previously committed set.
//
// Only the verifier may create the VerifiedFirProgram capability accepted by native lowering.
// Serialized JSON, a source binding, or a raw FIR object is insufficient. LLVM source text is
// inspection output; compilation MUST use typed operations through the C++ API bridge.

// 3. Artifact selections
// ----------------------
//
// Solc accepts:
//
//     --artifact-type=bin|cdylib|staticlib|solib
//
// The default is bin. This option selects an output contract, not a source-language mode.
//
// +--------------+----------------------+-----------------------------------------------+
// | Selection    | Primary result       | Roots and consumer boundary                  |
// +--------------+----------------------+-----------------------------------------------+
// | bin          | Windows PE executable| One Main.main entry and reachable calls      |
// | cdylib       | DLL plus import lib  | Supported exported C ABI wrappers            |
// | staticlib    | COFF archive         | Supported exported C ABI wrappers            |
// | solib        | Sol container        | Declared modules and allowed payloads        |
// +--------------+----------------------+-----------------------------------------------+
//
// The target configured today is x86_64-pc-windows-msvc. Requesting an unavailable target MUST
// fail before output commit.
//
// bin requires exactly one admissible non-generic Main object entry with main(args: []String): ().
// Its array contains process arguments after the executable path. The entry itself cannot be
// specialized.
//
// cdylib is a Windows DLL exporting only the admitted wrapper set. Its import library is a
// companion. Names must be unique after the documented C symbol rule; the import library must
// refer to the actual DLL basename.
//
// staticlib is a COFF archive containing admitted wrappers and their private object closure. It is
// not an executable or a self-contained freestanding binary. A native consumer provides the
// documented platform libraries and runtime closure.
//
// solib is a versioned Sol container. It is neither a C archive nor a DLL. A container extension
// alone does not implement source import, dependency resolution, external linking, or generic
// instantiation.
//
// An artifact-specific ABI rejection may follow valid source analysis. It MUST report the output
// boundary and MUST NOT silently select a different source overload.

// 4. Backend choices
// ------------------
//
// Solc accepts -C llvm-compiler=aot|orc; AOT is the default.
//
// +--------------+---------------------+-----------------------------------------------+
// | Backend      | Operation           | Permitted output                              |
// +--------------+---------------------+-----------------------------------------------+
// | aot          | Native link         | bin, cdylib, staticlib, solib when gated      |
// | orc          | JIT execution       | bin only; no persistent artifact path        |
// +--------------+---------------------+-----------------------------------------------+
//
// ORC defaults to --emit=jit, with program arguments following --. JIT has no output path, so -o
// and --out-dir are invalid. ORC with a library artifact or link emission is an option error; it
// does not secretly switch to AOT.
//
// ORC owns a separate native guest process containing LLVM JIT state and guest runtime state.
// Solc MUST NOT invoke guest function pointers inside the .NET process. Compiler CLR objects are
// not guest roots, and Yellow Tea objects are not CLR objects.
//
// Both backends consume the same verified FIR semantics. Target lowering, optimization, runtime
// resolution, and output packaging may vary only under an explicit backend contract. AOT
// availability cannot grant a source feature rejected by ORC's shared verifier.
//
// A process boundary is not an OS security sandbox. This contract does not authorize arbitrary
// guest imports or remove validation of pipe framing, child handles, cancellation, output size, or
// exit status.

// 5. Output paths and commit
// --------------------------
//
// With -o, the path names the primary artifact. Companion outputs derive from the selected
// contract and actual basename. A cdylib primary must use .dll, a solib must use .solib, a bin
// must use the executable extension, and a staticlib must use the selected archive extension.
// Conflicting extensions should be rejected before writing.
//
// --out-dir selects the directory for default names. Supplying -o selects its documented primary
// path; it does not make companion outputs independent transactions.
//
// The compiler stages all outputs, verifies target and companion consistency, then commits them as
// one set. A write, verifier, optimizer, linker, or container failure preserves prior outputs.
// Ordinary reported-failure rollback is required; crash-durable atomicity across all filesystems is
// not implied.
//
// All source and destination paths are preflighted. An output cannot overwrite a source. Native,
// FIR, LLVM, and linked modes treat multiple inputs as one explicit compilation; CST's per-file
// output behavior is separately defined.

// 6. Reachability and instance identity
// -------------------------------------
//
// Retained generic instances derive from selected calls reachable from output roots. A declaration
// that is never selected creates no machine-code instance. An unreachable ordinary body does not
// make its calls artifact-live.
//
// An implementation may retain extra ordinary functions for debugging or simpler layout, but it
// MUST NOT emit open generic bodies or instances reachable only through unresolved candidates.
//
// Each FIR call edge records the exact source-selected declaration and concrete ordered type tuple.
// The verifier rejects a different target even when another same-name overload has a similar
// machine signature.
//
// The same canonical declaration and same type tuple within one compilation produce one logical
// instance. Duplicate call sites share it. Different tuples stay distinct even when generated
// layouts happen to match.
//
// Type IDs are scoped to one analysis workspace. They cannot be a persistent cache key, public
// symbol, solib identity, or cross-process ABI. Digests may accelerate lookup only if the complete
// canonical key is checked before reuse.
//
// Instance identity includes canonical declaration identity and ordered closed type identities.
// A native-code cache additionally includes all semantic input digests, compiler identity, language
// edition/features, target ABI, LLVM/backend version, runtime ABI, artifact policy, and codegen
// options that change output.

// 7. Generic source and FIR admission
// -----------------------------------
//
// Only module-level generic free functions are current specialization candidates. The body must be
// present, return by value, and have a fully selected substitution. Every type argument must be
// closed, owned by the current workspace, and satisfy all declared constraints.
//
// Generic classes, structs, ref structs, interfaces, enums, objects, constructors, properties,
// accessors, operators, local functions, and generic method owners are outside this initial
// boundary. A non-generic member that calls a supported module-level function still goes through
// ordinary selected-call verification.
//
// A type parameter cannot enter LLVM as a placeholder. Any parameter left in a parameter, return,
// local, effect, array element, function type, or nested nominal argument leaves the instance open.
//
// For a generic FirFunction, SpecializationArguments contains exactly one closed identity per
// declaration parameter, in declaration order. For an ordinary function it is empty. For a generic
// FirCall, TypeArguments equals the source binding after the caller's substitution is composed.
//
// Independent FIR verification checks:
//
// - canonical source declaration identity and generic arity;
// - type workspace ownership and complete closedness;
// - all generic bounds and anti-constraints;
// - substituted signature, effects, and every source expression result type;
// - exact source overload selection and mapped argument origins;
// - exact target instance and parameter passing modes;
// - caller-to-callee substitution composition;
// - unique instance keys and reachability from a valid root;
// - supported native type, operation, and lifetime shapes.
//
// Default, null, foreign-workspace, duplicate, or incomplete specialization metadata is invalid.
// Recovery constants and error types are not concrete generic arguments.
//
// Current body specialization substitutes already-bound facts. It does not rerun full source body
// and overload analysis for every concrete tuple. Before accepting an operation or member whose
// meaning depends on the concrete type, an implementation must perform the necessary rebinding or
// reject it. The current subset rejects detected dependent operations and members.

// 8. Generic calls from a bin root
// --------------------------------
//
// A bin graph starts at its required non-generic entry. Only statically selected reachable calls
// add generic instances. Every helper's concrete tuple is checked separately for constraints,
// supported ABI, operations, runtime helpers, checked effects, and root obligations.
//
// A generic identity helper does not make every T native-compatible. If identity<int> is admitted
// and identity<String> is admitted, these are distinct instances and call edges. An instance that
// needs an unimplemented nullable layout, ref-like ABI, unsupported floating ABI, user operator,
// exception edge, or root map is rejected without changing the selected source overload.
//
// Function values, delegates, closures, reflection lookup, dynamic dispatch, and runtime type
// arguments are not part of the direct-call specialization model. A function reference cannot ask
// the backend to choose a type tuple at runtime.
//
// Recursive calls reuse the same exact key when the declaration and closed type tuple match.
// Increasingly nested recursive type arguments may create new keys and are bounded by the
// specialization resource contract. Graph recursion and runtime recursion are independent.

// 9. cdylib and staticlib exports
// -------------------------------
//
// C libraries expose only wrapper functions admitted by the C ABI contract. A generic source
// declaration MUST NOT be a generic C callable: C has no Sol type parameters, constraints,
// overload resolution, or inference protocol.
//
// A closed internal generic helper MAY be retained when an admitted exported nongeneric wrapper
// reaches it and the transitive body is verified. That helper remains private implementation code,
// not an export, import-library symbol, or stable cross-build identity.
//
// A concrete wrapper may call a generic helper only after source binding selected the helper and
// FIR verified the closed tuple. The helper cannot wait until the C caller supplies a runtime type.
//
// Source-level generic validity does not imply C ABI validity. The current exported boundary admits
// only explicitly qualified scalar integer, single, and double values and supported scalar
// ref/out locations. String, Object, arrays, nullable values, tuples, half, quad, generic values,
// unsupported effects, and stateful receiver exports remain rejected unless a later ABI contract
// explicitly adds them.
//
// A wrapper preserves source argument mapping, evaluation order, passing modes, and return
// behavior. Ref/out storage needs an exact supported native representation; it cannot be retyped,
// boxed, or silently widened.
//
// Export collisions are checked after applying the final native symbol rule. Erased generic
// arguments cannot distinguish exports when the C symbol collides. Collision is an artifact error,
// never a reason to select one source overload.
//
// FIR function IDs, type IDs, and private specialization suffixes are not exported ABI. Runtime
// adapter names are reserved against source collisions. A static archive may contain private
// specialized helpers, but foreign callers only see the declared concrete wrappers and documented
// archive/runtime requirements.

// 10. solib boundary
// ------------------
//
// A solib is a versioned ZIP-based container with a bounded manifest, module export metadata,
// permitted verified-FIR payloads, target-specific native objects, optional binary bitcode as
// defined by schema, and integrity digests.
//
// The current development format is manifest schema 2. It pins compiler identity, language
// edition, target, LLVM 23.1.3, runtime layout, linkage, module identities, and payload lengths and
// SHA-256 values. Entry ordering and timestamps are deterministic for identical declared inputs.
//
// A reader validates total and expanded size, entry count, path safety, duplicate and
// case-colliding names, declared payload membership, byte length, digest, schema, target,
// compiler/runtime compatibility, and module metadata before exposing content.
//
// A reader returns checked container data. It cannot mint VerifiedFirProgram from JSON, trust a
// producer's verified flag, execute code, or treat serialized type/function IDs as identities in
// the consumer's analysis workspace.
//
// A generic declaration in module metadata is not an instantiable template. A consumer cannot ask
// a producer to specialize a body absent from the consumer's source compilation. Producer-local
// instance IDs cannot be inferred or called across container boundaries.
//
// A producer may package a closed instance as private code only when the container schema admits
// it, all required symbols and runtime contracts are declared, and no consumer can mistake the
// record for a generic export. Current external generic instantiation, dependency resolution,
// --extern consumption, and a generic solib ABI are not implemented.
// The current schema-2 payload contract is closed and nongeneric, so it does not yet admit generic
// FIR instance records. A future version may define private closed-instance payloads separately
// from consumer-instantiable generic templates.
//
// A future generic container ABI requires versioned declaration and type identity, calling
// convention/layout, constraints, runtime/compiler identity, dependency closure, visibility,
// duplicate ownership, cache invalidation, and consumer verification. It cannot be added to schema
// 2 as an unversioned JSON field.
//
// A digest detects change relative to the manifest. It is not a signature, publisher identity,
// trust policy, or complete reproducible-build proof.

// 11. SolRT linkage
// -----------------
//
// SolRT belongs to the native Sol process. It is separate from Solc's .NET 10 runtime and CLR
// objects. Host and guest heaps do not share references.
//
// Dynamic SolRT linkage is the default. Linked executables and dynamic libraries deploy the
// required runtime DLL under the output contract. A static-library consumer must provide matching
// platform system libraries and the runtime closure selected by the artifact.
//
// --static-solrt-lib selects static SolRT linkage. It does not select an artifact, change source
// semantics, select AOT versus ORC, alter generic arguments, or turn a DLL into an archive.
//
// For bin AOT, static linkage places runtime objects in the executable; the default imports the
// runtime DLL. A cdylib is linked with its selected runtime closure. A staticlib's archive and
// consumer obligations follow its selected linkage; a dynamic-runtime archive is not self-contained.
//
// ORC resolves the guest runtime inside the separate guest process. Static or dynamic guest linkage
// cannot load guest objects into Solc or expose guest symbols to the host CLR.
//
// Runtime linkage is part of artifact compatibility. A solib records linkage assumptions and
// runtime identity; a reader rejects incompatible ABI/layout instead of silently substituting a
// different runtime.
//
// The current runtime-layout version is provisional. It does not establish a stable SolRT ABI or
// imply full std.core, object layout, or GC root support.

// 12. LLVM and linker boundary
// ----------------------------
//
// Sol lowering issues typed operations through LLVM 23.1.3 C++ APIs. It must not generate textual
// LLVM assembly as the compilation interface. Printer text is inspection output only.
//
// The backend accepts a VerifiedFirProgram and checked artifact policy. Lowering preserves
// instance identity, exact targets, types, calling convention, runtime/effect obligations, and
// required root metadata.
//
// The native component verifies modules before code emission and after its optimization pipeline.
// AOT emits Windows AMD64 COFF. Embedded LLD.COFF and LLD.Common link objects and create archives;
// Solc does not shell out to lld-link.exe or llvm-lib.exe.
//
// ORC receives binary bitcode from the same constructed module and verifies it in the guest. The
// guest binds an explicit native runtime allowlist.
//
// Artifact obligations are:
//
// - bin: entry, reachable bodies, runtime helpers, startup, and deployment companions;
// - cdylib: private objects, wrapper shims, export names, import library, and runtime deployment;
// - staticlib: archive index, wrapper/object closure, and runtime linkage requirements;
// - solib: exactly the metadata and payload inventory selected by the manifest schema.
//
// An unresolved required symbol aborts linking. The linker cannot invent a generic body or choose
// an unrelated same-name function to fill a specialization edge.

// 13. Resource limits and diagnostics
// ----------------------------------
//
// The current builder caps one compilation at 4,096 distinct generic instances, 16,000,000
// aggregate source AST nodes copied into specialized bodies, and 128 nested type levels. Current
// limit exhaustion reports SOL2403.
//
// These are implementation limits, not source arity limits. Staying below them does not establish
// type validity, body support, verifier success, ABI compatibility, or linker success. Accounting
// must be overflow-safe and cancellation must stop work promptly.
//
// Unsupported native shapes report SOL2400; a malformed FIR proof reports SOL2402; an invalid
// entry reports SOL2401; a specialization resource limit reports SOL2403. Source binding errors
// retain their source diagnostics.
//
// A deterministic limit error identifies the declaration or call that exhausted the budget.
// Partial graph state can be used for diagnostics but cannot be lowered or published.
//
// Recovery nodes remain invalid. The compiler cannot emit an empty-body instance to claim success.

// 14. Determinism, security, and compatibility
// --------------------------------------------
//
// Identical source snapshots, compiler identity, language features, target, backend options,
// artifact type, linkage, and native dependencies should produce deterministic function order,
// FIR inspection, symbols, manifest bytes, and container entry order.
//
// Instance discovery order cannot change canonical identity. Compilation-local IDs are not stable
// across source edits and are never public ABI. A cache key additionally includes every semantic
// source/dependency digest and ABI-affecting compiler, target, runtime, LLVM, artifact, and
// optimization input.
//
// Native readers validate untrusted names, lengths, counts, hashes, and schemas before extraction.
// Windows case-insensitive collisions are rejected, and extraction remains beneath its destination.
// ORC process separation does not remove IPC framing, handle, cancellation, size, or exit checks.
//
// Solib data cannot authorize arbitrary native imports. A malformed generic record cannot request
// pointer reinterpretation or bypass FIR verification. Host paths and usernames are not generic
// instance identity and are excluded from public manifests unless a distinct documented field
// requires provenance.
//
// FirJson schema 7 records ordered specialization arguments. Schema 3 readers cannot be assumed
// compatible. Solib manifest schema 2 is separate; a FIR JSON change does not redefine it. Readers
// reject unknown mandatory schema versions instead of guessing.
//
// LLVM 23.1.3, target ABI, compiler semantic identity, and runtime ABI are artifact inputs. Matching
// visible module names or function signatures alone does not establish binary compatibility.

// 15. Conformance status
// ---------------------
//
// Reports distinguish source parsing, binding, selected generic tuple, FIR construction,
// independent verification, LLVM module verification, object/bitcode emission, artifact commit,
// loading/execution, and runtime/platform behavior.
//
// A successful earlier phase does not imply any later phase. A fixture describes normative
// behavior; it does not report a run unless its status explicitly says so.
//
// The implemented slice is limited to direct module-level generic free-function calls whose
// concrete types, body operations, effects, nested calls, and artifact roots fit the verified native
// subset. It is not full Sol generic support.
//
// Sol 0.0.1 remains the initial frontend release. Development artifact features belong in a release
// only when its packaging metadata and published binaries establish that they were shipped.

// 16. Ownership of artifact contract fields
// -----------------------------------------
//
// The compiler, artifact reader, native runtime, and consumer own distinct proof obligations.
// Solc owns source selection, specialization construction, FIR verification, and output policy.
// The LLVM component owns native handle and module validity. The linker owns symbol closure and
// target-object compatibility. The solib reader owns structural and digest validation. The runtime
// owns process-local object layout and GC root behavior.
//
// None of these components may claim another component's proof from a serialized flag or successful
// return code. A valid ZIP archive does not prove a valid FIR graph. Verified FIR does not prove
// correct LLVM lowering. A linked DLL does not prove wrappers preserve Sol evaluation order. A
// runtime load does not prove cross-version ABI compatibility.
//
// A producer and consumer must agree on ownership of every callable symbol. Internal FIR function
// IDs are owned by one compilation. Public C names are owned by the wrapper contract. Sol module
// declarations are owned by the language module system. The solib manifest owns only its versioned
// metadata fields and declared payloads.
//
// Cross-boundary use requires an explicit contract at every boundary: source module visibility,
// artifact export visibility, native ABI, runtime identity, and target compatibility. A generic
// type argument cannot substitute for one of these declarations.
//
// If any boundary owner cannot validate its input, it rejects or marks the value untrusted. It
// cannot coerce an unknown generic record into a trusted function, type, symbol, or runtime object.
