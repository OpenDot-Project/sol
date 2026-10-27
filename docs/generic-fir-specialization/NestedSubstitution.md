<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Nested generic substitution in FIR

This guide describes how a closed generic caller forwards type arguments to another generic
free function while the compiler builds verified FIR. It is a maintainer document for the bounded
native slice. It does not state that all generic Sol programs can be specialized.

The language-facing contract is in [`spec/GenericFirSpecialization.sol`](../../spec/GenericFirSpecialization.sol).
Its focused source matrix is [`GenericFirNestedSubstitution.sol`](../../spec/examples/semantics/GenericFirNestedSubstitution.sol).
The broader candidate-binding contract is in [`generic-inference.md`](../generic-inference.md).

## Current implementation status

The managed Analysis layer retains ordered type arguments on each `BodyCallBinding`. This is
necessary because a substituted parameter/result signature alone cannot reconstruct arguments that
were inferred from expected-result evidence or explicit syntax, nor preserve a generic parameter that
does not occur in the callable signature.

The FIR builder seeds ordinary functions, reserves closed specializations for selected generic
free-function calls, and processes a deterministic pending queue. When it builds one instance, it
substitutes the instance's concrete type map into body expression types, local slots, effects, and
nested call type arguments. An inner call can therefore request an instance whose original type
argument was the outer function's open `T`, after `T` has been closed at the root.

The verifier checks that each generic FIR function has a closed, ordered argument list; that those
arguments satisfy the generic declaration's constraints; and that its signature, result, effects,
call target and call-site type arguments agree with the source analysis after composing the caller's
substitution. The verified monomorphic functions use the existing LLVM 23.1.3 C++ API bridge path.

This is a bounded projection of already-produced source-analysis facts. The compiler does not yet
re-run the full source binder for every concrete instance. Consequently, this implementation is not
a claim that every generic operator, conversion, flow fact, effect pattern, or member lookup has been
rechecked under every concrete substitution. The native FIR gate rejects unsupported shapes.

## Terms

| Term | Meaning |
| --- | --- |
| Generic parameter identity | A canonical parameter owned by one declaration and identified by its ordinal. |
| Caller substitution | A mapping from the caller declaration's parameters to its closed concrete types. |
| Call binding | The source-selected target plus argument mapping, signature, result, effects and type arguments. |
| Callee arguments | The ordered type identities selected for the called declaration's own parameters. |
| Instance substitution | The mapping from one generic callee's parameters to its closed callee arguments. |
| Function instance | One generic source declaration paired with one ordered closed type-argument tuple. |
| Call origin | The original source call expression and span, retained even when an instance is shared. |
| Open parameter | A type parameter still present in an argument after composing the caller substitution. |

A source spelling is not a parameter identity. Two functions that both declare `T` have different
owners. A call expression relates those declarations by evidence and argument mapping; spelling
equality does not.

The identity model is important for nested calls. Consider:

```sol
module docs.generic_fir_specialization
func <T> identity(value: T): T = value
func <Outer> forward(value: Outer): Outer = identity(value)
```

`identity` owns `T@identity/0`; `forward` owns `Outer@forward/0`. The fact that the body call is
analyzed while the caller remains generic does not make these identities interchangeable. At
`forward<int>(1)`, source analysis records the inner argument as the outer parameter identity, then
FIR composes the caller mapping and obtains `identity<int>`.

## Composition rule

Let the selected caller declaration have parameters `P0...Pn` and closed root arguments `R0...Rn`.
Its map is `Sroot = { P0 -> R0, ..., Pn -> Rn }`.

Let a source call in that body select a callee declaration with parameters `Q0...Qm`. The body
binding records the call's declaration-ordered type arguments `A0...Am`. Before constructing the
callee instance, each argument is rewritten with the caller map:

```text
closed_i = Substitute(A_i, Sroot)
Scallee  = { Q0 -> closed_0, ..., Qm -> closed_m }
```

`Substitute` is type-identity substitution, not a search-and-replace over source text. It traverses
compound types and returns identities owned by the same specialization workspace. It does not
rewrite the result of a replacement a second time. If `P -> Q` and `Q -> int` are simultaneously in
a map, the original occurrence of `P` becomes `Q`; it is not transitively rewritten to `int` in the
same pass.

The caller map and callee map have separate lifetimes. The callee function body is built with
`Scallee`, while the caller's body continues to use `Sroot`. The callee's result type is substituted
using its own map. Its caller expression result is then checked against the caller-substituted
`BodyCallBinding.ReturnType` and the target's concrete FIR return type.

The same composition applies to effects. The selected target's declared source effects are
substituted using the caller map, then compared with the effects stored on the concrete target
instance. Checked-effect budget validation remains associated with the caller function currently
being verified.

## Ordered arguments and independent declarations

Arguments are ordered by the target declaration's generic parameter ordinal. They are not ordered by:

- the lexical order in which caller parameters happen to appear;
- the order of values after named-argument reordering;
- the order in which evidence was discovered;
- the order in which the generic declarations were declared in a file;
- the source spelling of type parameters;
- the hash-table order of substitution entries.

For example:

```sol
module docs.generic_fir_specialization
func <T, U> chooseSecond(first: T, second: U): U = second
func <A, B> reorder(valueA: A, valueB: B): B = chooseSecond<B, A>(valueB, valueA)
```

The inner tuple is `[B, A]`, in `chooseSecond` declaration order. Its first argument is the outer
`B`; its second is outer `A`. A positional copy of the outer tuple `[A, B]` would be incorrect.

For inferred type arguments, source analysis first maps each argument to its formal parameter and
solves the candidate. FIR receives the selected ordered type-argument vector; it does not rerun
candidate ranking and must not reconstruct the vector from one substituted formal type. This is
especially important when a parameter is unused by the result or when expected-result evidence
closes it.

### Same-spelling parameters

```sol
module docs.generic_fir_specialization
func <T> idA(value: T): T = value
func <T> idB(value: T): T = idA(value)
```

`T@idA/0` and `T@idB/0` are distinct identities. If an `idB<int>` instance calls `idA(value)`,
the selected binding carries `T@idB/0` as the inner argument, and the caller map changes it to `int`.
The target's own substitution maps `T@idA/0` to that resulting `int`.

This relationship must remain correct when the parameter names differ, when only one argument is
used, and when a function declares several parameters with repeated occurrences in its signature.
The ordinal is based on declaration order, not on identifier text.

## Root discovery and pending instances

The current builder creates IDs for ordinary, nongeneric source functions first. Their bodies are
converted to FIR. While a body is converted, a selected generic free-function call asks the builder
to reserve an instance ID and queues the `(declaration, closed type arguments, ID)` tuple.

The builder drains that queue in FIFO order. Building a generic body may append more closed instances
to the same queue. Reserving the ID before lowering the body is what lets exact recursive calls refer
to the pending instance rather than recursing indefinitely in the host call stack.

The queue is local to one `FirBuilder` and one `BodyAnalysis` workspace. The builder does not use
serialized type IDs from another compilation, an on-disk cache, a stable symbol hash, or a global
process cache to decide equality.

The deterministic order currently follows the source function order and expression traversal order
within the same input snapshot and configuration. Function and type IDs are local inspection IDs.
Changing the file enumeration or inserting another source type may change those IDs without
changing the semantic declaration/type tuple or the call-to-target relationship.

A future persistent code cache needs a separately versioned canonical key, source-content digest,
compiler semantic version, target triple, language edition and runtime ABI namespace. The current
in-memory `(AstFunction identity, type-ID tuple)` key is not a persistent cache key and must not be
used as a public ABI.

## Deduplication and recursion

Two calls to one declaration with the same closed argument tuple use one FIR function instance in
the current workspace. They remain two call nodes, with two origins and independent argument
expressions. Instance reuse does not authorize common-subexpression elimination or any change to
source evaluation order.

Two different declarations with the same name and type arguments remain different instances. A
single declaration with `int` and `String` substitutions produces different instances. Equality is
not based on a display name or a signature string.

### Exact recursion

A function that recursively calls itself with the exact same declaration and exact same closed type
tuple reaches a key already reserved in the worklist. The recursive FIR call points to that ID.
Mutual recursion works the same way: every declaration/type tuple reserves an ID, and an exact
backedge resolves to its existing node.

The worklist must be drained before `FirProgram` is returned. A pending ID with no built function is
an internal inconsistency and must not be silently converted into an empty function.

### Type-growing recursion

A recursive call with a different type tuple is not an exact cycle. If a future type constructor
allows a function to call `f<[]T>` from `f<T>`, the instance sequence can grow without repeating a
key. An active declaration-name set would incorrectly classify this as a finite cycle.

The current native ABI rejects nested arrays, which closes that particular path. The builder also
has finite limits for specialization count and argument type depth. Those limits are safety bounds;
they are not a proof that arbitrary type-growing recursion is supported. A limit error identifies
the source call and prevents a partial program from becoming verified FIR.

## Source origins and call identity

The specialized FIR function retains the generic declaration's original `AstOrigin`. Specialized
expressions retain the original expression origins. The generated ID and argument tuple are
additional compiler metadata; they do not create a synthetic source file or a second user
_declaration_.

A call node retains its own source origin, target ID, type-argument tuple, source argument order,
formal parameter mapping, passing modes, and expression types. If ten calls share one instance, FIR
still has ten call nodes. Flow graph call sites keep their own source origins and target references.

When verification rejects an instance, diagnostics should point back to the source declaration,
body node, or call edge that caused the failure. Specialization context can appear in the diagnostic
message, but it must not replace the original source span. A cache hit or deduplicated body must not
suppress an error at a second call site whose candidate or arguments differ.

## Type-bearing FIR positions

A closed function instance needs consistent substitution throughout its whole body, not only in the
function header. The current builder substitutes these positions before verifier admission:

| Source position | FIR representation | Required agreement |
| --- | --- | --- |
| Formal parameter | `FirSlot` in the function parameter prefix | Same name, order, passing mode, concrete type. |
| Local parameter storage | `FirSlot` in the complete slot table | Reads/writes use the identical slot ID and type. |
| Explicit local type | `FirDeclare` plus slot | Declaration type and initializer agree after substitution. |
| Inferred local type | Source binding and slot | The bound local type is closed before slot allocation. |
| Expression result | `FirExpression.Type` | Equals the substituted source expression type. |
| Function result | `FirFunction.ReturnType` | Equals the specialized source body summary. |
| Declared effects | `FirFunction.Effects` | Preserve the selected source declaration's closed effect identities. |
| Nested call | `FirCall.TypeArguments` and `Target` | Matches the caller-substituted binding and target instance key. |
| Array iteration | Array type and item slot | Item slot equals the concrete element type. |

A partially specialized body is invalid. It is not sufficient to substitute the outer return type
while leaving a local slot, call target, array element, or effect member open.

## Constraint boundary

Generic source binding checks the selected candidate's declared constraints. Before queuing an
instance, the FIR builder also requires all selected type arguments to be supported closed native
types and asks the workspace type-specialization service to check declaration constraints. The FIR
verifier repeats the constraint check from the source declaration and the function's stored
specialization argument tuple.

A caller's bound is not automatically a callee's bound. For example, if an outer function declares
`T: Readable`, an inner generic call still needs its own source-selected constraint proof after the
caller substitution. A concrete root call may close `T` to a type that satisfies the inner constraint,
but a generic body must not assume arbitrary values of T satisfy an unrelated bound.

Constraint validation is not a substitute for body rebinding. Generic operator resolution,
conversion availability, member dispatch, capture rules and borrow behavior can depend on the
concrete substitution. The current native tranche requires a source-bound body and rejects any
resulting FIR shape that it cannot independently validate.

## Native ABI boundary

Only types that the current `FirVerifier.Supported` predicate and LLVM emitter can represent are
admitted as generic arguments. This covers supported primitive scalars, `std.core.String`, and
one-level dynamic arrays whose elements are supported scalar or String values. `half`, `quad`,
unsupported nominal types, nullable native values, ref-like values, nested arrays and unsupported
function/tuple layouts remain outside this slice.

Generic free functions with a body may be emitted as internal monomorphic functions when selected
by a direct call. This does not create a generic C export, reified runtime descriptor, method
specialization, generic class layout, or cross-container generic link contract. A public generic
function is not an unmanaged symbol merely because one internal instance exists.

All instances lower through the same constructed LLVM module and C++ API bridge as ordinary verified
FIR. The backend receives concrete signatures. It does not parse a textual LLVM representation to
recover generic arguments.

## Failure and rejection behavior

The builder rejects a generic call when any of these conditions holds:

- the selected target is not a source-owned function in the admitted direct-call form;
- the target is a generic member or depends on an unsupported generic owner;
- type-argument arity does not match the target declaration;
- caller substitution leaves a type parameter open;
- a type argument has no qualified native ABI representation;
- the selected source binding does not provide a completed unique result;
- generic constraints are unsatisfied or indeterminate;
- the specialization count or type-depth budget is exceeded;
- a body expression, local, passing mode, effect, or control-flow shape is outside verified FIR.

`SOL2400` continues to identify unsupported shapes in the verified native subset. `SOL2403` is the
bounded specialization resource diagnostic in the current implementation. `SOL2402` remains the
independent FIR verifier failure code. The exact diagnostic catalog and messages must stay aligned
with source origins and the public diagnostic policy.

A rejection is not a successful partially specialized function. It does not fall back to a generic
function pointer, choose another overload, create a fake `Object` type argument, or silently omit the
call. A failed instance prevents the dependent `FirCompilation` from returning a
`VerifiedFirProgram`.

## Inspection format

FIR JSON schema 5 carries ordered `specializationArguments` on function records and ordered
`typeArguments` on direct call nodes. Values refer to the same compilation-local resolved type table
as function signatures and expression nodes.

The verifier checks the call's argument list against the source `BodyCallBinding` after applying the
current function instance's substitution. It also checks the target function's ordered
`SpecializationArguments`. This permits tools to inspect how a nested source call's outer T became a
concrete inner `String` or primitive argument without serializing unstable type-parameter object
references.

Schema IDs are not stable across compilations. A consumer that persists FIR JSON must check the
schema number and must not assume that a type ID or function ID can be joined with a different
compilation's output.

## Boundaries that remain open

The current implementation does not close the complete Sol generic model. The following remain
separate work:

- rebinding each body under its concrete type arguments, including generic overload/operator/member
  resolution that can change after substitution;
- generic class, struct, ref struct, interface, object, property, constructor and operator owners;
- arbitrary nested nominal types, nullable values, nested arrays, tuples and function-type layouts;
- open generic function values, closure conversion, virtual dispatch and runtime type tokens;
- full multi-open checked-effect inference and exception unwinding in the native runtime;
- persistent specialization caches, stable canonical IDs and cross-version cache invalidation;
- generic C ABI exports, generic `.solib` exports and specialization across artifact boundaries;
- the complete target/layout matrix beyond the currently supported Windows x86-64 bootstrap subset.

A passing source binding does not close any of these boundaries. Each extension must define its source
contract, typed FIR metadata, independent verifier rule, LLVM/runtime behavior, serialization impact
and exact positive/negative fixture status before being described as implemented.

## Maintainer review checklist

When changing nested specialization, inspect all layers together:

1. Does `BodyCallBinding` retain the target-owned arguments in declaration order?
2. Are generic parameter identities compared by owner and ordinal rather than spelling?
3. Is the outer caller map applied before the callee key is created?
4. Does the callee body use its own map without mutating the caller map?
5. Are expected-result, explicit and inferred arguments all represented identically after selection?
6. Are all local, expression, signature and effect types closed before verification?
7. Do repeated exact keys reuse one body without collapsing call-site origins or evaluation order?
8. Does exact recursion point to a reserved ID, while distinct growing keys consume a finite budget?
9. Does the independent verifier compare source binding, call metadata and target instance metadata?
10. Are constraints checked for every concrete instance rather than inherited by name?
11. Does unsupported ABI shape fail before the LLVM module is emitted?
12. Is schema 5 updated when a serialized field or semantic invariant changes?
13. Are generic functions kept out of unmanaged exports unless a separate ABI contract authorizes them?
14. Do documentation and status notes say exactly which generic body semantics are still unimplemented?
15. Is every new source and documentation file below the repository's 750-line preferred limit?

The more complete normative scenarios are in
[`GenericFirNestedSubstitution.sol`](../../spec/examples/semantics/GenericFirNestedSubstitution.sol).
They are design inputs until an executable semantic/native regression harness explicitly asserts them.

## Walkthrough: one closed root call

Use this source pair:

```sol
module docs.generic_fir_specialization
func <T> identity(value: T): T = value
func <Outer> forward(value: Outer): Outer = identity(value)
func run(value: String): String = forward(value)
```

The source analyzer first processes the call to `forward` in `run`. It selects the declaration,
validates the argument mapping, infers `Outer = String`, checks the declared constraints, and records
a `BodyCallBinding` with one type argument.

The FIR builder reserves an ordinary function ID for `run` and lowers its call to `forward`.
The selected binding supplies the `forward` declaration and `[String]` tuple. The builder verifies that
the target is a module-level generic function, that the tuple is closed, and that the type fits the
native subset. It reserves the `forward<String>` ID and adds a queue item.

When the queue item is processed, the builder creates the caller substitution:

```text
Outer@forward/0 -> String
```

The source-analysis call binding for `identity(value)` retains its own target-owned type argument:

```text
T@identity/0 -> Outer@forward/0
```

The builder substitutes the caller map into that argument and obtains:

```text
T@identity/0 -> String
```

It reserves or reuses `identity<String>`, then emits the nested `FirCall` with the concrete
`String` type argument and the corresponding target ID. The generated `forward<String>` result and
parameter slot are both `String`.

The verifier later checks three linked facts independently:

1. `forward<String>` has exactly one source type argument and it is closed.
2. Its parameter and result match source analysis after `Outer -> String`.
3. Its `identity` call's source binding, call metadata, and target specialization all agree on
   `String` after the caller map is applied.

The root call and nested call keep separate origins. The two generic declarations keep separate
source identities. Only `identity<String>` is shared if another selected call reaches the same key.

## Call argument evidence and instance identity

The generic inference solver can use several categories of evidence. The source binder decides which
evidence applies to the selected candidate; FIR consumes only the completed selected result.

| Evidence source | Candidate binding role | Effect on inner key |
| --- | --- | --- |
| Explicit `callee<int>` argument | Fixes one target parameter by ordinal | Contributes the exact selected `int` identity. |
| Value argument `callee(value)` | Input relation through mapped formal | Contributes only after a unique candidate solution. |
| `ref` or `out` value | Exact storage identity | Cannot use numeric widening or boxing to form a key. |
| Expected result | Expected relation to declared result | May determine a parameter absent from value inputs. |
| Contextual lambda | Candidate-local fixed point where supported | May remain deferred; FIR does not invent missing type arguments. |
| Default argument | Source mapping only | Does not count as a runtime value witness unless the source contract defines it. |
| Vararg expansion | Source mapping only | Native body admission can reject the call even if source analysis mapped it. |

The type-argument vector must be retained even when all parameters are not visible in the concrete
function ABI. For example, an unused parameter can still appear in a bound or in a declared effect
shape. Reconstructing generic arguments only from the final parameter and result types is lossy.

The source call's syntax spelling is also not an identity field. These calls can have the same
instance key if they select the same declaration and type tuple:

```text
identity(value)
identity<String>(value)
```

That equality holds only after both source calls resolve uniquely and all constraints/effects have
been checked. It does not merge their argument expressions or their source locations.

## Distinguishing common nested-call cases

| Caller situation | Inner argument before composition | After composition | FIR result |
| --- | --- | --- | --- |
| Concrete nongeneric caller passes `int` | `int` | `int` | Reserve/reuse `callee<int>`. |
| Generic caller parameter `T`, root call closes T to String | caller-owned `T` | `String` | Reserve/reuse `callee<String>`. |
| Generic caller forwards `U` to callee's second parameter | caller-owned `U` in target ordinal 1 | concrete root type at ordinal 1 | Preserve the callee's declared argument order. |
| Caller body names a different generic `T` | caller-owned identity | concrete root type | Do not compare the spelling to callee's T. |
| Caller map is incomplete | open caller-owned parameter | still open | Reject/defer; create no verified instance. |
| Candidate has two valid targets | candidate-specific argument tuples | not uniquely selected | No target key is authorized yet. |
| Target bound fails after composition | closed type not satisfying target | same closed type | Reject with source call origin; no fallback specialization. |
| Two wrappers reach one exact leaf | same declaration and tuple | same closed type | Reuse leaf function ID; preserve both edges. |
| Same declaration receives different tuples | same declaration | different closed types | Reserve distinct instances. |

This table is a semantic guide, not a replacement for overload resolution. The selected source target
is a required input to key construction.

## Worklist state transitions

The queue owns immutable request records. A request is conceptually:

```text
(declaration identity, ordered closed type arguments, reserved FIR function ID)
```

The state of a request changes only through these transitions:

| Current state | Event | Next state |
| --- | --- | --- |
| unseen | selected closed call requests key | reserved and queued |
| reserved | same exact key requested again | existing ID returned; queue unchanged |
| queued | builder starts concrete projection | building |
| building | body completes and passes source-shape admission | built |
| building | nested call requests unseen key | new request reserved and appended |
| building | unsupported source/body shape found | compilation diagnostics; no verified program |
| queued/building | cancellation observed | operation stops with cancellation |
| any state | graph/type budget exceeded | resource diagnostic at source origin |

Only a complete queue drain can produce the function array supplied to verification. The output
array is ordered by reserved integer IDs, so every `FirCall.Target` indexes one function. The
verifier checks ID/index agreement before constructing the capability consumed by LLVM lowering.

A queue item is not a cache entry that may outlive the compiler call. There is no disk serialization
of request state, no resume-from-cache mode, and no cross-process lookup. Any such feature needs an
independent design for source digests, target ABI, language edition, compiler identity and corruption
handling.

## Failure transaction

A specialization request is made only after source binding has selected one callable. The native
builder then admits or rejects that selected target; it does not rerun overload resolution with a
new native-only preference.

On failure, the compiler must keep the following facts distinguishable:

- source syntax errors, which prevent valid AST/type analysis;
- a source candidate that is proven inapplicable;
- an ambiguous call with more than one equally best candidate;
- a deferred source candidate whose evidence is incomplete;
- a selected source binding outside the native FIR subset;
- an internal verifier inconsistency after FIR construction;
- a bounded specialization resource failure.

These categories lead to different diagnostics and must not be collapsed into a generic successful
fallback. In particular, a missing inner type argument cannot be replaced with `Object`, and a failed
specialization cannot be silently erased from the function graph while its caller remains verified.

A partial queue may exist briefly while the builder is running. If any one required body fails, the
`FirCompilation` result must not return a `VerifiedFirProgram`. Consumers cannot see the partial
worklist through public APIs.

## Why a second source binding pass remains important

The current implementation substitutes source-bound facts, then independently checks FIR. A fuller
compiler should rebind or revalidate each generic body under its closed substitution before FIR
construction.

The concrete type can affect more than signature spelling. It can affect:

- operator overload selection and whether an operation is intrinsic or a user call;
- available members, hidden members and constructed base projections;
- implicit conversion ranking and ambiguity;
- checked effects inferred through nested calls or callbacks;
- nullability, flow refinement and smart-cast validity;
- ref/out exact-storage rules and definite assignment;
- capture and escape analysis for ref-like values;
- the native layout and GC root shape of locals;
- whether a call is direct, virtual, intrinsic, or unsupported.

A source body proven valid for a generic parameter under its declared constraints does not
automatically prove that every code-generation rule is valid for every concrete type that satisfies
them. The current admission boundary is intentionally narrower than the full language contract.

## Relation to artifacts

`bin` AOT and ORC share the same verified FIR semantics. A generic instance admitted by this slice
is a concrete function in the module before either backend mode consumes it. The backend mode does
not affect source inference or specialization identity.

`cdylib`, `staticlib`, and `solib` have additional export and container rules. Internal generic
instances may be needed by a function body in the artifact, but that does not make a generic source
function an exported native symbol. Each artifact adapter must verify the generated symbol and
calling convention separately.

The current implementation does not define generic instance exports across `.solib` boundaries. A
consumer cannot ask an archive to instantiate arbitrary source-level generic declarations from type
arguments alone. Cross-module generic use requires source re-analysis or an explicitly versioned
pre-specialized interface contract.

## Conformance organization

The source example companion separates related scenarios so review can find the intended oracle:

| Section | Main question |
| --- | --- |
| Generic declaration ownership | Are same-spelled parameters distinct by declaration? |
| Nested forwarding | Does the outer substitution close the inner argument? |
| Explicit and inferred mappings | Is each target tuple in declaration order? |
| Expected-result evidence | Does context flow only along the selected call chain? |
| Overload selection | Is the key created only for the selected candidate? |
| Local and control-flow positions | Are every slot and expression type concrete? |
| Exact recursion | Does a reserved key stop unbounded host recursion? |
| Growing recursion | Does a different key consume a finite budget? |
| Origins and sharing | Are shared bodies separate from call-site provenance? |
| Named/ref/out mapping | Are source mappings preserved through substitution? |
| Unsupported owner/ABI | Does the native gate reject without falling back? |

The annotations are expected outcomes, not proof of passing semantic tests. A future executable
harness should bind each case ID to a source call node, expected diagnostic or selected declaration,
closed argument tuple, target function and stable result shape. It should separately assert that
rejected cases produce no verified artifact.

## Review questions for nested changes

Before changing this path, answer each question with evidence from the Analysis, FIR and lowering
code:

1. Where does the source binder store the target-owned argument tuple?
2. Can that tuple contain a parameter owned by the current generic caller?
3. Which exact substitution is active when the nested call expression is converted?
4. Is substitution simultaneous and owned by the same analysis workspace?
5. Does the target key include the selected declaration, not only its name?
6. Does target parameter order survive named or reordered argument maps?
7. Does the function body use the target's map while its caller retains its own map?
8. Are return and effect types substituted in the same workspace as the signature?
9. Can an exact recursive call resolve to a reserved but not-yet-built ID?
10. Can a type-growing call escape the count/depth budget?
11. Do separate call sites retain distinct source spans when they reuse a target body?
12. Does verifier validation recompute source-to-FIR identity after caller substitution?
13. Does the native gate reject unsupported types before LLVM module construction?
14. Does the JSON schema expose ordered type arguments without implying cross-build stability?
15. Have public status notes been updated to distinguish current projection from full rebind?

If an answer is unknown, the extension is not ready to be described as supported. The open contracts
listed above remain visible until the source binder, FIR verifier and LLVM/runtime boundary all agree.

## Unused parameters and key completeness

An outer parameter that does not appear in the body still belongs to the outer instance tuple. It may
participate in bounds or source type identity even when there is no `FirRead` for it. The target's
inner tuple, however, contains only the parameters declared by the selected inner function.

An implementation must not infer a shorter specialization tuple by scanning emitted body reads.
Generic arity is a declaration property. A concrete instance key always contains every declared
type argument, including arguments that erase from the native calling convention.

Conversely, an outer argument that is irrelevant to an inner target must not be copied into the inner
tuple. For `forward<A, B>(a, b) = identity(b)`, the inner identity key has one argument for `B`; it
does not acquire `A` merely because the outer key contains both.

## Source type versus backend type

Two distinct Sol type identities may map to the same LLVM primitive representation. They remain
distinct in source inference and in a generic specialization key unless the language's canonical
type interner says they are the same identity. Backend erasure occurs after source selection and
verification; it cannot be used to merge overload candidates or constraints.

Likewise, equal display strings are not a substitute for owner identity. A serialized inspection
consumer should use the compilation's type table to understand the call tuple and should discard
those numeric IDs when that compilation ends.
