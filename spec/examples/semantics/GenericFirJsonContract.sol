// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative inspection examples; expected observations are not a claim of runner coverage.
module examples.semantics.generic_fir_json_contract

// This file describes structural observations for the schema-5 verified FIR inspection document.
// `expect` annotations are normative fixture outcomes for an implementation that supports the
// corresponding native shape. A valid source example marked `NativeBoundary` may still be rejected
// by today's FIR builder; such rejection is not a source type error.
// Type IDs shown as symbols below are placeholders for document-local integers.
// They are never expected to equal a hard-coded numeric ID across compilations.
//
// The schema and diagnostic contract is defined in `spec/GenericFirJsonContract.sol`.
// The broad generic specialization contract is defined in `spec/GenericFirSpecialization.sol`.

func <T> identity(value: T): T = value
func <T> echo(value: T): T = value
func <T> box(value: T): []T = [value]
func <T> head(values: []T): T = values[0]
func <A, B> left(first: A, second: B): A = first
func <A, B> right(first: A, second: B): B = second
func <T> forward(value: T): T = identity(value)
func <T> forwardTwice(value: T): T = forward(identity(value))
func intEcho(value: int): int = identity(value)
func stringEcho(value: String): String = identity(value)
func arrayEcho(value: []int): []int = identity(value)
func doubleIdentity(value: int): int = identity(identity(value))
func leftNamed(value: int): int = left(second = "label", first = value)
func rightNamed(value: int): String = right(second = "label", first = value)
func explicitInt(value: int): int = identity<int>(value)
func explicitString(value: String): String = identity<String>(value)
func inferredBox(value: int): []int = box(value)
func inferredHead(value: []String): String = head(value)
func nestedArray(value: [][]int): [][]int = identity(value)
func nestedForward(value: int): int = forwardTwice(value)

// 1. Root schema and mode
// -----------------------
// expect: root.schema == 5
// expect: root.verified == true only for FirJson.Serialize(VerifiedFirProgram)
// expect: root.library is the current FIR program mode
// expect: executable root.entry indexes functions; library root.entry == -1
// expect: root.functions is an array whose position equals each record.id
// expect: root.types holds compilation-local type descriptions
// expect: root.flow is verifier metadata indexed by functionId
// expect: absent or unsupported schema is rejected by a schema-aware reader
// expect: schema value is not language version and is not SolRT ABI version
// expect: formatting-only JSON change does not require semantic schema revision
//
// The root `verified` bit is written from a typed in-process wrapper. A JSON consumer cannot prove
// the bit's origin. It cannot deserialize the document into a trusted executable FIR object merely
// because the value is true. It must treat external JSON as data for inspection.
//
// 2. Ordinary function record
// ---------------------------
// expect: ordinary non-generic source function has specializationArguments = []
// expect: function.id == its zero-based functions array position
// expect: function.returnType references a root.types id
// expect: function.effects is an array of type ids
// expect: function.parameters contains ordered slot ids
// expect: function.slots is dense from zero within that function
// expect: function.body is a serialized block for an ordinary source function
// expect: runtime adapter body is null and runtime names the adapter kind
//
// For `intEcho`, the ordinary caller is represented separately from the specialized `identity`
// target. Its own function record has no specialization arguments. The call node carries the type
// argument `[int]`; the target function record carries `[int]` as its specialization tuple.
// These arrays are related by verifier-proven source identity before serialization.
//
// 3. Generic function record and ordered tuple
// --------------------------------------------
// expect: identity<int> specializationArguments == [typeId(int)]
// expect: identity<String> specializationArguments == [typeId(String)]
// expect: each tuple follows identity declaration parameter ordinal zero
// expect: the two identity instances have distinct dense function ids
// expect: identity source name remains "identity" in both records
// expect: specializationArguments are present even when empty
// expect: no open type parameter is serialized for a closed instance
// expect: tuple is not sorted by display name, type ID, or machine code discovery order
//
func emitInteger(): int = identity(3)
func emitText(): String = identity("sol")

// expect: emitInteger's call.typeArguments == [typeId(int)]
// expect: emitText's call.typeArguments == [typeId(String)]
// expect: each call.target resolves within the same root.functions array
// expect: target.specializationArguments == call.typeArguments
// expect: target.returnType == call expression.type
// expect: source call spans remain attached after instance interning
//
// The two local `typeId` values are symbolic names in this fixture. Numeric type IDs can vary as
// unrelated types are interned earlier or later. A test compares the referenced type's resolved
// structure within its own document, not an assumed absolute integer.

// 4. Declaration identity is not a name
// -------------------------------------
// expect: declaration identity = source origin object plus closed ordered type tuple in memory
// expect: simple name "identity" is not an in-memory specialization key
// expect: module spelling is not a complete source declaration key
// expect: type parameter spelling T is not a type parameter identity
// expect: type ID is not a stable source declaration identity
// expect: function.id is not a stable cross-build specialization identity
// expect: schema 5 does not serialize stable source declaration identity
// expect: consumer that drops source bundle cannot always distinguish same-named declarations
//
// A future contract can add a source-file and declaration table, but that change needs a schema
// revision. This fixture intentionally does not invent stable IDs by hashing a source name. Hashes
// require canonical inputs, collision handling, versioning, and a stated public compatibility scope.

// 5. More than one generic parameter
// ----------------------------------
// expect: left<int,String> tuple == [typeId(int), typeId(String)]
// expect: right<int,String> tuple == [typeId(int), typeId(String)]
// expect: ordinal zero belongs to A and ordinal one belongs to B
// expect: parameter mapping does not reorder the generic tuple
// expect: named argument positions are carried separately in FIR call arguments
//
func twoTypes(): int = left(first = 1, second = "x")
func namedReordered(): int = left(second = "x", first = 1)
func reverseDeclarationOrder(): String = right(first = 1, second = "x")

// expect: twoTypes and namedReordered select the same left<int,String> source declaration instance
// expect: their `arguments` may encode different evaluation/mapping positions
// expect: their `typeArguments` remain [int,String]
// expect: reverseDeclarationOrder selects right<int,String>, not right<String,int>
// expect: same tuple types do not merge `left` and `right` source declarations
//
// Argument evaluation order and generic parameter order answer distinct questions. The former tells
// the FIR reader how values reach formals; the latter tells it which concrete type fills each
// declaration parameter. A consumer must never derive the latter from argument array position.

// 6. Explicit and inferred arguments
// ---------------------------------
// expect: explicitInt call typeArguments == [typeId(int)]
// expect: explicitString call typeArguments == [typeId(String)]
// expect: intEcho inferred call typeArguments == [typeId(int)]
// expect: stringEcho inferred call typeArguments == [typeId(String)]
// expect: explicit syntax is not copied as source text into typeArguments
// expect: inferred evidence provenance is not currently serialized in FIR JSON
//
func explicitCall(): int = identity<int>(1)
func inferenceCall(): int = identity(1)
func explicitTextCall(): String = identity<String>("x")
func inferenceTextCall(): String = identity("x")

// expect: explicitCall and inferenceCall may share one identity<int> function record
// expect: explicitTextCall and inferenceTextCall may share one identity<String> function record
// expect: instance interning does not erase call node spans
// expect: each call node keeps its own selected type argument array
// expect: explicit-versus-inferred evidence does not change the instance key after selection
//
// The serializer exposes the resolved arguments, not the solver's complete evidence ledger.
// A future diagnostic/evidence view has a different schema and ownership contract.

// 7. Nested calls and substitution
// --------------------------------
// expect: forward<int> body call to identity has closed call.typeArguments == [int]
// expect: forward<String> body call to identity has closed call.typeArguments == [String]
// expect: forwardTwice<int> closure includes forward<int> and identity<int>
// expect: each nested target id points to its exact closed function record
// expect: caller specialization is applied to its body binding before nested target lookup
// expect: no callee target remains parameterized by the caller's open T
//
func rootNestedInt(): int = forwardTwice(5)
func rootNestedString(): String = forwardTwice("s")

// expect: rootNestedInt remains non-generic with specializationArguments = []
// expect: rootNestedString remains non-generic with specializationArguments = []
// expect: generic callee records are reachable transitively from ordinary root functions
// expect: an orphan specialized record is rejected by verifier before JSON serialization
// expect: exact recursive instance keys may form cycles without duplicate function records
// expect: growing type keys that exhaust a resource cap produce SOL2403, not successful JSON
//
// The JSON does not serialize the pending work queue. It serializes only the completed verified
// function graph. Discovery order is not a public semantic order for the generic argument tuple.

// 8. Compound type IDs
// --------------------
// expect: box<int> specializationArguments == [typeId(int)]
// expect: box<int> returnType references a []int type record
// expect: array type arguments points to int's workspace type id
// expect: head<String> parameter type references []String
// expect: array nesting is represented through recursively referenced type records
// expect: type graph comparisons follow references rather than display name alone
//
func boxedInteger(): []int = box(1)
func firstText(values: []String): String = head(values)
func boxedArray(values: []int): [][]int = box(values)

// expect: boxedArray selects box<[]int> and its tuple includes the array type id
// expect: the `types` table includes a record for []int, not just its leaf int type
// expect: a compound type's printed name is not the sole reconstruction algorithm
// expect: unsupported compound native layout may still be rejected with SOL2400
// expect: schema serialization never implies that every referenced type reached LLVM
//
// Nullable, tuple, function, nominal, and ref-like type shapes need their own complete type-record
// semantics before external tools can reconstruct them. The type ID array itself carries identity
// references; it does not substitute for a canonical type grammar.

// 9. Call argument payload
// ------------------------
// expect: FirCall value contains target, typeArguments, and arguments
// expect: arguments each contain parameter, mode, and nested value
// expect: parameter is a target formal slot position, not a type parameter ordinal
// expect: mode is a passing mode label, not a nullable/effect qualifier
// expect: each nested argument expression has its own node envelope and source span
// expect: call result type appears on the parent node envelope
// expect: call result type can be independently compared with target.returnType
//
func callWithNamedFormals(firstValue: int, secondValue: String): int =
    left(second = secondValue, first = firstValue)

func <T> withEarlierDefault(value: T, fallback: T = value): T = fallback
func inspectEarlierDefault(value: int): int = withEarlierDefault(value)

// expect: callWithNamedFormals call.arguments maps values to formal positions
// expect: callWithNamedFormals call.typeArguments maps generic parameters in declaration order
// expect: argument array order does not replace the `parameter` index
// expect: nested argument spans identify original source expressions
// expect: no `typeArguments` field is omitted for an ordinary non-generic call
// expect: inspectEarlierDefault call arguments are ordered [value, fallback]
// expect: the omitted fallback argument contains FirCallValueReference(evaluationIndex=0)
// expect: that index points to the earlier value argument and does not re-evaluate `value`
//
// The current FIR slice admits pure defaults on module-level free functions when each omitted value
// is a scalar/String literal, an earlier value-parameter read, or a supported primitive unary/binary
// expression. Calls/member/array/effectful defaults, defaults following ref/out parameters, and
// defaults on vararg functions remain outside the verified native subset. Varargs and some other
// argument forms may still be rejected before a verified program exists.

// 10. Node envelope and origins
// -----------------------------
// expect: every serialized node has kind, type, start, length, and value
// expect: expression type is a document-local type id
// expect: statement/block type is null
// expect: start and length derive from the associated source origin span
// expect: source offset unit follows current parser span model
// expect: a source file key is not currently present in every node envelope
// expect: no absolute source checkout path is synthesized from a span
//
// A FIR clone represents a generic instance but retains source nodes from the original declaration.
// One declaration body may therefore appear in more than one specialized function record with the
// same source span. This is expected: source origin is provenance, not a unique FIR node ID.
// Function ID and parent call target distinguish the specialized contexts.
// A debugger that needs stable multi-file navigation requires a source file table not present here.

// 11. Flow record and effects
// ---------------------------
// expect: flow record functionId references the owning function id
// expect: block id and successor ids are graph-local values
// expect: read/write/kill slot ids belong to the enclosing function
// expect: each flow call target references the same root.functions array
// expect: flow call effects are type ids from the same types table
// expect: flow call source span preserves call origin offsets
// expect: flow graph is verifier metadata rather than executable CFG serialization
//
// `function.effects` lists declared effects on the function signature. `flow.blocks[].calls[].effects`
// lists effects associated with calls in the flow graph. These collections have related type IDs,
// but different ownership and meaning. A viewer should label them separately.
//
// The examples in this file use pure generic functions so the JSON expectation remains focused on
// type and declaration identity. Generic effect substitution and checked-effect enforcement are
// covered separately by the generic-effects fixture; neither effect list is a stable ABI encoding.

// 12. Dense IDs and scope boundaries
// ----------------------------------
// expect: functions array index is the scope for target ids
// expect: slot array index is scoped to one function id
// expect: types array is the scope for all Type IDs in one document
// expect: flow block array index is scoped to one function graph
// expect: IDs from two JSON files cannot be joined by numeric equality
// expect: IDs may change when unrelated declarations or types are added
// expect: a pretty-printer must not label a local ID as a durable key
//
// `identity<int>` in two documents can receive different type and function IDs while preserving
// the same language meaning. Conversely, integer ID 7 in two documents may name unrelated types.
// Cross-document comparison requires canonical source and type identities from a future contract.

// 13. Native admission and serializer boundary
// --------------------------------------------
// expect: source-valid but unsupported native type -> SOL2400, no verified JSON
// expect: generic body operation unsupported by current FIR -> SOL2400 at source origin
// expect: source inference mismatch -> source-analysis diagnostic, not SOL2400
// expect: verifier mismatch after FIR construction -> SOL2402, no verified JSON
// expect: specialization work cap -> SOL2403, no truncated verified JSON
// expect: ordinary successful verified FIR -> schema-5 inspection JSON
// expect: cancellation -> cancellation propagation, not one of the three diagnostics
//
func nativeAcceptedInt(): int = identity(7)

// expect: nativeAcceptedInt may serialize when the selected primitive operation is admitted
// expect: same source API with a valid unsupported representation may stop at SOL2400
// expect: lack of LLVM object output does not turn JSON inspection into failed serialization if a
//        verified FIR program exists and the caller requested FIR inspection only
// expect: failure before VerifiedFirProgram creation prevents FirJson.Serialize input construction
//
// FirJson takes a VerifiedFirProgram, not raw FirProgram. The type boundary is deliberate. A tool
// must not add a convenience overload that serializes unchecked source candidates as `verified`.

// 14. Diagnostic classification matrix
// -------------------------------------
// expect: SOL2400 means current native FIR subset does not admit the selected source shape
// expect: SOL2400 does not mean overload inference failed
// expect: SOL2400 does not mean verifier found malformed FIR
// expect: SOL2402 means an independently checked FIR invariant failed
// expect: SOL2402 must prevent code generation and verified serialization
// expect: SOL2403 means a generic specialization resource boundary was reached
// expect: SOL2403 must not return a partial but successful instance graph
// expect: parser/type/constraint/effect diagnostics keep their own diagnostic identities
//
// The diagnostic code is independent from the JSON schema value. A future diagnostic JSON format
// must have its own schema member and must never reuse `schema: 5` to mean a diagnostic revision.
// A diagnostic consumer can use its code to route a message but still needs file identity for a
// multi-file navigation UI. The current FIR JSON does not serialize diagnostics or a stable path.

// 15. Reader validation
// ---------------------
// expect: reject missing `schema`
// expect: reject schema 3 when expecting schema 5 call type arguments
// expect: reject unsupported future major shape rather than silently claiming fidelity
// expect: reject target id outside functions array
// expect: reject slot id outside owning function slot array
// expect: reject type id not present in document types map
// expect: reject duplicate dense function ids and duplicate scoped slot ids
// expect: preserve generic tuple array order
// expect: preserve array element and statement order
//
// An IDE that chooses to render unknown future node kinds as opaque boxes can remain useful, but it
// must disclose that execution structure is incomplete. It cannot claim semantic compatibility.
// A cache reader cannot repair missing source identity by using display-name heuristics.

// 16. No stable serialization contract
// ------------------------------------
// expect: schema 5 is not a persistent specialization cache format
// expect: schema 5 is not a stable public FIR exchange format
// expect: schema 5 is not a C ABI name-mangling scheme
// expect: schema 5 is not a `.solib` symbol contract
// expect: schema 5 is not a debug information format
// expect: schema 5 is not a source archive or source map
// expect: schema 5 is not a checksum or cryptographic attestation
// expect: schema 5 is only the current compiler inspection schema
//
// A consumer may retain JSON as a human-readable diagnostic artifact for the exact compiler run.
// It must record compiler identity separately if it needs to compare artifacts over time. That
// identity does not make workspace IDs portable. It only explains which producer emitted the view.

// 17. Future extension expectations
// ---------------------------------
// expect: sourceFileTable addition requires a schema revision
// expect: declarationKey addition requires explicit stable identity semantics
// expect: canonicalType addition requires generic and nullable constructor rules
// expect: diagnostic records require code, severity, span, related locations, and file identity
// expect: source offset units are specified before line/column conversion is promised
// expect: new FIR node kind requires reader compatibility handling
// expect: stable cache requires compiler, target, language, and ABI keys beyond this document
//
// A new source table should use per-document opaque IDs rather than absolute build-machine paths.
// A canonical type string should be a rendering of a grammar, not a guess from `name` and `args`.
// A stable declaration key should handle module versioning, partial declarations, overload identity,
// source movement, and collisions. These topics are deliberately not solved by schema 5.

// 18. Reproducibility fixture rules
// ---------------------------------
// expect: compare root structure and references, not hard-coded workspace type IDs
// expect: compare specialization type argument order as semantically meaningful
// expect: compare function names only as display fields, not declaration identity
// expect: compare statement and array order exactly
// expect: compare numeric spans only against the same source input and span model
// expect: do not compare absolute paths because none are part of this contract
// expect: do not require identical JSON whitespace for semantic conformance
//
// Snapshot tests for schema layout may compare exact field names and JSON kinds. Their expected
// values must use symbolic roles or in-document references for compiler-local identities. A test
// that asserts `int` always has ID 5 is brittle and would falsely couple unrelated interning order.

// 19. Negative reader cases
// -------------------------
// expect: function target references another document -> reject
// expect: specialized type tuple has fewer elements than source declaration -> reject when checked
// expect: specialized tuple includes open parameter -> reject verified input
// expect: generic call typeArguments do not agree with target tuple -> reject verified input
// expect: source declaration same name but other origin -> keep distinct in compiler, not merge by name
// expect: absent function source origin -> verifier failure SOL2402
// expect: duplicate specialization key for one source declaration -> verifier failure SOL2402
// expect: orphan generic instance -> verifier failure SOL2402
// expect: invalid verifier input is never labeled successful by root.verified
//
// Some conditions are compiler invariants and are not expected to be discoverable from JSON alone.
// The reader can validate structural references, but source-origin proof belongs to FirVerifier.

// 20. Root failure cases
// ----------------------
// expect: SOL2400: no VerifiedFirProgram exists for the rejected native candidate
// expect: SOL2402: Verify returns diagnostics and Program == null
// expect: SOL2403: builder returns failure and incomplete specialization queue is discarded
// expect: serialization is not invoked after any of those failed FIR outcomes
// expect: user can still request unrelated source diagnostics through the normal compiler pipeline
// expect: the JSON serializer does not turn a failure result into a partial success record
//
// This distinction lets an inspection tool report what stage completed without claiming a native
// artifact exists. A future compiler command may provide structured phase summaries separately.

// 21. Conformance recap
// ---------------------
// expect: schema root integer = 4 for the current implementation
// expect: generic tuples always use declaration parameter order
// expect: type/function/slot/flow IDs always state their local scope
// expect: source declaration identity is not serialized as a durable key in schema 5
// expect: node origins preserve spans but not complete source file identity
// expect: type and effect IDs remain references into the current compilation table
// expect: verified JSON never contains a partially built specialization closure
// expect: SOL2400, SOL2402, and SOL2403 remain phase-distinct
// expect: no serialized field implies an external ABI promise
//
// Implementations may extend diagnostics and tooling separately, but any observable schema change
// requires a corresponding revision and a migration note. A consumer must remain conservative when
// it encounters unknown values or identity references outside its own document.

// 22. Type table consistency matrix
// ---------------------------------
// expect: every function.returnType resolves in root.types
// expect: every function.effects element resolves in root.types
// expect: every slot.type resolves in root.types
// expect: every expression node.type resolves in root.types
// expect: every generic specializationArguments element resolves in root.types
// expect: every call.typeArguments element resolves in root.types
// expect: every flow call.effects element resolves in root.types
// expect: every child in type.arguments resolves in root.types
// expect: every type id has exactly one record in one document
// expect: duplicate display names do not merge different resolved type ids
// expect: child ordering for tuple, function, array, and nominal shapes follows type semantics
// expect: nullable structure must not be inferred from a human-readable name alone
//
// This matrix checks reference closure, not source-language legality. Source analysis and the FIR
// verifier establish that a particular type is legal in its context. An inspection reader may flag
// an out-of-range reference as malformed JSON, but it cannot decide whether `T?` satisfies a source
// constraint without the corresponding declaration and semantic rules.

// 23. Function and call cross-reference matrix
// --------------------------------------------
// expect: each function.id equals exactly one functions array index
// expect: each call.target resolves to exactly one function in the same document
// expect: each flow call.target resolves to the same function table
// expect: call.typeArguments length agrees with the source target generic arity when source exists
// expect: target.specializationArguments == call.typeArguments after caller substitution
// expect: target.returnType == call expression type after specialization
// expect: call argument parameter indexes address target formal parameters
// expect: target parameter types agree with source call binding under substitution
// expect: recursive call target is permitted when it references a verified dense function id
// expect: target cycle does not imply cyclic JSON object references
//
// JSON nests the call's actual argument expression under a node payload. The target reference is a
// scalar ID; it does not recursively embed the target function record. This keeps the JSON tree
// finite even when the source call graph contains direct or mutual recursion.

// 24. Mutation and tampering examples
// -----------------------------------
// expect: edit root.verified from true to false -> inspection data no longer asserts verified origin
// expect: edit root.verified from false to true -> does not create a verified compiler object
// expect: edit a call.target -> no longer matches source binding; verifier rejects the FIR object
// expect: edit call.typeArguments order -> no longer matches declaration ordinals; verifier rejects
// expect: edit one function specialization argument -> signature/body facts cease to agree
// expect: edit expression.type id -> source expression binding check fails
// expect: edit effects list -> source declaration and checked effects no longer agree
// expect: delete a type record -> all references to its id are unresolved
// expect: duplicate a function id -> dense identity invariant fails
// expect: change span length -> source origin no longer points at the same syntax
//
// These edits are illustrative. The supported public API does not expose a JSON-to-FIR trust path.
// A producer constructing FIR programmatically must still run FirVerifier and preserve source
// declaration provenance. Parsing JSON and constructing a record is not a substitute for that API.

// 25. Version negotiation cases
// -----------------------------
// expect: reader supports schema 5 -> decode all known fields
// expect: reader supports schema 4 only -> reject schema 5 or explicitly announce partial support
// expect: reader supports schema 5 and sees schema 6 -> do not claim schema-5 full fidelity
// expect: schema 5 with missing specializationArguments -> reject malformed current document
// expect: schema 5 with null specializationArguments -> reject malformed current document
// expect: schema 5 with typeArguments serialized as a string -> reject wrong JSON kind
// expect: schema 5 with typeArguments reordered -> preserve order and report source mismatch if known
// expect: schema 5 with extra unknown member -> preserve if viewer policy permits, but do not infer it
// expect: a strict validator may reject unknown members when the profile requires exact shape
//
// Forward-compatible display and strict semantic validation are different reader profiles. A
// display-only tool can retain unknown members in an opaque map. A compiler cache loader cannot
// ignore unknown members and then claim it loaded the same semantic record.

// 26. Source span repetition cases
// -------------------------------
// expect: one generic declaration used at two closed types can appear as two function records
// expect: corresponding body nodes may share start and length values
// expect: repeated span does not mean duplicated or aliased function ID
// expect: source span can be zero length for synthesized nodes only if origin contract permits it
// expect: source file table is needed to distinguish equal offsets in separate source files
// expect: source node origin retains declaring file in memory even when schema 5 omits its key
// expect: a future schema must pair span with file identity before promising navigation
//
// A source navigation consumer that has the exact compilation inputs may correlate known paths
// out-of-band. It must not assume that input order or a module name uniquely recovers the originating
// file. A same-module declaration can be split across several conventional source files.

// 27. API behavior cases
// ----------------------
// expect: null `program` argument throws the established argument-null exception
// expect: pre-cancelled token propagates cancellation before serialization work
// expect: cancellation during recursive node serialization propagates cancellation
// expect: no diagnostic code is emitted merely because the caller cancelled inspection
// expect: unknown FIR node variant throws instead of producing an incomplete node record
// expect: JSON strings are escaped by System.Text.Json, not hand-assembled
// expect: no output cache is written by FirJson.Serialize
// expect: no LLVM module is created by FirJson.Serialize
// expect: no runtime library or artifact is selected by FirJson.Serialize
//
// The serializer is a pure inspection operation over an already verified graph, apart from memory
// allocation and cancellation. It is intentionally separate from Solc's artifact selection, the
// LLVM bridge, SolRT link mode, package manifests, and Shake metadata.

// 28. Comparison of compilation documents
// ----------------------------------------
// expect: compare schema number before comparing any field
// expect: map type references within each document before comparing resolved shapes
// expect: compare generic arguments by ordered type shape and declaration context
// expect: compare function calls by source location and local target graph only if source identity
//        is available to the comparison tool
// expect: avoid pairing same-name functions across documents without overload identity
// expect: treat Type ID renumbering as compatible when all local references are updated consistently
// expect: treat specialization tuple reorder as a semantic change
// expect: treat node span change as a source-origin change that needs explanation
// expect: treat added unknown node kind as a partial-compatibility boundary
//
// A structural diff tool can produce useful evidence without inventing ABI meaning. It should show
// which type relation changed, which call target changed, and which local references were renumbered.
// It should not report every type ID change as a public API break.

// 29. No promise from this fixture
// --------------------------------
// expect: an expectation annotation defines a contract case, not an executed test result
// expect: fixture presence does not imply current compiler support for every function body
// expect: documented `NativeBoundary` rejection does not invalidate source semantics
// expect: serialized successful shape does not imply a published package artifact exists
// expect: source examples do not make JSON a user-editable IR input format
// expect: a future test runner must report parse, semantic, FIR, verifier, JSON, and LLVM stages distinctly
//
// Maintainers must update this matrix when the schema changes. They must record the current emitted
// schema separately from planned diagnostic/source-map extensions. A fixture should state whether it
// checks a writer output, a reader rejection, an in-memory verifier invariant, or only a normative
// requirement; those outcomes are not interchangeable.
