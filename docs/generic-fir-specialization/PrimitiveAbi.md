<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->

# Generic specialization: primitive and String ABI

This guide describes how a closed generic free-function instance can cross the current native ABI
boundary. It is for Sol users who need to understand which type substitutions can reach verified
FIR and for compiler maintainers who implement the substitution-to-native path.

The document is deliberately narrower than the Sol type system. A type being legal in a declaration
or accepted by source analysis does not prove that its concrete body has a verified FIR layout. The
native backend admits a type only when the signature, every body expression, every call edge, every
local slot, and every runtime operation have one independently checked representation.

The companion normative contracts are [generic specialization](../../spec/GenericFirSpecialization.sol)
and the [primitive ABI examples](../../spec/examples/semantics/GenericFirPrimitiveAbi.sol). The
specialization contract remains the source of truth for semantics; this guide explains the concrete
native representation and its current limits.

## 1. Scope

The first native slice is limited to direct calls to module-level generic free functions. It does not
specialize generic classes, structs, ref structs, interfaces, objects, constructors, properties,
accessors, operators, local functions, or closures.

An instance consists of a source declaration and a complete ordered list of closed type arguments.
For `func <T> identity(value: T): T = value`, `identity<int>` and `identity<String>` are two
different instances even though both share the same syntax tree.

Each instance gets a concrete parameter signature, result type, effect set, local-slot types,
expression types, and nested call targets. An open type parameter must not survive into the verified
program.

The target is an ordinary monomorphic FIR function. No generic runtime descriptor, hidden type token,
dynamic dispatcher, or user-visible generic ABI is added.

The LLVM backend receives verified FIR and constructs the LLVM module through the C++ API bridge.
The compiler does not generate LLVM IR text and then parse it.

This document makes no claim that every source-level inference feature is implemented in FIR. A
candidate can be successfully bound by source analysis and still be rejected by the native subset.

The required response to an unsupported substitution is a diagnostic at the concrete call or source
body location. The compiler must not invent a layout to keep compilation moving.

## 2. Supported native type matrix

The current FIR verifier recognizes the following scalar source types for slots, parameters,
results, and closed generic arguments:

| Sol type | Native representation | Notes |
| --- | --- | --- |
| `bool` | `i1` | Boolean operations remain type-checked. |
| `sbyte` | `i8` | Signed 8-bit integer. |
| `short` | `i16` | Signed 16-bit integer. |
| `int` | `i32` | Signed 32-bit integer. |
| `long` | `i64` | Signed 64-bit integer. |
| `byte` | `i8` | Unsigned 8-bit integer. |
| `uint` | `i32` | Unsigned 32-bit integer. |
| `ulong` | `i64` | Unsigned 64-bit integer. |
| `single` | `float` | IEEE single-precision operations use the selected FIR rule. |
| `double` | `double` | IEEE double-precision operations use the selected FIR rule. |
| `String` | managed pointer | `std.core.String` has a dedicated runtime contract. |

`half` and `quad` are language built-ins but are outside the current LLVM native subset. Their
presence in the language does not imply that they can be function parameters or generic arguments in
verified native code.

The table describes value representation only. It does not mean every operator is valid for every
row. For example, a Boolean has an integer-like LLVM storage type but Sol does not thereby permit
arithmetic on `bool`.

Similarly, String is pointer-shaped at the native ABI, but a String pointer is not interchangeable
with `Object`, an arbitrary class reference, an exception object, a delegate, or an array pointer.

The supported array forms are one-dimensional managed arrays whose element is one of the admitted
scalar types or `String`:

| Source array | Element representation | Native status |
| --- | --- | --- |
| `[]bool` | `i1` | Admitted by the current closed type check. |
| `[]sbyte`, `[]short`, `[]int`, `[]long` | Signed integer element | Admitted by the current closed type check. |
| `[]byte`, `[]uint`, `[]ulong` | Unsigned integer element | Admitted by the current closed type check. |
| `[]single`, `[]double` | Floating element | Admitted by the current closed type check. |
| `[]String` | Managed reference element | Uses the qualified array element kind and root map. |
| `[][]int` | Nested pointer element | Rejected by the current native type check. |
| `[]String?` | Nullable reference element | Rejected until nullable element layout is verified. |
| `[]Window` | Ref-like element | Rejected; it cannot be boxed or treated as a pointer. |

Only the direct one-dimensional array element contract is covered here. Fixed arrays, slices, spans,
vectors, user-defined collection types, and arbitrary arrays-of-arrays require independent storage,
ownership, root, and lifetime contracts.

An array's element ABI is checked after the generic substitution is applied. It is not enough that the
outer type is `[]T`; the substituted `T` must itself have a supported element representation.

## 3. Scalar calling convention

The LLVM signature for a specialized scalar function is derived from its closed FIR parameter and
return types. The source type identity is retained until the verifier has checked signedness,
width, result type, and call argument mode.

| Sol parameter type | LLVM argument type | FIR identity that must remain attached |
| --- | --- | --- |
| `sbyte` | `i8` | `sbyte`, signed |
| `byte` | `i8` | `byte`, unsigned |
| `short` | `i16` | `short`, signed |
| `int` | `i32` | `int`, signed |
| `uint` | `i32` | `uint`, unsigned |
| `long` | `i64` | `long`, signed |
| `ulong` | `i64` | `ulong`, unsigned |
| `bool` | `i1` | `bool`, logical |
| `single` | `float` | `single`, floating |
| `double` | `double` | `double`, floating |

The LLVM storage width is not a source type identity. `sbyte` and `byte` share an LLVM width but do
not share a specialization key, signedness rule, overflow rule, or conversion rank.

The same restriction applies to `int` and `uint`, `long` and `ulong`, and any future types that happen
to use the same register class.

The lowering must preserve source-level checked arithmetic and overflow semantics. A generic function
body such as `left + right` is not lowered as an unqualified machine add; it is admitted only after
the selected typed-FIR operator rule has resolved and the LLVM operation matches that rule.

The LLVM `float` or `double` representation is likewise not sufficient by itself. The verifier checks
that a floating operation has floating operands of the same specialized type and a matching result.

There is no implicit numeric cast in the specialization layer. Source analysis may classify a
conversion, but verified FIR needs an explicit, typed operation for that conversion before it can
cross the native call boundary.

An integer literal may be assigned the selected formal type when the source literal rules permit it.
That does not authorize converting an already typed local from one integer type to another.

For example, a literal passed to `identity<long>` may be checked under the literal-target rule; a
local `sbyte` passed to that parameter needs a real conversion node and a verifier rule.

If the frontend reports an applicable source call but the FIR builder cannot represent the selected
conversion, native compilation must fail closed. It must not emit the smaller value directly and
pretend the LLVM parameter type was satisfied.

The same exactness applies to return expressions. The value produced by a specialized body must
match the specialized result type or an explicit typed conversion represented by FIR.

## 4. String ABI

`String` is the only nominal reference type in this first scalar matrix. It is recognized by its
canonical declaration identity in `std.core`, not by the source spelling alone.

A user declaration named `String` in another module does not receive the standard String ABI. A
type alias, wrapper class, subclass, or structurally similar class also does not become String merely
because it contains text.

String values cross a specialized call as managed references. The runtime uses its own object
descriptor, literal creation, length, comparison, hashing, and write operations where those FIR
operations are explicitly supported.

String references participate in root tracking at safepoints. A specialized function must provide
the same root metadata as the equivalent non-generic function with a `String` parameter or local.

The generic call itself does not copy or pin a String, erase its identity, or attach a type parameter
descriptor to the object.

`identity<String>(value)` is therefore a permitted shape when the body consists of supported
operations. `identity<Object>(value)` is not a substitute for the dedicated String case.

String interpolation, regex, culture-sensitive formatting, encodings, and arbitrary method dispatch
are not implied by the pointer ABI. They need their own semantic and runtime operations.

Nullability is not ignored. `String?` is a distinct closed source type that requires a null-state
representation and flow/GC proof. The present scalar matrix does not accept it merely because
`String` is supported.

The same is true for a nullable String array element. A pointer map that marks every array element as
non-null would be invalid for `[]String?`.

## 5. Arrays, GC roots, and element access

The array cases admitted here use the existing dynamic-array ABI. An array is passed as a managed
reference; its element layout is selected from the closed element type.

The array descriptor and data must agree on the element kind. A `[]String` instance cannot use the
integer element kind, and a `[]ulong` instance cannot use the signed `long` element kind.

When a generic body reads `values[index]`, FIR records the specialized array type, an `int` index, and
the exact element result type. The verifier rejects an index result that does not match the array's
element.

When a generic body reads `values.length`, the result type is `int` independent of `T`. The operation
still verifies that the receiver is a supported dynamic array or String.

When a generic body creates an array literal `[value]`, the element expression and result array must
use the same substituted type. It cannot be emitted as `[]Object` and cast back to `[]T`.

The compiler must root reference-bearing local slots before any runtime operation that may collect.
For `T = String`, a slot of type `T` becomes a String root in the specialized function.

For `T = int`, the same source slot becomes a scalar and is not reported as a GC root. The root map
must be recomputed for each instance from its concrete layout.

This distinction is why a generic template body cannot be lowered once and shared between
pointer-shaped and integer-shaped substitutions.

A `[]String` may contain moving references under the runtime's GC policy. Array element loads and
stores must use the runtime's defined barrier and root behavior; the specialization stage must not
cache an element address across a safepoint without a proof.

Nested arrays require a pointer element map and a recursive descriptor contract. Until that is
implemented and independently verified, `[][]T` is outside the accepted set, even if the source
analysis can represent it.

Nullable element references, pinned spans, ref-like arrays, fixed arrays, and borrowed views each
need a distinct lifetime/ownership contract. They must not be encoded as a direct `[]T` element kind.

The first native slice covers array values only where the existing FIR verifier already recognizes
the array expression, indexing, length, and root operations. It does not imply support for every
library collection API.

## 6. Body operation admission

The substitution closes type identities; it does not make an invalid generic body valid. Each
operation in the body must be analyzed for the concrete type and admitted by the FIR verifier.

For a pass-through body such as `value`, a generic function can be specialized for any type in the
ABI matrix, subject to parameter/result and source conversion checks.

For an arithmetic body, both operands, the operation's source binding, and the result must agree with
the operator contract for the selected type. A Boolean or String substitution does not inherit
integer addition from a shared machine width or pointer representation.

For an equality or ordering operation, the verifier requires the specific built-in or selected
operator semantics. The existence of an LLVM `icmp` or `fcmp` instruction does not automatically
authorize a Sol comparison.

For a branch, the condition must become `bool` and every reachable result path must produce the
specialized function result. Generic body cloning may not drop a path because one branch appears
unreachable under a guessed runtime value.

For a local, the slot type is substituted before it is created. A local declared as `T` in a body
must be `int` in the `int` instance and String in the String instance.

For a return, the expression is checked against the specialized declared or inferred return. Source
body inference and FIR verification are separate checks; the latter must independently confirm the
closed result.

For a call, the selected target id, ordered type arguments, parameter mapping, passing mode, result
type, and effects are preserved. A same-name overload is not a fallback if the specialized target
fails verification.

For a call to an intrinsic, only the registered intrinsic signature is admitted. Generic type
arguments do not make an unknown intrinsic operation valid.

For `ref` and `out`, substitution preserves the passing mode. The actual must still be writable
storage of the exact specialized type, and the CFG must prove out assignment on normal return.

For an array loop, the iterable is evaluated under the source order contract, and the specialized
item slot uses the substituted element type. The loop's flow and GC behavior are verified after
substitution.

For a checked effect, the effect type is substituted and compared against the selected caller's
effect budget. A generic body cannot erase or widen the checked effect set.

The current FIR subset rejects several otherwise valid Sol constructs, including cleanup, many
exception shapes, defaults, expanded user varargs, closures, delegates, and user conversions. A
generic declaration containing one of those constructs does not bypass the rejection.

The user should receive the existing unsupported-native diagnostic with a source span. The compiler
must not report successful native output for a partially lowered body.

## 7. Type argument and call mapping

The type arguments stored on a selected body call are ordered by the generic declaration's canonical
parameter identity. Spelling and call-site order are not identity.

For a declaration `func <A, B> choose(first: A, second: B): A`, the key is `(A, B)`. A source call
that supplies named arguments in the order `second`, then `first` still records `(A, B)`.

Explicit type arguments fix the selected values. The final arguments, constraints, effects, and
expected result are still checked against those values.

Inferred type arguments are recorded only after the candidate-local inference algorithm has reached
a unique result. A partial substitution is not an instance key.

Expected-result evidence may determine a type parameter when the inference contract permits it.
After the candidate wins, the resulting result type is exact; native lowering does not perform a
second inference pass.

Defaults do not provide arbitrary type evidence. They are evaluated only after candidate selection
under the selected closed substitution and only if the default lowering is supported by FIR.

Varargs and spread arguments require the call-mapping contract to define how each source expression
maps to the specialized parameter. The generic ABI does not synthesize a vararg buffer implicitly.

`ref` and `out` evidence is exact. A mutable `sbyte` cannot become an `out long` merely because a
source conversion might exist for ordinary value arguments.

If one type argument is unsupported, the entire ordered tuple is rejected. The compiler must not
keep a prefix of the argument list or publish an incomplete instance.

## 8. Specialization identity and worklist

A specialization key combines canonical generic declaration identity with the ordered closed type
argument identities. It is local to one compilation's type workspace.

For the same declaration and same ordered tuple, repeated calls reuse one function id. The target is
stable independent of call count.

For the same declaration and different tuples, the compiler creates distinct function identities.
The source origin may be shared, but function id, signature, body slots, call edges, and GC roots are
not shared mutable state.

The worklist is deterministic. Declarations are processed in source-analysis order; nested calls are
added in a stable source traversal order; instance keys use canonical type identities.

Before a function id is published, the compiler checks that the tuple is complete, closed, within
the native type matrix, and satisfies the declared generic constraints.

The function id is reserved before its body is traversed. A recursive call with the same key points
back to that reserved instance and does not create an unbounded chain of clones.

A recursive call whose type tuple grows creates additional distinct keys. The compiler limits graph
growth and reports a resource diagnostic instead of exhausting memory or stack.

The current implementation applies bounded limits for the number of instances and type nesting. The
exact resource policy is in the normative specialization specification and must be changed in one
place with corresponding diagnostics and documented guarantees.

The key is not persisted across compiler processes. A `ResolvedType.Id` is a workspace-local id and
must not be treated as a durable package-level type identity.

FirJson is diagnostic/inspection output. Its schema records instance and call type arguments by
workspace type id for this compilation; it is not a binary module format or reusable cache database.

Any future persistent generic cache needs stable module/declaration content identity, compiler and
runtime ABI versions, target triple, type-layout identity, all transitive specialization dependencies,
and validation for hostile or stale cache entries.

## 9. Verifier obligations

The verifier receives the entire FIR program and its source analysis. It does not trust the builder's
claim that a type tuple is valid.

For each generic FIR function, it verifies that the original source declaration is unique and
module-level, the argument count matches, all arguments belong to the current type workspace, and no
open generic parameter remains in the tuple.

The verifier checks that each argument is admitted by the native type matrix and that the declaration
constraints are satisfied. A failed or indeterminate constraint check invalidates the instance.

It builds a declaration-owned substitution map from source generic parameter identity to closed
argument. It then substitutes and compares the source body result, function parameters, effects, and
each body expression/slot type with the corresponding FIR metadata.

For every call, the FIR call arguments are compared with the source-bound call mapping. The call's
type arguments must equal the source binding's type arguments after applying the caller instance's
substitution.

The call target must be the function instance whose source declaration and specialization tuple
match the selected target. Equal source origins alone are insufficient to identify an instance.

The call result type and effects must match the specialized source binding and target signature.
Argument count, parameter indices, value origins, named mapping, passing modes, ref/out storage,
and exact native parameter types are checked independently.

The verifier rejects duplicate specialization keys. It also rejects a generic function with missing
or default-initialized specialization metadata and a nongeneric function with nonempty specialization
metadata.

The verifier runs before LLVM module construction. A `VerifiedFirProgram` is the only input accepted
by the native emitter.

If any specialized function or reachable edge fails, compilation returns no verified program. The
compiler does not emit a partially valid artifact containing the remaining functions.

## 10. Constraints and non-admitted types

Generic constraints are source declarations, not native layout evidence. A `where` clause can prove
that a type offers an operation or implements an interface while leaving its native object layout
unknown.

The first native slice therefore requires both a satisfied source constraint and a supported concrete
layout. Constraint success cannot widen the ABI matrix.

The standard `Object`, `Throwable`, `Exception`, and `RuntimeException` types are nominal. Their
availability in `std.core` does not mean their class layouts are supported as generic values.

Application classes, interfaces, enums, generic nominal applications, delegates, closures, tuples,
function types, nullable wrappers, ref-like values, and spans remain outside this primitive/String
boundary unless another verified FIR contract explicitly admits them.

`String` is an explicit exception to the nominal-type exclusion because its layout and runtime
operations are separately known. That exception does not extend to arbitrary String-like values.

Ref-like values cannot be boxed to sneak across the pointer ABI. A generic call must obey the same
escape and lifetime restrictions as the source declaration.

The type checker may support a larger set of types than the native verifier. The narrower native
boundary is an intentional capability check, not a promise that source-level type support is absent.

## 11. Diagnostics and recovery

An unsupported scalar or array type reports a native-subset diagnostic at the specialization call or
the unsupported source body node. The diagnostic should show the concrete closed type when known.

An unsatisfied generic constraint remains a source applicability error. It is not rewritten as a
native ABI diagnostic.

A resource limit reports a deterministic specialization diagnostic. Re-running the same compilation
with the same options must fail at the same first over-budget source edge.

An open or missing argument reports an inference/call-binding diagnostic. It must not be converted to
an arbitrary default type to avoid a later verifier failure.

An unsupported nested body call invalidates the enclosing instance and all callers that depend on
it. No wrapper with an empty or placeholder body is emitted.

Error recovery may continue checking independent declarations, but it cannot publish a verified FIR
program if a reachable call or body has failed.

The source span used for a call failure is the written or inferred generic call site. A body failure
uses the source operation that could not be substituted or lowered.

## 12. Example review checklist

When adding a positive example, confirm that it has one module-level generic declaration, a complete
closed type tuple, supported concrete types, a supported body, and a direct statically selected call.

For every positive generic function, inspect the specialized parameter list, result type, local slots,
call targets, effects, and roots. Do not check only that `FirCompilation.Program` is non-null.

For every unsupported example, identify whether failure belongs to source binding, constraint
checking, native type admission, generic owner restrictions, or body operation verification.

Include one same-type repeated-call scenario and one different-type scenario. They exercise cache
reuse and instance separation, respectively.

Include a generic-to-generic forwarding scenario so caller substitution is tested across an edge.
Include recursion with a stable tuple and a growing tuple to exercise worklist termination and limits.

Include an array of `String` scenario to verify moving-reference roots and a scalar array scenario to
verify a non-reference element kind.

Include `half`, `quad`, nullable, nested-array, ref-like, and nominal-object negative cases. Do not
label these as source errors if source analysis accepts them and FIR rejects them.

For argument conversion scenarios, distinguish numeric literal target typing from conversion of an
already typed local. The latter needs a typed FIR conversion before it is a native success.

For JSON inspection, assert schema version, deterministic output, function specialization metadata,
call type arguments, and absence of machine-local paths. Treat type ids as local diagnostics only.

For native output, verify the actual LLVM module against the required LLVM 23.1.3 bridge version.
Do not use textual IR assembly as an alternate implementation route.

## 13. Expanding the ABI matrix

To admit a new scalar, define its exact Sol identity, width, signedness, LLVM type, literal rules,
overflow behavior, comparison operations, parameter and result ABI, and native export policy.

Define its printing and formatting behavior, array element kind, GC scanning behavior, array load/store
rules, root behavior at safepoints, and any runtime helper it requires.

Define its interaction with nullable wrappers, tuples, function types, generic constraints, arrays,
and cross-module calls. If any of these remain unsupported, the verifier must continue to reject them.

Update the FIR source contract and verifier together. The emitter must consume already-verified types;
it must not be the first component that discovers a type is unsupported.

Update the normative Sol specification and the semantic example matrix. Keep planned language support
separate from the currently verified native subset.

Update package, artifact, and public API metadata only when the corresponding ABI is genuinely
supported. A change to a compiler-internal representation must not silently change a `.solib` ABI.

Keep all source files under the repository's 750-line preferred limit. If this document grows beyond
that limit, split it by one of the stable ABI boundaries while retaining links from the index.

## 14. Maintainer summary

The primitive/String matrix is a closed list. It is not a best-effort guess based on LLVM register
types.

The generic instance is monomorphic before FIR verification. Type arguments are part of the function
identity and the call edge.

The body is substituted completely. Open parameters cannot remain in types, effects, slots, calls, or
runtime roots.

Source inference and native admission are separate. A successful source binding is necessary but not
sufficient for verified native output.

Every instance is independently checked for source identity, constraints, closed arguments, exact
signature, body operations, call mappings, effects, and supported runtime layout.

When the backend cannot prove a representation, it rejects the instance and reports the source
location. It does not erase the type, box it, reinterpret it, or emit incomplete code.
