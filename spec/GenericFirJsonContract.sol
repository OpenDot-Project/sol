// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative inspection contract; the current serializer emits schema 5.
module source.spec.generic_fir_json_contract

// FIR JSON inspection and generic source identity
// ===============================================
//
// This file specifies the diagnostic and inspection contract for serialized verified FIR.
// It complements `GenericFirSpecialization.sol`; it does not define a stable object format.
// The JSON is an inspection view of a compiler-owned verified program in one compilation.
// A consumer may use it to diagnose compiler behavior or compare selected structural facts.
// A consumer must not treat its local integer identities as cross-process type identities.
// A consumer must not feed edits to this JSON back into FIR construction or LLVM lowering.
//
// Current implementation status:
//
// - `FirJson.Serialize` emits root schema version 5 and `verified: true`.
// - Function records carry `specializationArguments` as declaration-ordered type IDs.
// - Call nodes carry `typeArguments` as declaration-ordered type IDs.
// - Call argument arrays preserve explicit source order followed by omitted defaults in formal order.
// - `FirCallValueReference` identifies a previously captured value in that call argument array.
// - The `types` table describes IDs available in the producing analysis workspace.
// - FIR verification precedes `VerifiedFirProgram` construction and serialization.
// - Node and flow spans carry source offsets, but no stable source-file key is serialized.
// - Diagnostic codes SOL2400, SOL2402, and SOL2403 describe separate phase boundaries.
// - Schema 5 is an implementation inspection revision, not the public Sol ABI version.
//
// Full source file identity, reproducible cross-build IDs, cache serialization, debug path policy,
// source-map packaging, and public diagnostic JSON remain separate contracts to be completed.
// The requirements below distinguish current observable fields from future normative obligations.
//
// 1. Terms
// --------
//
// `Inspection JSON` is the UTF-8 JSON string returned by `FirJson.Serialize`.
// `Schema version` is the root integer that selects the field and meaning contract.
// `Verified program` is FIR accepted by `FirVerifier.Verify` and wrapped in a verified value.
// `Compilation workspace` is the set of analysis and type objects created for one compilation.
// `Type ID` is an integer allocated by that workspace's resolved-type interner.
// `Function ID` is the dense function index in the verified FIR program.
// `Slot ID` is the dense local-value index within one FIR function.
// `Node origin` is the source element and span associated with a FIR node.
// `Source declaration identity` is object identity anchored to one parsed source declaration.
// `Generic parameter ordinal` is the declaration position of a type parameter, starting at zero.
// `Specialization tuple` is the ordered list of closed types selected for one generic declaration.
// `Effect ID` is the Type ID of a declared or flow-reported effect type.
// `External consumer` is any process or tool other than the serializer's owning compiler instance.
// `Stable artifact identity` is a versioned identity that survives process and build boundaries.
//
// 2. Inspection boundary
// ----------------------
//
// The serializer accepts only a non-null `VerifiedFirProgram` value.
// The verified wrapper is the gate that separates candidate FIR from admitted FIR.
// The serializer does not run source overload selection again.
// The serializer does not run generic inference again.
// The serializer does not repair invalid IDs or rebuild missing call targets.
// The serializer does not convert unsupported source constructs into generic placeholders.
// The serializer does not invoke LLVM or write machine code.
// The serializer does not promise that every valid Sol program has verified native FIR.
// A source program may be well-typed and still fail native admission with SOL2400.
// A verifier failure does not produce a verified wrapper and must not be serialized as verified.
// A resource failure does not produce a truncated successful graph.
// Cancellation remains cancellation and is not represented as one of the three diagnostics below.
//
// The word `verified` in the root is a claim about the object being serialized.
// It is not a cryptographic attestation of the process or compiler binary.
// It is not proof that an untrusted JSON document was created by `FirJson`.
// Consumers must treat arbitrary JSON as untrusted input.
// No JSON parser may reconstruct a `VerifiedFirProgram` by trusting the boolean alone.
//
// 3. Schema versioning
// --------------------
//
// The root `schema` field is the required compatibility discriminator.
// The current emitted value is the integer 5.
// A schema revision changes whenever a field is added, removed, renamed, retyped, or redefined.
// A schema revision also changes when ordering or identity meaning changes observably.
// Changes to comments in this spec alone do not change the serializer schema.
// Changes to source language semantics do not automatically change the JSON schema.
// Changes to FIR metadata can require both a schema change and a language-contract update.
//
// A schema-5 reader must reject a missing, non-integer, or unsupported `schema` value.
// A reader must not infer schema 5 from the presence of `typeArguments`.
// A reader must not silently ignore a future schema version while claiming full fidelity.
// A writer must not emit a newer field layout while retaining schema value 5.
// A change that preserves every serialized field meaning can retain schema 5.
// The project may choose a stricter revision policy than this minimum contract.
//
// The schema value is not `Sol 0.0.2`, the LLVM version, the runtime ABI version, or the language
// version. It versions only the inspection document shape and the semantics of its fields.
// An artifact container has its own compatibility and ABI versioning rules.
// A future persistent generic cache must define a different versioned format.
// A future stable diagnostic interchange format must define its own versioned contract.
//
// 4. Root record
// --------------
//
// The schema-5 root has the following members:
//
// - `schema`: integer 5 for the current inspection contract.
// - `verified`: boolean true because the serializer accepts only the verified wrapper.
// - `entry`: integer function ID for an executable, or -1 for a library program.
// - `library`: boolean indicating library mode for this compilation.
// - `functions`: dense array of function records indexed by their `id` values.
// - `types`: array of workspace type descriptions used by functions and expressions.
// - `flow`: array of per-function flow graphs corresponding to verified ordinary functions.
//
// `entry` and `library` are mutually interpreted according to program mode.
// An executable entry is a dense function index and must refer to the accepted entry function.
// A library uses `entry: -1` and `library: true`.
// A consumer must not guess an entry point from a function name.
// A consumer must not treat a library's first function as an entry point.
// An empty `flow` array is not sufficient evidence that no calls or effects exist.
// A consumer joins records using IDs only within the same root document.
//
// 5. Function record
// ------------------
//
// Each function record describes exactly one FIR function in the dense `functions` array.
// `id` is its zero-based dense program index and must equal its array position.
// `name` is the source-facing function name or a compiler-owned runtime adapter name.
// `runtime` is the textual `FirRuntimeFunction` variant name.
// `specializationArguments` is an ordered array of type IDs for a generic function instance.
// An ordinary non-generic function has an empty `specializationArguments` array.
// `returnType` is the type ID of the function's verified return type.
// `effects` is an ordered array of effect type IDs declared on the function signature.
// `parameters` is an ordered array of slot IDs for parameter slots.
// `slots` describes parameters and locals in dense slot order.
// `body` is a serialized FIR block for ordinary functions and null for runtime adapters.
//
// The type argument list order follows the generic declaration's parameter ordinals.
// It does not follow the order in which call arguments were written.
// It does not follow the order of named arguments after source argument mapping.
// It does not follow the order in which inference evidence was collected.
// It does not follow the order in which a pending specialization happened to be discovered.
// A zero-parameter generic declaration is not represented as a specialization.
// A non-generic function must not carry a nonempty specialization tuple.
//
// A specialization tuple identifies a function instance only when paired with its source
// declaration identity in the current compilation context.
// The JSON record does not currently serialize a stable source declaration key.
// A tuple of local IDs alone is therefore not a durable instance identity.
// Two functions may each specialize their first parameter to the same Type ID and remain distinct.
// Two declarations with a parameter both spelled `T` remain distinct declarations.
// A specialized function must keep its source `name`; a compiler must not encode type IDs by
// changing the source-facing function name and pretending the result is a stable symbol.
//
// 6. Slot record
// --------------
//
// Every slot has `id`, `name`, `type`, `mutable`, `mode`, and `isParameter` fields.
// `id` is dense and local to one function.
// `name` is for inspection and does not define slot identity.
// `type` references one type record in the same compilation document.
// `mutable` reflects the source/FIR binding mutability fact.
// `mode` is the textual passing mode for a parameter slot, or `Value` for ordinary locals.
// `isParameter` distinguishes the initial parameter region from declared local slots.
//
// Parameter slot order is source parameter order after source declaration analysis.
// `parameters` must point to those same slots in that same order.
// An identifier spelling reused in another function does not alias a slot.
// A local slot ID cannot be joined with a slot from another function without its function ID.
// A slot name is not a debugger-safe stable identifier.
// The inspection record does not serialize lexical scope trees or shadowing chains.
// A consumer must not use `name` alone to resolve `FirRead` or `FirStore` nodes.
//
// 7. Type record
// --------------
//
// A schema-5 type record contains `id`, `kind`, `name`, and `arguments`.
// `id` is an integer owned by the current compilation's resolved-type interner.
// `kind` is the textual `ResolvedTypeKind` variant name.
// `name` is the resolved display name retained by the type object.
// `arguments` is the ordered list of child type IDs exposed by that resolved type.
//
// A type ID is suitable for local joins inside one inspection document.
// A type ID is not a source spelling.
// A type ID is not a declaration ID.
// A type ID is not a stable hash or content address.
// A type ID is not stable across compilations, source order changes, SDK changes, or process runs.
// A type ID is not a CLR `Type`, LLVM `Type*`, object address, mangled name, or ABI ordinal.
// A type ID must not be persisted as an external package dependency.
//
// Two IDs being equal in one document means the workspace interner assigned the same identity.
// Two IDs being numerically equal in separate documents means nothing about type equality.
// A repeated type record with the same ID is invalid for a consumer that validates this schema.
// A reference to an absent type ID is invalid for a consumer that validates this schema.
// The current writer emits the workspace type inventory; consumers should tolerate records that
// are not reachable from a subset of functions when the analysis workspace interns extra types.
//
// For generic specializations, the ID at ordinal `i` in `specializationArguments` is the concrete
// type selected for generic parameter ordinal `i` of the source declaration.
// The type may itself be compound, such as an array, nullable type, tuple, function type, or nominal
// type; acceptance into FIR is independently constrained by native verification.
// An open type parameter is forbidden for an emitted closed specialization.
// Type rendering must preserve constructor nesting and nullability when a future reader renders it.
// Current schema-5 records are not required to provide a canonical textual spelling for every type.
//
// 8. Node envelope
// ----------------
//
// Every serialized FIR node has `kind`, `type`, `start`, `length`, and `value`.
// `kind` is the runtime FIR node record name emitted by the serializer.
// `type` is a type ID for an expression and null for a statement or block.
// `start` is the start offset in the associated source document's origin span.
// `length` is the source span length associated with that node.
// `value` is the node-specific payload described below.
//
// Offsets are source offsets from the parser's span model, not line or column values.
// The JSON does not currently serialize a normalized relative path with each node.
// A node origin alone may therefore be ambiguous across multiple input files.
// A consumer must not assume offsets are Unicode scalar indexes unless the span contract says so.
// A future source map contract must state whether offsets count UTF-16 code units, bytes, or another
// unit and must pair every origin with a stable source-file table entry.
// The current serializer preserves numeric offsets and lengths only.
//
// 9. Node payloads: expressions
// -----------------------------
//
// `FirConstant` carries `Spelling` as its value payload.
// Constant spelling is source-oriented inspection data and may not be a canonical literal format.
// A numeric constant's spelling is not sufficient to recover its verified type by itself.
// The node envelope's `type` ID is authoritative for that FIR expression.
//
// `FirRead` carries an integer `Slot` reference.
// The reference is local to the enclosing function and must name a declared slot.
// It is not a source variable declaration ID.
// It is not portable outside the enclosing function record.

// `FirCallValueReference` carries `evaluationIndex`, an index into the containing call's argument
// array. It must point to an earlier argument whose formal mode is `Value`. This node is valid only
// inside a default expression in a call plan. It reads the captured result of that earlier evaluation;
// it does not repeat the source expression or read caller storage again.
//
// `FirUnary` carries the operator text in `Operation` and a nested `value` node.
// `FirBinary` carries operator text in `Operation`, then nested `left` and `right` nodes.
// The operator text is a FIR operation label, not necessarily the source token spelling.
// Operand and result type IDs are supplied by the nested node envelopes and parent envelope.
//
// `FirCall` carries target function ID, type arguments, and argument records.
// `target` is a program-local dense function ID.
// `typeArguments` is the ordered list selected by source analysis for the call target.
// `arguments` preserves FIR parameter indexes and argument values after source mapping.
// Each argument record carries `parameter`, textual `mode`, and nested `value`.
// Argument array order is evaluation order: explicit source arguments first, then omitted defaults
// in formal declaration order. It is not generic argument order.
// Named arguments may therefore be represented with parameter indexes differing from their array
// position; consumers must use `parameter` to interpret the target formal slot.
// A default may use `FirCallValueReference` to read an earlier captured value without evaluating its
// source expression twice.
// A non-generic call has an empty type argument array.
// A generic call's target specialization tuple must agree with its call type argument tuple.
// The verifier establishes that relationship before a `VerifiedFirProgram` exists.
//
// `FirIntrinsic` carries an operation name, parameter order array, and nested argument nodes.
// `order` gives the mapping to the intrinsic's logical parameter positions.
// It is not a type argument array.
// A consumer must not infer a generic substitution from intrinsic ordering metadata.
//
// `FirArrayLength` carries a nested `array` expression.
// `FirArrayIndex` carries nested `array` and `index` expressions.
// `FirArrayLiteral` carries an ordered `elements` array.
// Element ordering is semantically observable and must be preserved.
// The array expression's type ID describes its closed element type.
//
// 10. Node payloads: statements
// -----------------------------
//
// `FirBlock` carries an ordered `statements` array.
// Statement order is execution order and must be preserved by consumers.
// A block has a null type envelope.
//
// `FirDeclare` carries a slot reference and a nullable `initializer` node.
// `initializer: null` means the declaration has no initializer expression in FIR.
// It does not mean the local is initialized to a source-language null value.
// Definite assignment is validated by the flow graph and verifier.
//
// `FirStore` carries a target slot and a nested `value` expression.
// `FirEvaluate` carries one nested expression whose value is discarded.
// `FirReturn` carries a nullable `value` expression.
// `value: null` represents a return with no value in the admitted empty-tuple return subset.
//
// `FirIf` carries `condition`, `then`, and nullable `else` nodes.
// A null `else` means no explicit else block exists in FIR.
// It is not equivalent to a fabricated empty source block for source-origin purposes.
// `FirLoop` carries nullable initializer, nullable condition, body, and nullable update nodes.
// A null condition represents the FIR loop form without a condition expression.
// `FirArrayForEach` carries `itemSlot`, `array`, and `body`.
// `FirBreak` and `FirContinue` currently serialize an empty payload object.
// Unknown FIR node variants must cause serialization failure rather than silent omission.
//
// 11. Flow graph record
// ---------------------
//
// Each flow record names `functionId` and an ordered `blocks` array.
// `functionId` references the function whose verified body produced the graph.
// A block carries `id`, `reads`, `writes`, `kills`, `successors`, and `returns`.
// Block IDs and all slot references are scoped to the associated function.
// `reads`, `writes`, and `kills` are slot ID collections from flow analysis.
// `successors` identifies outgoing graph edges by block ID.
// `returns` describes whether the block has a return edge under the flow contract.
// `calls` lists call targets and effects observed in the block, plus source span offsets.
//
// Flow metadata is verification evidence, not an executable CFG serialization.
// The record omits some implementation data required to rebuild a machine CFG.
// A consumer must not treat `successors` as complete exception or unwind edges.
// A consumer must not infer GC root maps from `reads`, `writes`, or `kills` alone.
// A consumer must not claim a call is effect-free merely because a partial display is empty.
// Flow graph array ordering is currently deterministic for an equivalent verified program.
// Determinism does not create cross-build stable IDs.
//
// 12. Generic source identity
// ---------------------------
//
// A generic instance's semantic key in the in-memory compiler is source declaration identity plus
// the ordered closed argument identities.
// Source declaration identity is anchored to a particular parsed source element.
// It is not the function's simple name.
// It is not the module name alone.
// It is not the ordinal of the function among all source declarations.
// It is not the order of discovery in the specialization work queue.
//
// A generic type parameter is identified by owning declaration and ordinal.
// Type parameter spelling is retained for diagnostics but does not define identity.
// Shadowed parameters with equal spelling must not unify by name alone.
// Type IDs encode workspace-owned resolved identities after inference and substitution.
// FIR function ID identifies the emitted instance only within its `functions` array.
// Call target ID references that same local array.
//
// Schema 5 exposes ordered type IDs but not a stable declaration identity field.
// Therefore the JSON can show a specialization's type tuple but cannot independently distinguish
// two same-named declarations with the same tuple after source identity is discarded.
// A consumer must retain the containing source bundle or separately retain its own file/span map.
// Future schemas may add source-file and declaration records with explicit stable scopes.
// Such fields require a schema revision and must not reinterpret current local IDs.
//
// 13. Source type/effect correspondence
// -------------------------------------
//
// For each generic function instance, its `specializationArguments` instantiate its source
// declaration's type parameters by ordinal.
// Its `returnType` must equal the source analysis result after that substitution.
// Each parameter slot type must equal the corresponding source parameter type after substitution.
// Its `effects` must equal the declared source effects after substitution.
// A generic call's `typeArguments` must equal its selected call binding after caller substitution.
// The target function's specialization tuple must agree with those selected call arguments.
// The call result and argument mappings must agree with the selected source binding.
// Node type IDs must match the source expression bindings under the same instance substitution.
// The current verifier checks these relationships before exposing the verified wrapper.
//
// The serializer does not independently validate these relationships.
// A corrupted in-memory object that bypasses verification must not be passed to serialization.
// The JSON boolean `verified` cannot repair a missing source binding.
// A source effect is not a call type argument.
// A declared effect list is not the same thing as a basic-block's observed call-effect list.
// Type substitutions are simultaneous, declaration-scoped, and keyed by resolved type identity.
// A textual replacement of `T` is never a conforming substitute operation.
//
// 14. Diagnostic phase boundaries
// -------------------------------
//
// Diagnostic records are produced by compiler phases and are not presently embedded in FIR JSON.
// Their IDs below distinguish reasons for failure; they are not JSON `schema` values.
// A source-valid program may still be rejected at the native admission boundary.
// A FIR verifier failure indicates an invariant breach or invalid FIR input to the verifier.
// A resource diagnostic indicates a bounded compilation could not finish its specialization work.
//
// `SOL2400` is the native subset admission diagnostic.
// It reports a source construct or selected concrete specialization shape that FIR does not admit.
// It is not a generic inference failure code.
// It is not a request to rewrite source overload selection to prefer a lower-capability candidate.
// It must point at the source construct that crossed the native boundary.
// It must not produce `VerifiedFirProgram`, LLVM output, or a partial binary.
// Unsupported owner methods, unsupported operations, and unsupported type representations belong
// at this boundary when source analysis itself succeeded.
//
// `SOL2402` is the FIR verifier diagnostic.
// It reports a source/FIR identity mismatch, invalid type ownership, signature/effect mismatch,
// invalid call target, malformed slot or flow invariant, or other failed verifier condition.
// It does not report a normal, well-formed native subset limitation discovered by the builder.
// A failed verifier result contains diagnostics and no verified program.
// A consumer must treat `SOL2402` as a compiler correctness or malformed-FIR signal.
// It must not suppress the diagnostic and continue code generation from the failed program.
//
// `SOL2403` is the generic specialization resource-limit diagnostic.
// It reports a defined cap such as instance count, specialized-body node work, or type depth.
// It must identify the request that exceeded the cap when a source origin is available.
// It must not disguise a resource limit as generic inference failure or native unsupported syntax.
// It must not return a successful truncated specialization graph.
// It must not emit a generic function with an empty body as a recovery artifact.
// It must not produce LLVM output from the incomplete graph.
//
// Parser, syntax, name-resolution, inference, constraint, conversion, and effect diagnostics retain
// their own phase codes; this file does not rename them to the three FIR boundary codes.
// Cancellation and out-of-memory conditions are not source diagnostics.
// Internal unexpected exceptions must not be converted to SOL2400 or SOL2403 merely for convenience.
// If an error occurs before a source origin can be assigned, the compiler uses its established
// locationless diagnostic representation rather than inventing a fake span.
//
// 15. Diagnostic location requirements
// -------------------------------------
//
// A diagnostic produced for a source call uses the exact call source origin where possible.
// A diagnostic for a type argument uses the argument expression or type syntax origin where stored.
// A diagnostic for a body operation uses the operation's source origin.
// A verifier diagnostic uses the origin of the malformed FIR node or source declaration.
// A resource error uses the call or declaration that required additional specialization work.
// A diagnostic span does not change function, slot, or type identity.
// A diagnostic range must be validated against the corresponding source text by the diagnostic UI.
// A location without a file key is incomplete for a multi-file external consumer.
// The current inspection JSON does not define a stable diagnostic source-file table.
// Future serialized diagnostics must reference a versioned source table, not absolute machine paths.
//
// A diagnostic must state the failed boundary in a way that does not imply the source is ill-typed
// when it only fails the current native subset.
// A generic inference mismatch must remain an inference diagnostic.
// A failed constraint must remain a constraint diagnostic.
// A closed type that is valid in source but unsupported by native lowering may use SOL2400.
// A type ID from a foreign workspace injected into FIR is a verifier failure, not SOL2400.
// A growing recursive specialization graph that reaches a defined budget reports SOL2403.
// An ordinary finite recursive generic instance that reuses its exact key is not a resource error.
//
// 16. Determinism and ordering
// ---------------------------
//
// The current serializer writes JSON with indentation for inspection readability.
// It preserves arrays supplied by the verified program and type workspace.
// Function IDs are dense and correspond to the emitted `functions` array positions.
// Slot IDs are dense within each function.
// Type IDs are the order allocated by the workspace's interner.
// Generic type argument arrays are in declaration parameter order.
// Call argument arrays preserve the FIR builder's argument evaluation representation.
// Flow block arrays and their successor arrays follow the graph object ordering.
//
// Equivalent inputs are expected to produce deterministic semantic structure in one compiler build.
// This expectation is not a promise that local IDs remain fixed across builds.
// Object hash codes, dictionary bucket order, process addresses, and task completion order must not
// be made observable as a substitute for semantic ordering.
// A future deterministic stable identity format must define canonical source and type identities.
// The present JSON uses neither cryptographic hashes nor canonical type strings.
// Consumers comparing files across compiler builds should compare decoded semantic relationships,
// not raw numeric Type IDs as if the IDs were stable package ABI values.
//
// 17. Privacy and path handling
// -----------------------------
//
// The present node envelope serializes only numeric spans and no source path field.
// It does not claim that all nested diagnostic text is free from user source excerpts.
// A future extension must not serialize absolute developer machine paths by default.
// A source key should be relative to an explicit compilation root or opaque within the document.
// A document must not expose environment variables, credentials, checkout usernames, or host names.
// A consumer must not concatenate a source-provided path into a filesystem operation without checks.
// Source names and function names remain user-controlled display strings.
// A serializer must escape JSON strings using the JSON library rather than manual concatenation.
//
// 18. Reader behavior
// -------------------
//
// A schema-5 reader first validates the root schema and basic JSON kinds.
// It then builds document-local maps for functions, types, and function-scoped slots.
// It verifies IDs are in range before following references.
// It verifies `entry` against the `library` mode before using it.
// It verifies specialization arrays are lists of IDs, not names or textual type syntax.
// It preserves array ordering exactly as serialized.
// It treats absent optional node payloads according to this schema, not according to a source guess.
// It rejects unknown node kinds when it requires complete FIR semantics.
// A best-effort viewer may display an unknown node as opaque text but must label the view partial.
//
// A reader must not execute the graph.
// A reader must not infer memory safety from a `verified` boolean in untrusted JSON.
// A reader must not resolve local IDs by consulting a different compiler process's type table.
// A reader must not import this view into the persistent specialization cache.
// A reader must not treat JSON `name` fields as signed symbols or dynamic loader names.
//
// 19. Writer behavior
// -------------------
//
// The writer checks cancellation before work and during recursive node traversal.
// The writer emits the exact current schema integer.
// The writer fails for an unknown FIR node variant instead of silently skipping its semantics.
// The writer serializes generic argument arrays even when they are empty.
// The writer does not omit an empty array merely to reduce payload size.
// The writer does not emit null for a required specialization array.
// The writer does not sort a declaration-ordered generic argument list by type name or ID.
// The writer does not claim stable IDs in prose or property names.
// The writer does not accept an unverified `FirProgram` overload as an equivalent input.
//
// When schema fields are added, snapshots and consumers must be updated together.
// Schema migration must not reinterpret earlier numeric IDs as new identity classes.
// Any output truncation policy must fail explicitly and must not leave syntactically valid JSON that
// appears to be a complete verified program.
// Cancellation must propagate as cancellation rather than a diagnostic JSON document.
//
// 20. Compatibility matrix
// ------------------------
//
// | Change | Schema action | Reason |
// | --- | --- | --- |
// | Add a required root member | Increment | Existing complete readers need a new contract. |
// | Remove a function member | Increment | Existing readers lose a required fact. |
// | Rename `typeArguments` | Increment | Field lookup and producer/consumer contract changes. |
// | Change argument order | Increment | Generic parameter ordinals would be misread. |
// | Change ID scope | Increment | Joins and identity assumptions change. |
// | Change span offset units | Increment | Source navigation would select the wrong text. |
// | Add a node kind | Increment | Exhaustive consumers need a new variant. |
// | Add a value payload to an existing kind | Increment | Its semantic record contract changes. |
// | Change `verified` semantics | Increment | Root claim changes. |
// | Change indentation only | None | JSON structure and meanings remain identical. |
// | Change internal queue implementation | None | No serialized fact changes. |
// | Change an unobservable compiler cache | None | No serialized fact changes. |
//
// A producer and a consumer must negotiate schema support explicitly.
// A stable ABI revision must not be inferred from this compatibility matrix.
// A package format may embed an inspection document but must identify it as non-executable metadata.
//
// 21. Implementation conformance obligations
// --------------------------------------------
//
// An implementation claiming schema-5 conformance must:
//
// 1. Serialize only verified FIR values.
// 2. Emit the exact root fields and meanings defined above.
// 3. Use function IDs as dense array indexes.
// 4. Use slot IDs only in their owning function scope.
// 5. Use type IDs only as workspace-local joins.
// 6. Preserve declaration-ordered generic specialization tuples.
// 7. Preserve call type arguments in the selected callee's parameter order.
// 8. Serialize every verified node kind with its defined payload.
// 9. Preserve source offsets and lengths without inventing file paths.
// 10. Fail rather than silently serialize an unknown node kind.
// 11. Keep SOL2400, SOL2402, and SOL2403 semantically distinct.
// 12. Never convert a failed or truncated FIR build into `verified: true` output.
// 13. Propagate cancellation distinctly from source diagnostics.
// 14. Revise the schema when any observable field meaning changes.
// 15. Avoid describing schema-5 IDs as stable ABI identities.
//
// 22. Required specialized-function consistency
// -----------------------------------------------
//
// For each specialized function record, a validating producer has already established:
//
// - The source declaration is a module-level generic free function admitted by current policy.
// - The specialization tuple length equals the declaration type parameter count.
// - Each tuple entry is closed and owned by the current analysis workspace.
// - The tuple satisfies the source generic constraints under resolved declaration identity.
// - The emitted signature is the declaration signature after simultaneous substitution.
// - The emitted body expression types match source analysis after that substitution.
// - Nested generic calls identify the exact selected target instance.
// - Every emitted instance is reachable from an ordinary, non-generic root function.
// - Every resource limit was respected without dropping graph edges.
//
// These invariants are not reconstructed from the JSON alone because source declaration identity
// and full source binding facts are not serialized in schema 5.
// A consumer can perform structural checks, but cannot replace the in-process verifier.
// A new producer must use its own verifier before claiming the same semantics.
//
// 23. Negative requirements
// --------------------------
//
// It is non-conforming to persist a schema-5 Type ID and later assume it names the same type.
// It is non-conforming to derive a C export name from `specializationArguments` without a separate
// versioned ABI naming rule.
// It is non-conforming to treat the schema integer as an artifact or Sol language version.
// It is non-conforming to report a source typing error solely because FIR admission returned SOL2400.
// It is non-conforming to continue LLVM generation after SOL2402 or SOL2403.
// It is non-conforming to serialize a partially processed specialization queue as successful FIR.
// It is non-conforming to join a call's `target` against another document's function array.
// It is non-conforming to pair slot ID 1 in one function with slot ID 1 in another function.
// It is non-conforming to treat textual equality of names as type-parameter identity.
// It is non-conforming to invent source paths not present in the originating workspace.
//
// 24. Future contract work
// ------------------------
//
// A later schema may add a source-file table with opaque document-local file IDs.
// A later schema may add declaration records with source ranges and logical module names.
// A later schema may add a canonical type description independent of workspace-local IDs.
// A later schema may add serialized diagnostics with codes, severity, spans, and related locations.
// A later schema may add per-node file references for multi-file source navigation.
// A later package contract may define stable type and declaration identities for caches.
// A later ABI contract may define externally visible names and generic export rules.
//
// Each such extension requires explicit compatibility review and a schema revision.
// No future field may silently turn current inspection IDs into stable identity values.
// No future cache may trust unvalidated JSON as executable FIR.
// No future UI may label a source-bound but native-rejected example as emitted machine code.
//
// 25. Conformance summary
// -----------------------
//
// Schema 5 is a readable, compiler-local structural view of verified FIR.
// Its function and slot IDs are dense indices with explicitly local scopes.
// Its type IDs are workspace-local references and have no durable meaning.
// Its generic argument arrays preserve declaration ordinals.
// Its node source offsets support local inspection but lack a portable source-file identity.
// Its `verified` field reflects the serializer's typed input boundary, not untrusted provenance.
// SOL2400 marks a valid-source shape rejected by native FIR admission.
// SOL2402 marks a FIR invariant violation discovered by independent verification.
// SOL2403 marks a bounded generic-specialization resource limit.
// These diagnostics do not replace source parsing, typing, overload, constraint, or effect errors.
// The JSON is not a package ABI, cache format, executable graph, or durable cross-build identity.
// Any future promotion beyond inspection requires separate versioning and validation work.
