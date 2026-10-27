<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# FIR JSON and generic diagnostics

This guide explains the current FIR inspection JSON contract for generic function specialization.
It is for compiler and tooling maintainers. It describes schema 6 as emitted by the current
implementation and distinguishes that inspection view from durable artifact formats.

The normative language contract is in [`GenericFirJsonContract.sol`](../../spec/GenericFirJsonContract.sol).
The specialized source fixture is
[`GenericFirJsonContract.sol`](../../spec/examples/semantics/GenericFirJsonContract.sol).
The overall specialization scope is described in
[`GenericFirSpecialization.sol`](../../spec/GenericFirSpecialization.sol).

## Current status

`FirJson.Serialize` takes a `VerifiedFirProgram`; it does not take raw candidate FIR. The root
document emits `schema: 6` and `verified: true`. Function records include an ordered
`specializationArguments` array, and generic call nodes include an ordered `typeArguments` array.
Call arguments are ordered by evaluation: explicit source arguments first, then omitted defaults in
formal order. A `FirCallValueReference` uses `evaluationIndex` to refer to an earlier value captured
by that call. These additions remain inspection metadata, not a persistent ABI.

The verifier checks that a function's type tuple agrees with its source declaration, signature,
effects, expression types, constraints, and selected call bindings. The JSON writer serializes that
already verified object; it does not repeat source analysis or prove that arbitrary JSON is trusted.

Current type IDs are compilation-local. The schema does not include a stable source declaration key
or a complete source-file table. Span offsets are useful to inspect a compiler run that still has its
inputs, but they do not create a portable source map. Persistent caches, stable package metadata,
source navigation across processes, and external diagnostic JSON need separate versioned contracts.

This schema is not a public FIR exchange format, machine-code format, `.solib` ABI, C symbol scheme,
or generic instantiation cache. Do not persist its integer IDs and reuse them in another compilation.

## Schema 6 at a glance

| Member | Meaning | Scope |
| --- | --- | --- |
| `schema` | Inspection layout and field semantics revision; currently `6` | Document |
| `verified` | The input value passed through the in-process verified wrapper | Document |
| `entry` | Executable function index, or `-1` in library mode | Document |
| `library` | Whether the program is being inspected in library mode | Document |
| `functions` | Dense function records | Document |
| `types` | Resolved type descriptions referenced by this document | Compilation workspace |
| `flow` | Verification flow metadata for emitted functions | Function and graph |

`schema` is not the Sol language version, the SolRT ABI version, or the LLVM version. The number
does not identify a release artifact. A field meaning or ordering change requires schema review;
changes to internal queues that are not observable in JSON do not by themselves require a revision.

The root mode fields are interpreted together. For an executable, `entry` indexes the accepted
entry function. A library has `library: true` and `entry: -1`. A reader must not guess an entry point
from the name `main` or from the first array element.

## Function and slot records

Each function's `id` is its zero-based index in `functions`. `name` is display information. `runtime`
identifies the runtime adapter kind. `returnType` and `effects` refer to the `types` table.
`parameters` lists parameter slot IDs in declaration order. `slots` lists all slots in dense local
order. `body` contains the verified body or is null for a runtime adapter.

| Field | Interpretation |
| --- | --- |
| `id` | Dense program-local function index; a `FirCall.target` refers to this index |
| `name` | Source-facing function or adapter name; not a unique declaration key |
| `runtime` | Text form of the runtime adapter variant |
| `specializationArguments` | Ordered concrete type IDs for a generic function instance |
| `returnType` | Type ID for the verified result |
| `effects` | Ordered declared effect type IDs |
| `parameters` | Slot IDs for formal parameters, in declaration order |
| `slots` | Parameter and local slot records |
| `body` | Serialized FIR block, or null for a runtime adapter |

A slot's `id` is local to its containing function. `type` references a compilation-local type.
`mutable`, `mode`, and `isParameter` carry inspection facts. A slot name is not an identity, and slot
number 2 in one function has no relation to slot number 2 in another function.

### Specialization tuple ordering

For `func <A, B> select(first: A, second: B)`, a closed instance has
`specializationArguments: [typeId(A), typeId(B)]`. The order is the generic declaration's type
parameter order. It is independent of:

- the order of named arguments at a call site;
- the order of candidate inference evidence;
- the order in which parameters appear in a caller;
- the work queue's discovery order;
- the numeric ordering of local type IDs.

The generic function's source name remains visible in the record. The serializer does not encode
the specialization by appending type IDs to the name or by exposing a machine symbol.

### In-memory identity versus serialized identity

In memory, an instance is keyed by source declaration identity and an ordered tuple of closed type
identities. The source declaration identity is anchored to a parsed source element. Parameter
spelling and function name do not replace that identity. Two declarations that both spell a type
parameter `T` have different owners and ordinals.

Schema 6 exposes the ordered type tuple, but does not serialize a durable source declaration key.
After the original analysis context is discarded, a consumer cannot always distinguish same-named
declarations with the same tuple. Function IDs are dense indexes for one document, not stable keys.

## Type references

Schema-6 type records contain `id`, `kind`, `name`, `arguments`, `parameterModes`, and `effects`. The writer uses the resolved
type workspace's local integer IDs. All type references are meaningful only inside the same JSON
document. A function return, slot, expression, effect, call argument, and child type can refer to an
ID from the `types` array.

Readers should validate that referenced IDs exist and are unique in their local table. They should
not assume that `id` is a source spelling, a declaration index, a stable hash, a CLR `Type`, an LLVM
`Type*`, or an external symbol. Equal IDs across two files do not imply equal types. Different IDs
across two builds do not imply a semantic type change.

The `name` field is useful for display, but is not a complete canonical type grammar. In particular,
a reader must not reconstruct nullable, nested, tuple, function, nominal, array, or ref-like type
identity by guessing from a display name. The ordered `arguments` links are part of the current type
description, but schema 6 does not define a cross-version canonical type string. Non-function types
carry empty `parameterModes` and `effects` arrays. Function types preserve one mode per parameter and
an ordered effect-type-ID set; their `arguments` are parameter type IDs followed by the result ID.

An open type parameter is not valid in a closed emitted specialization. The verifier checks that
generic function arguments are closed and that constraints hold. The serializer does not perform
that check a second time.

## Call nodes

A `FirCall` payload contains `target`, `typeArguments`, and `arguments`. `target` is a function ID
in the same root document. `typeArguments` lists the types chosen for the target's generic
parameters in declaration order. `arguments` carries each formal parameter index, passing mode, and
nested value expression. The array is in evaluation order: explicit arguments follow source order,
then omitted defaults follow formal order. A default that reads an earlier parameter contains a
`FirCallValueReference` node whose `evaluationIndex` points to the earlier captured argument entry.

These orderings serve different purposes. `typeArguments` answers which types fill the generic
declaration parameters. The `arguments` entries answer how values map to formal parameters and how
those expressions appear in FIR. A consumer must not derive a generic tuple from the argument array's
position, particularly when the call used named arguments.

The verifier checks that the call's type tuple agrees with source overload resolution after caller
substitution, that the target has the selected closed specialization tuple, and that result,
parameters, effects, and argument mapping agree. For a non-generic call, the type argument array is
empty. The field is still present in schema 6.

`FirFunctionReference` carries a target function ID. `FirIndirectCall` carries a nested function
expression and argument records with formal indexes and passing modes. Function type records provide
the parameter modes and checked effect IDs used to verify the call. An indirect flow edge uses
`target: null` because the runtime callee comes from a function value; its `effects` still contain the
declared set. The function reference and indirect call are source-verified native FIR nodes; they do
not represent a closure environment. Arrays may contain non-null raw function pointers; their
elements have scalar pointer-width layout and are not GC roots. Closures and unsupported signature
conversions are rejected before serialization.

Call argument array order is not a declaration-order type tuple. Each argument's `parameter` value
is the target formal position. Nested expressions retain their source origin. A target ID does not
recursively embed the target function record, so recursive call graphs do not create recursive JSON
objects.

## Node envelope and source spans

Every serialized node has `kind`, `type`, `start`, `length`, and `value`. Expressions use a type ID;
statements and blocks use a null type. `start` and `length` come from the source origin span. They are
offsets, not line/column coordinates.

The current serializer does not include a stable source-file identity on every node. Two source files
can contain the same offset and length. A generic declaration body may also appear in multiple
specialized function records while preserving the same source span. That is expected: the origin
describes source provenance, not unique FIR identity.

Schema 6 is therefore useful for inspection while the corresponding compilation inputs are known.
Portable navigation needs a source-file table and a stated offset unit. Do not synthesize an absolute
path from the build machine or assume that a module name identifies exactly one file.

## Flow metadata

Flow records identify a `functionId` and a list of blocks. Block IDs and slot references are scoped
to that graph and function. The serialized facts include reads, writes, kills, successors, return
state, and calls with target, effects, and source spans.

This is verifier metadata, not a complete executable CFG. It is insufficient to rebuild machine
control flow, exception edges, unwind behavior, GC root maps, or runtime safepoints. A display tool
should label it as analysis metadata. It must not infer that empty-looking flow data proves a
function has no effects or allocation behavior.

`function.effects` describes declared signature effects. `flow.blocks[].calls[].effects` describes
call effects attached to graph records. The arrays can reference the same type table but represent
different facts and should not be merged in a viewer.

## Diagnostic boundaries

The current FIR JSON does not embed a serialized diagnostics array. The codes below are emitted by
compiler phases and are not values of the JSON `schema` field.

| Code | Boundary | Meaning | Output rule |
| --- | --- | --- | --- |
| `SOL2400` | Native FIR admission | A source-valid construct or selected concrete shape is outside the current verified native subset | No verified program or native output for that candidate |
| `SOL2402` | Independent FIR verification | Source identity, type, signature, effect, call, slot, or flow invariants do not hold | No verified wrapper; stop code generation |
| `SOL2403` | Generic specialization resources | A defined instance, specialized-body, or type-depth bound prevents completion | No truncated successful instance graph |

### `SOL2400`: native subset rejection

This code means that the current native FIR builder does not admit the selected source shape. It
does not mean overload resolution failed, and it does not make a source-valid construct ill-typed.
Examples include a concrete type representation or body operation outside the current native
subset. The diagnostic should point to the source construct or selected specialization that crossed
the boundary.

The compiler must not change source overload ranking merely to choose a candidate that happens to
fit current code generation. If source analysis selected a valid candidate but the native boundary
rejects it, report the boundary. Do not silently substitute a different overload.

### `SOL2402`: verifier failure

This code indicates an invariant breach or malformed FIR at the independent verifier boundary. It
can identify a mismatch between a FIR function and its source declaration, an open or foreign type,
an incorrect call target, wrong type arguments, invalid effect metadata, or a slot/flow mismatch.
Treat it as a compiler correctness signal, not a routine source-language limitation.

The verifier must return diagnostics without a `VerifiedFirProgram`. Code generation and verified
JSON serialization must not continue from the failed object. Suppressing the diagnostic and emitting
a partial binary would defeat the verifier's purpose.

### `SOL2403`: bounded specialization work

This code identifies a defined generic-specialization resource cap. Current implementation bounds
include 4,096 instances per compilation, 16,000,000 specialized body AST nodes, and 128 nested type
levels. The exact error should identify the dimension that was exhausted and the relevant source
origin where available.

The compiler must not report a resource cap as a source inference failure, emit an empty replacement
body, or serialize the queue processed so far as a successful complete graph. Cancellation is not
`SOL2403`; it must propagate as cancellation. An ordinary recursive call that reuses an exact closed
key is not a resource error merely because the graph contains a cycle.

### Other source diagnostics

Parser errors, name lookup, overload ambiguity, failed inference, failed constraints, conversions,
and checked-effect violations retain their own diagnostic ownership. A valid source call can fail
native admission after those phases succeed. An invalid source call should not be relabeled SOL2400
to suggest that it was semantically selected.

## Reader guidance

A schema-aware reader should:

1. Validate the integer `schema` before interpreting other members.
2. Validate JSON kinds and require the schema-6 specialization and function-signature arrays.
3. Build document-local maps for function and type IDs.
4. Scope slot IDs under the containing function and flow block IDs under the graph.
5. Check every reference before following it.
6. Preserve generic type argument order exactly.
7. Preserve statement, element, and flow successor ordering.
8. Treat source names and spans as display/provenance fields, not stable identities.
9. Reject unsupported future schemas when complete interpretation is required.
10. Keep unknown future nodes opaque and visibly partial in a best-effort viewer.

The `verified` boolean is not a trust mechanism. An arbitrary JSON producer can write `true`. A
reader must not reconstruct trusted executable FIR or run code based only on that field. JSON is an
inspection boundary, not a deserialization API for the compiler's verifier wrapper.

## Writer guidance

The writer should continue to accept only `VerifiedFirProgram`. It must serialize all fields required
by the selected schema, including empty specialization arrays. It must preserve declaration-ordered
type arguments and fail on an unknown FIR node kind rather than silently omit it. JSON strings should
be escaped by the JSON library.

The writer does not re-run binding, verify the source again, choose an artifact type, invoke LLVM, or
write an object file. Cancellation propagates; it does not become one of the phase diagnostics above.
If the writer is extended, producer and reader changes should land with an explicit schema decision.

## Compatibility and revisions

Increment the inspection schema when an observable field is added, removed, renamed, retyped, or
given a different meaning. Ordering and identity scope are semantic parts of the schema. Changing
`typeArguments` from declaration order to some other order would be a breaking change even if the
field remained an integer array.

Formatting-only changes such as indentation do not alter schema semantics. An internal work queue
change does not require a revision unless it changes serialized ordering or another observable fact.
Do not infer a stable ABI revision from schema 6. Artifacts, caches, diagnostic interchange, source
maps, and C symbol names each need their own compatibility rules.

| Proposed change | Schema decision |
| --- | --- |
| Add a required root member | Increment |
| Remove or rename a required field | Increment |
| Change tuple or node ordering | Increment |
| Change span offset units | Increment |
| Add a node kind or alter a payload | Increment |
| Add file/declaration identities | Increment and define their scope |
| Change whitespace only | No semantic revision |
| Change a non-observable compiler cache | No JSON revision |

## Source identity and future work

The current in-memory verifier can compare source element identity because it still has the source
analysis workspace. Schema 6 cannot expose every part of that proof. A future schema may add opaque
file IDs, declaration records, stable source keys, canonical types, or structured diagnostics. Each
addition needs a clear scope, privacy review, compatibility policy, and a schema revision.

Stable declaration keys must account for overloads, partial declarations, modules, source movement,
versioning, and collisions. Canonical type text must define arrays, nullability, tuples, functions,
nominal types, and generic parameters. A cache key also needs compiler version, target, language
configuration, ABI contract, and dependency identity. A local type ID cannot stand in for these.

No future extension should place absolute checkout paths in the JSON by default. Source keys should
be relative to an explicit root or opaque within the document. Diagnostics intended for external
tools should refer to a versioned file table and preserve related locations without machine-local
secrets.

## Conformance checklist

- The writer accepts a verified wrapper, not unchecked candidate FIR.
- The root advertises schema 6 for the current field layout.
- Function IDs and call targets use the same dense document-local function table.
- Slot IDs are scoped to their containing function.
- Type IDs are local to the producing analysis workspace.
- Generic tuples are closed and ordered by the source declaration's parameter ordinals.
- Call type arguments agree with the selected target instance.
- Source expression types, function signature, effects, and call mapping were verifier-checked.
- Source spans are not misrepresented as complete portable source locations.
- SOL2400, SOL2402, and SOL2403 remain distinct in diagnostics and output behavior.
- Failed or truncated FIR never becomes successful verified JSON.
- The schema is not called an ABI, cache, object, or stable interchange format.

This checklist describes the contract and current implementation boundary. It is not a test result.
The fixture specifies expected structural behavior; a separate test runner must report which stages
it executes and what was verified.

## Representative schema-6 shape

The following abbreviated document illustrates field relationships. Symbolic IDs are explanatory;
the serializer writes compilation-local integers and may allocate different values in another run.

```json
{
  "schema": 6,
  "verified": true,
  "entry": 0,
  "library": false,
  "functions": [
    {
      "id": 0,
      "name": "main",
      "runtime": "None",
      "specializationArguments": [],
      "returnType": 1,
      "effects": [],
      "parameters": [],
      "slots": [],
      "body": {
        "kind": "FirBlock",
        "type": null,
        "start": 0,
        "length": 24,
        "value": {
          "statements": [
            {
              "kind": "FirEvaluate",
              "type": null,
              "start": 4,
              "length": 17,
              "value": {
                "value": {
                  "kind": "FirCall",
                  "type": 2,
                  "start": 12,
                  "length": 5,
                  "value": {
                    "target": 1,
                    "typeArguments": [2],
                    "arguments": [
                      {
                        "parameter": 0,
                        "mode": "Value",
                        "value": {
                          "kind": "FirConstant",
                          "type": 2,
                          "start": 20,
                          "length": 1,
                          "value": { "spelling": "1" }
                        }
                      }
                    ]
                  }
                }
              }
            }
          ]
        }
      }
    },
    {
      "id": 1,
      "name": "identity",
      "runtime": "None",
      "specializationArguments": [2],
      "returnType": 2,
      "effects": [],
      "parameters": [0],
      "slots": [
        { "id": 0, "name": "value", "type": 2, "mutable": false, "mode": "Value", "isParameter": true }
      ],
      "body": {
        "kind": "FirBlock",
        "type": null,
        "start": 25,
        "length": 24,
        "value": {
          "statements": [
            {
              "kind": "FirReturn",
              "type": null,
              "start": 25,
              "length": 24,
              "value": {
                "value": {
                  "kind": "FirRead",
                  "type": 2,
                  "start": 45,
                  "length": 5,
                  "value": { "slot": 0 }
                }
              }
            }
          ]
        }
      }
    }
  ],
  "types": [
    { "id": 1, "kind": "Tuple", "name": "()", "arguments": [], "parameterModes": [], "effects": [] },
    { "id": 2, "kind": "Primitive", "name": "int", "arguments": [], "parameterModes": [], "effects": [] }
  ],
  "flow": []
}
```

This is intentionally illustrative rather than a golden snapshot. The spans and IDs are made up to
show the shape, and the example does not assert a specific allocation order. Do not copy placeholder
IDs into a fixture and assume they are stable.

For a generic call, the structural relationship is:

```text
ordinary function body
    FirCall.target          -> functions[target].id
    FirCall.typeArguments   -> [type-id for each target generic parameter]
    functions[target].specializationArguments -> same ordered type-id list
    FirCall expression type -> functions[target].returnType
```

The source verifier establishes more than this JSON relationship. It checks that the selected source
binding, declaration identity, type substitution, parameter mapping, and effects all agree. A reader
that checks only the arrows above validates references, not source correctness.

## Payload decoding notes

The writer serializes each node as a common envelope and a node-specific `value` object. A reader
should keep those layers distinct. It should first inspect `kind`, `type`, `start`, and `length`, then
decode the `value` according to the known node kind.

| Node kind | Important payload fields | Reader note |
| --- | --- | --- |
| `FirBlock` | `statements` | Statement order is execution order |
| `FirConstant` | `spelling` | Display spelling is not the complete typed value |
| `FirRead` | `slot` | Slot reference is function-scoped |
| `FirUnary` | `operation`, `value` | Operation label is not necessarily the source token |
| `FirBinary` | `operation`, `left`, `right` | Child nodes retain their own types and origins |
| `FirCall` | `target`, `typeArguments`, `arguments` | Function and type IDs are document-local |
| `FirFunctionReference` | `target` | Reference has no captured receiver or closure environment |
| `FirIndirectCall` | `function`, `arguments` | Signature, modes, and effects come from the function type |
| `FirIntrinsic` | `operation`, `order`, `arguments` | `order` is not a generic type tuple |
| `FirArrayLength` | `array` | Child type describes the array expression |
| `FirArrayIndex` | `array`, `index` | Result type is on the parent envelope |
| `FirArrayLiteral` | `elements` | Element ordering is semantic |
| `FirDeclare` | `slot`, nullable `initializer` | Null initializer is not source null |
| `FirStore` | `slot`, `value` | Slot must exist in the enclosing function |
| `FirEvaluate` | `value` | The expression's result is discarded |
| `FirReturn` | nullable `value` | Null is a no-value return in the admitted subset |
| `FirIf` | `condition`, `then`, nullable `else` | Missing else stays distinguishable from a source block |
| `FirLoop` | `initializer`, `condition`, `body`, `update` | Nullable components represent the FIR loop form |
| `FirArrayForEach` | `itemSlot`, `array`, `body` | Item slot is scoped to the owner function |
| `FirBreak` | empty object | Control-flow meaning comes from the node kind |
| `FirContinue` | empty object | Control-flow meaning comes from the node kind |

Unknown node kinds should not be decoded as if they were a known variant. A best-effort viewer can
display their raw `value` but should mark the view as partial. A verifier or cache loader must require
full understanding and reject unsupported variants.

## Type and effect inspection workflow

When a user inspects a specialization, use the following order:

1. Find the function record by its document-local `id`.
2. Identify the source declaration from the retained compilation context, not from `name` alone.
3. Read `specializationArguments` in their exact serialized order.
4. Resolve each type ID in the same document's `types` array.
5. Compare the resolved argument structure with the declaration's parameter ordinals.
6. Check that parameter, result, and effect types are consistent with those arguments.
7. Follow call targets only inside the same function table.
8. Preserve the call node's source origin when displaying shared instances.

For each effect ID, distinguish the declared function effect set from effects attached to individual
call edges. The former is a signature obligation; the latter is a flow observation. Neither field
contains a source-language exception message or a stable runtime class number. Any display of effect
names should resolve the ID through the current document table and source context.

If a reader sees a type mismatch, it can report that the JSON relationships are inconsistent. It
cannot decide whether the original source program was well-typed without the source analysis model.
If the original compiler reported `SOL2402`, that is the authoritative verifier boundary. A
standalone JSON parser must not manufacture a replacement verifier result.

## Diagnostic presentation patterns

The three FIR boundary codes lead to different useful explanations:

| Situation | Suggested user-facing explanation | Follow-up evidence |
| --- | --- | --- |
| `SOL2400` | Source analysis accepted the program, but this native FIR path does not support the selected construct or type representation yet. | Source span, selected target, unsupported operation or type |
| `SOL2402` | The FIR verifier found that generated IR disagrees with source analysis or violates an internal invariant. | FIR node origin, invariant message, compiler revision |
| `SOL2403` | The compiler stopped because generic specialization exceeded a documented resource bound. | Which limit, observed count/depth, requesting source origin |

Do not tell users to change their generic inference when the failure is `SOL2400`. Do not tell them
to choose another overload when overload selection already completed. Do not suppress `SOL2402` as a
mere unsupported feature. Do not invite users to increase a limit for an inference failure that did
not reach specialization. A resource limit must identify the actual budget dimension.

If a command displays both source diagnostics and native admission errors, group them by phase and
keep their codes intact. A source failure can prevent FIR construction before any of these three
codes are relevant. An inspection serializer should not invent a diagnostic list that was not passed
to it.

## Source identity and repeat spans in IDEs

An IDE can use the in-memory source workspace to navigate the current compile. It must be careful
when it persists the JSON or sends it to a second process. Schema 6 does not say which file owns a
particular span. The same function body can be represented in several monomorphic records, each
preserving the same origin span while having a different specialization tuple. These records are
separate generated functions backed by one source body.

An IDE should not deduplicate those records by `(name, start, length)`. It should retain the
specialization tuple and the actual source declaration identity while the workspace exists. If only
the JSON remains, it can display the local function IDs and type tuples but should state that source
identity is incomplete.

For future file IDs, use explicit compilation-scoped IDs and a table mapping them to policy-approved
relative paths or opaque labels. A path should not be inferred from the current working directory.
The table needs to describe generated inputs, virtual buffers, and repeated module roots. It should
not leak local usernames or checkout paths.

## Failure atomicity

Successful verified JSON represents the whole program that passed verification. It is not an
incremental checkpoint for a partially expanded specialization queue. If adding a new generic call
would exceed a cap, the build fails with `SOL2403`; no schema-6 root with `verified: true` should be
written for the partial closure. This keeps every target ID resolvable and every emitted generic
instance reachable under the verifier's graph rules.

Likewise, `SOL2400` means the native builder rejected a source shape before a verified object exists.
`SOL2402` means verification refused the candidate. Both must stop verified FIR serialization for
that candidate. A separate command may still emit source diagnostics or a partial analysis report,
but that is a separate output contract and must not be mislabeled as schema-6 verified FIR.

This atomicity is important for downstream tools. A viewer can rely on references being internally
closed in a complete document. A cache cannot treat a truncated graph as a complete dependency set.
An LLVM backend cannot use the inspection document as a fallback input when verified FIR is absent.

## Review checklist for schema changes

Before changing `FirJson`, answer these questions in the code review:

- Which root, function, slot, type, node, or flow fields change?
- Does the change alter the scope or ordering of any integer ID array?
- Are generic parameter ordinals still explicit in the stored argument order?
- Does every new field have a source or verifier invariant that owns it?
- Does an absent field differ from an empty array or a JSON null value?
- Does the writer still require a `VerifiedFirProgram`?
- Can a stale reader detect that it cannot interpret the new shape?
- Does the change expose paths, source text, user names, or environment data?
- Could a reader mistake inspection data for a stable ABI or cache entry?
- Do the normative spec, examples, docs, and downstream readers describe the same schema?

If the change adds serialized diagnostics, decide whether they belong in this document or a separate
diagnostic report. If they are added here, each diagnostic needs a code, severity, phase, origin,
related locations, and a file identity strategy. Do not overload the node `type` field with severity
or reuse the schema integer as a diagnostic code.

## Maintainer examples

The following pseudocode describes a safe consumer join. It is not a public API:

```text
document = parse_json(bytes)
require document.schema == 4
functions = unique_map(document.functions, key = function.id)
types = unique_map(document.types, key = type.id)
for function in document.functions:
    for type_id in function.specializationArguments:
        require types.contains(type_id)
    for node in walk(function.body):
        if node.kind == "FirCall":
            require functions.contains(node.value.target)
            for type_id in node.value.typeArguments:
                require types.contains(type_id)
```

The pseudocode verifies basic references only. It cannot re-run source binding or prove that a call
target is the same overload selected from the original program. That proof uses the source identity
and analysis facts retained inside `FirVerifier`.

For a viewer that wants to render a human-readable specialization label, it may display something
like `identity<int>` by combining the function's display name with resolved types from the same
document. It must label this as a display string. It must not reuse it as a C export symbol, cache
key, or dynamic loader lookup name. Those uses require a separately versioned naming contract.

The inspection interface is deliberately conservative: it exposes enough structure to understand
which closed instances and calls the verifier accepted, while preserving clear boundaries around
source identity, artifacts, and persistent serialization.
