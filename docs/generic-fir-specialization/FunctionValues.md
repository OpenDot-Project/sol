<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Generic functions and function values

This guide defines the boundary between a direct generic call, a function value, a lambda, and a
closure. These forms can share the same visible parameter and result types, but they have different
selection, lifetime, execution, and ABI requirements.

The current implementation has bounded native FIR paths for selected module-level generic free
function calls, monomorphic values of generic module-level functions or methods on eligible stateless
top-level `object` declarations, and noncapturing lambdas whose contextual function type exactly
matches the native function-pointer ABI. A lambda body may call or reference a generic function when
source analysis selected a complete supported closed target. An admitted lambda becomes a synthetic
source-linked FIR function and uses the same typed code-pointer representation; it does not allocate
a closure environment. Capturing lambdas, receiver-bound member access, lambdas in generic enclosing
function bodies, open/unsupported generic targets, and signatures that need an ABI adapter remain
outside native FIR. Source body analysis can understand additional function-value and lambda shapes,
but source analysis alone does not authorize native emission.

The normative examples are in [GenericFirFunctionValues.sol](../../spec/GenericFirFunctionValues.sol)
and [the semantic case matrix](../../spec/examples/semantics/GenericFirFunctionValues.sol). This page
describes implementation requirements and how to interpret the cases; it does not claim that all
listed scenarios have been executed.

## Four related language objects

| Object | What is selected or represented | Current native status |
|---|---|---|
| Generic function declaration | One source declaration with owner-scoped type parameters | Source declaration and constraints are available to analysis. |
| Direct generic call | One selected declaration and one ordered, closed type-argument list | Bounded monomorphic FIR specialization is implemented. |
| Generic function value | A generic declaration closed to one concrete function type at the value site | Bounded monomorphic FIR specialization is implemented for eligible free functions and stateless singleton methods. |
| Function value with an environment | A callable value with a receiver or captured lexical storage | No verified FIR node or native ABI. |
| Lambda | A source expression with contextual parameter/result types, effects, flow and captures | Noncapturing, exact-ABI lambdas have a bounded synthetic-function FIR path; captured or adapted values are rejected. |
| Anonymous function | A callable source expression with its own parameter list, effects, flow and possible captures | Partial source analysis; native emission is not admitted. |

The distinctions are observable. A direct call executes a selected function now. A function value
can be stored, returned, passed to another function, or invoked after the expression that created
it. A closure can retain locals beyond their lexical stack frame. Sol closes a generic function
value at its source site; the stored value is monomorphic and carries no open generic parameters.
That rule does not define closure environments or polymorphic runtime dispatch.

## Status vocabulary

The sources use these outcome labels to keep phase ownership visible:

| Label | Meaning |
|---|---|
| `Bound` | Source analysis produced the described declaration, type, mapping, or result facts. |
| `Rejected` | The named source rule or selected candidate is invalid. |
| `Ambiguous` | More than one equally ranked source candidate remains. |
| `Deferred` | The source/API path does not yet have enough implemented rules to decide. |
| `FIR: Ready` | The constructed FIR is eligible for independent verification under the current subset. |
| `FIR: Rejected` | The source may be valid, but the current native FIR subset cannot represent it. |
| `Verifier failure` | Constructed FIR violates an independent source-linked invariant. |
| `Runtime qualified` | An artifact mode was executed with evidence for the documented behavior. |

A `Bound` call is not a function-value conversion. A `FIR: Rejected` outcome must not change which
source overload won. A diagnostic from the independent verifier must not be disguised as a source
error. A fixture comment states an expectation; it is not itself test evidence.

## Direct generic call path

For `identity<String>(value)`, source analysis resolves `identity`, checks its arguments and
constraints, substitutes its type parameters, and records the selected closed type arguments. FIR
uses the source declaration identity and declaration-ordered type IDs to name one compilation-local
instance. Calls to the same key point to the same FIR function. Recursive calls with an identical
key reuse that instance.

The verifier checks the specialized signature, effects, expression types, selected source binding,
call arguments, constraints, and reachability from an ordinary source function. Only verified FIR
can proceed into native lowering. The current backend builds LLVM structures through the LLVM
23.1.3 C++ API via the C++/CLI bridge; generic function values do not introduce an LLVM-text path.

This path produces a normal direct call edge. It does not create a function pointer, descriptor,
closure environment, trampoline, virtual dispatch entry, or runtime generic dictionary. The
Each closed instance gets a fresh `BodyAnalysis` pass under its type substitution. That pass rechecks
the body bindings, overload/operator choices, effects, and flow facts represented by the current
analysis surface. This is not complete support for every language construct: unsupported specialized
operations still stop at the verified FIR boundary.

## Closing a generic function value from context

The expression `identity` in a value context is not equivalent to `identity(0)`. The latter is a
call that evaluates an argument and returns a value. The former selects a declaration and requests a
callable code reference. It does not execute the target, evaluate defaults, or create a closure.

No reference suffix is needed. A generic function name in a value context uses the expected function
type to infer every generic type parameter. For example, `val callback: (String) -> String =
identity` selects the closed `identity<String>` instance. An explicit generic argument list belongs
to call syntax; the compiler does not invent a second reference syntax.

Inference follows the direction of function-value conversion: the selected source function must
convert to the expected target type. Value parameters reverse the relation, results preserve it,
ref/out parameters require exact storage identity, and source checked effects must fit the target
effect budget. Every generic parameter must have one solution and satisfy its declared constraints.
The resulting value is monomorphic even when stored or returned.

If a parameter cannot be inferred uniquely, candidate analysis stays deferred. If the closed
signature or constraints are incompatible, that candidate is inapplicable. Candidate bodies,
defaults, and code-generation support do not contribute inference evidence. No dummy call executes
the target to discover its type.

## Candidate selection in a value context

An overloaded function name in a value position is a candidate group. `BodyAnalysis` selects a
candidate when an expected function type exists and every candidate that could affect the result is
either decidable or explicitly deferred. Eligible candidates are module-level functions or methods
declared on a stateless top-level `object`, with explicit parameter types and a result type that is
either declared or inferred by ordinary body analysis. Such object methods have no hidden receiver in FIR: the object has no instance state,
constructors, bases, initialization, or non-function members, and the method group cannot use
`this`, `super`, or virtual dispatch. Generic candidates are closed
from the expected function type and their constraints are checked before ranking. Candidate
compatibility uses the resolved function conversion rules, including variance, ref/out identity,
and checked-effect coverage. Candidate bodies and defaults do not contribute evidence. The selected
declaration, function type, and ordered generic arguments are recorded on `ExpressionBinding`.

Candidates rank by identity, intrinsic reference conversion, then boxing. If conversion ranks tie,
an otherwise equivalent non-generic declaration is preferred; it does not beat a generic candidate
with a strictly better conversion. Remaining equal best candidates produce `SOL2212`; no applicable
candidate produces `SOL2206`. Declaration order does not break ties. An overloaded name without an
expected function type remains deferred. A candidate whose signature, inference result, or
constraints remain indeterminate keeps the group deferred because it could change the winner.
Receiver-bound or signature-incomplete candidates also remain deferred. Declarations imported from
multiple modules remain ambiguous. A single non-generic function reference retains the existing
source behavior without overload ranking.

Candidate analysis is non-executing. It does not run a getter, default expression, lambda body,
constructor, or generic body to learn whether a candidate fits. An expression-body result already
inferred by ordinary body analysis is part of the function's resolved signature; inspecting that
type does not execute the body or use its operations as generic argument evidence. Candidate-local
inference facts are isolated and are published only after one candidate wins. Deferred candidates
cannot be silently discarded when they could change the result.

| Candidate result | Required behavior |
|---|---|
| One unique applicable candidate | Record source binding and closed generic arguments; FIR admits an eligible free-function or stateless-object target with an exact native signature. |
| No applicable candidate | Report `SOL2206` at the value expression. |
| Equal best candidates | Report `SOL2212`; do not record a winner. |
| A candidate remains semantically deferred | Preserve Deferred if the candidate could alter the result. |
| A generic candidate with missing or indeterminate evidence | Preserve Deferred; do not discard a possible winner. |
| A receiver-bound group | Preserve Deferred; no bound receiver or closure is fabricated. |
| Declarations come from multiple modules | The source lookup reports `SOL2002`; imported modules do not form one overload set. |
| A selected function value has no verified native ABI | Reject with `SOL2400`; adapters, closures, open generic targets, and receiver captures are not fabricated. |

This ranking is a bounded source-analysis rule for free functions and eligible stateless-object
methods. A backend must not
choose a candidate based on code-generation convenience, symbol order, declaration order, or whether
its body happens to be lowerable.

## Verified native function values

Verified FIR represents a supported named function value as `FirFunctionReference(target, type)`.
The target may be a generic module-level function or eligible stateless-object method only after it
has a source body, one ordered closed
specialization argument list, a closed supported signature, and an exact match between its parameter
modes, parameter types, result type, and checked effects. The verifier checks that target
specialization arguments match source inference. A reference from an ordinary function roots that
instance in the FIR specialization graph; a reference from another generic instance adds a graph
edge. Overload selection is completed in source analysis, and FIR does not choose a different
overload to suit code generation.

`FirIndirectCall` carries the function expression, declaration-ordered arguments, result type, and
source origin. Its verifier checks arity, parameter indexes, exact value/ref/out types and modes,
the return type, and the function type's checked effects. Out arguments produce CFG writes only after
the call; ref arguments are read before it. The flow graph records a null direct target with the
function type's declared effect set. The LLVM 23.1.3 C++ bridge creates a `FunctionType` and emits
`CreateCall` with the function pointer. Source expressions and calls are matched back to their
resolved AST bindings before verified FIR is returned.

The native ABI admits monomorphic function values as locals, parameters, return values,
direct-call arguments, and indirect-call targets, including references to closed generic instances
and eligible noncapturing lambdas. It also admits non-null arrays of those values. A function array stores the raw code pointer at
pointer width; the Yellow Tea element descriptor has no reference scan shape. `[]((int) -> int)`
can therefore hold a direct reference, an eligible closed generic reference, or an admitted
noncapturing lambda, and `callbacks[i](x)` loads the exact signature before an indirect call. Nested arrays use a normal traced array reference at
each outer level and retain the scalar/no-scan function-pointer descriptor at the leaf. Empty arrays
require the function element type.
The array object itself remains a managed root during allocation and initialization, but code
addresses are never passed to the collector as `Header*` references.

Receiver-bound method values, variance adapters, nullable function values, capturing lambda
closures, and anonymous-function values remain outside the subset. A source-valid function reference conversion may still
be rejected by FIR when its signature is not exact; no ABI cast is used as a substitute for an
adapter. A capturing lambda cannot be stored merely because its source type matches a function
array. Closures need their own environment layout, escape and thread-transfer rules, and GC tracing
contract; the static function-pointer path does not claim those properties. See the executable
array cases in [FunctionValueArrayStorage.sol](../../spec/examples/semantics/FunctionValueArrayStorage.sol).

## Function types and identity

Function types are resolved structures. Their identity includes the resolved parameter types,
parameter passing modes, result type, nullability, and declared checked-effect set. Type-parameter
references include declaration owner and ordinal; a display name such as `T` is not sufficient.

Function effects use set semantics. Reordering an effect list does not create a different semantic
budget. A conversion cannot erase a checked effect merely because an LLVM calling convention does
not encode exceptions. A callback that can throw `IOException` cannot be assigned to a context that
promises no checked effect unless a source rule handles that effect.

Function compatibility is directional. Parameter modes and arity must match. Value parameters are
contravariant, results are covariant, ref/out storage types must be identical, and the source
function's checked effects must fit inside the target effect budget. A target may allow extra effects
that the source never raises. Nested function values apply the same rules recursively, so every
value-parameter edge reverses direction. Generic nominal applications remain invariant.

The generic inference solver collects bounds with the same direction: value-parameter occurrences
reverse input/expected relations, result occurrences preserve them, and ref/out occurrences require
exact identity. The fully specialized function type is checked for conversion after a unique type
substitution has been selected. A machine-code pointer cast, equal word size, or wrapper omission
does not establish source compatibility.

For a function type containing ref or out, both the mode and exact storage type matter. Ref/out
arguments cannot be widened, boxed, copied to a temporary, or represented by a read-only closure
capture. Out assignment is required on every normal return from the invoked callback.

## Lambda contextual typing

Named headers such as `{ value -> value }`, positional forms such as `{ $0 + 1 }`, typed headers,
anonymous functions, and trailing lambdas are syntactic forms that should converge on a resolved
function type after source analysis. There is no implicit `it`. Positional references require an
expected function type and their highest index must fit the function arity.

The expected type can supply parameter identities, modes, and a result context where the source
rules permit. A written lambda header must agree with that context. Nested lambdas introduce nested
parameter scopes; the inner `$0` refers to the inner lambda. Same-spelled lambda parameters and
generic parameters remain different identities.

For generic call inference, a candidate may use a lambda only after ordinary argument mapping has
established the relevant parameter position. Each overload candidate receives its own temporary
analysis state. New facts from the lambda are added in bounded rounds; failed or losing candidates
must restore temporary expressions, locals, flow facts, diagnostics, and effects. The selected
lambda is then analyzed under the final selected type substitution.

This candidate-local source analysis does not by itself admit a lambda to native FIR. The current
native slice can lift a noncapturing lambda when its contextual signature is closed, supported, and
identical to the emitted function type, and when the enclosing function is non-generic. A closed
generic call can carry that function reference when its callback parameter has the exact resolved
signature. A lambda written inside a generic enclosing body is still rejected by this slice, even if
one call site could specialize that body.

## Source capture and escape facts

`LexicalAnalysis.LambdaCaptures` and `GetLambdaCaptures(lambda)` inventory each captured local by
resolved symbol identity. Entries preserve source-ordered references, read/write access, passing
mode, and a candidate storage category. Nested lambdas list an outer local for each environment that
would need to retain it. This inventory does not establish a lifetime or environment layout.

`LexicalAnalysis.LambdaEscapes` and `GetLambdaEscape(lambda)` add a separate use classification:

| Disposition | Current evidence |
|---|---|
| `ProvenNonEscaping` | The lambda is the direct target of the call that creates it, or immutable local initializer aliases lead only to local aliasing and local invocation. Grouping and label wrappers are transparent. |
| `MayEscape` | The source returns it, passes it as a call argument, stores it in a property/heap location, or embeds it in an aggregate, including through a tracked local alias. |
| `Unknown` | A mutable alias is reassigned, an alias is captured by another callable, or the lambda appears in a consumer whose lifetime behavior is not modeled. |

Every result includes ordered source witnesses and the capture inventory for that lambda. The bounded
alias graph follows immutable local initializers by resolved `LocalSymbol` identity. A local alias
passed as a call argument or returned from the callable is an escape-capable sink. Mutable aliases,
alias writes, aliases captured by another callable, flow-sensitive branch joins, anonymous-function
environments, and callback retention contracts do not receive a non-escape proof. Call arguments are
escape-capable because there is no verified no-escape contract on callable declarations. `MayEscape`
means the source operation permits an escape; it does not claim a runtime callee actually retained
the value. `Unknown` is never a non-escape proof.

The result is advisory source analysis only. It does not authorize `FirFunctionReference`, closure
conversion, stack environment allocation, ref/out capture, or a Yellow Tea root/layout decision.
Capturing lambda FIR remains rejected until the callable ABI represents both code and environment,
the verifier checks capture sources and lifetimes, and runtime descriptors/barriers keep every live
environment reachable.

### Bounded noncapturing-lambda FIR path

The current path admits named-header lambdas such as `{ value -> value + 1 }`, positional forms such
as `{ $0 + 1 }`, and headerless zero-parameter bodies such as `{ 42 }`. Each needs a closed contextual
function type from a typed local, a mapped callback argument (including a trailing lambda), a typed
array element, or another supported source context. A headerless body is valid only when the
contextual function type has no parameters. A positional body can use only value parameters; its
`$n` reads map to the corresponding synthetic parameter slots. Named parameter count and passing
modes must match the context. Named `ref` and `out` parameters preserve their exact storage ABI;
source analysis must prove every `out` parameter is assigned on each normal lambda exit. Any
explicitly written parameter type must match exactly.

The enclosing function must not be generic. A stateless top-level `object` method is eligible only
when it has no receiver-dependent behavior. Every local referenced by the lambda must belong to the
lambda's own lexical scope: outer `val`/`var`, `this`, `super`, implicit member lookup, and local
function captures are rejected. A generic call or function reference from the lambda body is
eligible only when source analysis selected a complete closed argument list and the resulting
specialization has a supported native signature. The independent verifier checks the selected
arguments against the emitted target and adds it to the specialization graph. Native AOT and ORC
tests cover both `identity(value)` and a returned reference to `identity` from inside a lambda.
Nested lambdas can be lifted independently only when each is itself noncapturing; an inner lambda
that reads an outer lambda parameter is a capture and is rejected.

The builder adds a synthetic `FirFunction` and a `FirFunctionReference` at the original lambda
source site. The FIR verifier checks that the source analysis belongs to the same snapshot, the
synthetic function has the exact contextual parameter/result/effect signature, parameter slots and
positional reads match source identities, and capture/generic-body obligations are absent. The
function pointer is a scalar non-null code address; it does not own managed locals or a Yellow Tea
environment. Existing function-pointer array storage remains valid for an admitted lambda because
it stores only that same code address.

Source-valid lambda forms remain FIR-rejected when their signature would require variance/effect
adaptation, their enclosing body is generic, they capture any local or receiver, or a selected member
or generic target lacks the required native proof. No alternate overload is selected to avoid the
rejection. This is not general closure conversion and does not qualify mutable-cell identity,
escape, stack borrowing, thread transfer, nullable callbacks, or closure GC roots.

## Generic lambdas versus contextually typed lambdas

A lambda whose parameter type is the enclosing generic `T` is not thereby polymorphic. It has one
contextual type in that enclosing declaration. The source analyzer may record an open `T` while
checking the generic body, but the lambda has not acquired a separate type-parameter owner.

A polymorphic lambda would need its own parameter declaration, constraints, owner identity,
instantiation behavior, recursion limits, and ABI. It also needs a rule that says whether each call
instantiates the body, the value creation fixes arguments once, or runtime descriptors are retained.
Those choices affect code size, overload applicability, and the specialization graph. The current
language contract does not choose among them.

Until that design exists, syntax or source analysis must not pretend that a lambda can be reused at
multiple incompatible function types by inferring a new generic instance for each use.

## Ref/out callbacks

Function types preserve explicit `ref` and `out` modes. A callback invocation must receive exact
writable locations. A `ref short` cannot satisfy `ref int` by numeric conversion. A temporary or
ordinary property is not a ref/out location. An immutable captured value cannot be passed as a
writable location merely because its closure environment has an address.

A callback that receives a borrowed location may not retain it after the permitted lifetime. If the
callback can escape, store the address, capture it in another closure, transfer it to another thread,
or return it, the source contract must prove that each path is safe. Unknown indirect calls are
conservative sinks until their effect and escape contract is available.

The body analyzer and verified indirect-call node preserve exact ref/out modes and caller storage.
The verifier rejects non-storage, immutable, copied, or mode-mismatched arguments. An indirect out
argument is written in CFG only after the call, so an exception edge cannot claim it was assigned
normally. Escape analysis remains conservative: callbacks cannot capture or retain borrowed locations.

## Checked callback effects

The checked-effect set is part of the function type. Creating a closure does not execute its body.
Invoking it does, so a direct or indirect call edge contributes its declared effects to the enclosing
function budget. A wrapper that returns a callback without invoking it has different effect flow from
a wrapper that invokes it immediately.

For a generic effect parameter, substitution preserves the owning parameter identity until the call
is selected. The concrete effect must be a `Throwable` subtype and must fit the caller's checked
budget after any catches or cleanup are applied. Effect sets are not matched positionally.

Function types preserve checked effects through source binding, FIR verification, and indirect-call
CFG metadata. The verifier checks that every effect is a `Throwable` subtype and that the source
invocation fits the caller's declared budget. Native exception dispatch and source catch/finally/defer
lowering remain separate backend requirements; carrying an effect set does not implement Windows
exception delivery.

## Defaults and varargs

Defaults and varargs belong to a selected declaration. A function type does not carry declaration
default-expression metadata. Calling through a function value therefore requires every function-type
parameter; the indirect call does not synthesize omitted defaults or evaluate a declaration's default
expression. A source function's vararg parameter is represented in its function type as one array
parameter. An indirect call supplies that array as one argument and does not expand elements.

This rule does not reuse the direct-call argument mapper. Defaults remain available only when the
source calls a declaration directly, and vararg expansion remains a direct-call mapping operation.
The current FIR subset admits positional arguments only. A vararg target is called by passing its
already-formed array parameter; it is not expanded at the indirect-call site. Named arguments and
trailing-lambda mapping are not encoded in a function type and are rejected by this FIR path.

## Capture and closure environments

An immutable local captured by a closure can be copied only if its representation and lifetime allow
the copy. A mutable `var` uses a shared cell when multiple closures or the creating frame must observe
the same storage. Copying each closure's own snapshot would change program behavior.

Lexical analysis now exposes `LambdaCaptures` and `GetLambdaCaptures(lambda)` as a source-level
inventory. Each entry identifies the resolved `LocalSymbol`, every reference in that lambda, and the
combined read/write access. A mutable local is classified as shared-cell storage; an immutable local
is classified as a value-copy candidate. If a nested lambda refers to a local outside more than one
lambda boundary, the inventory includes that same local identity for each environment that must
provide it. Shadowed locals remain separate because capture ownership follows the resolved symbol,
not its spelling. `ref` access is read/write and `out` access is write-only in this inventory.
Ref/out parameters are classified as borrowed locations, and captured local functions retain a
separate callable-environment classification instead of being mistaken for value copies or cells.
Implicit receiver and member captures are not `LocalSymbol` entries; they require a separate
receiver/environment analysis and are not represented by this lexical API.

This metadata is lexical evidence, not a closure conversion or an escape proof. It does not certify
that a captured location or ref-like value can escape, define allocation/root placement, or create a
runtime cell. Native FIR still rejects capturing lambdas until a verified environment layout and
Yellow Tea ownership path are implemented.

A closure environment may contain captured values, shared cells, a receiver, and nested closure
references. It must retain source identity and concrete type metadata needed by the runtime. The
environment is distinct from a function's code pointer; a bare pointer cannot recover its captured
state.

Escaping closure environments must own or trace captured references after the creating stack frame
returns. Non-escaping stack environments also require precise liveness while they cross safepoints.
The compiler cannot label a captured reference as an untracked raw pointer because one fixture only
uses the closure locally.

The current FIR has no closure-environment representation. It therefore cannot emit either a
heap-allocated escaping closure or a stack-only non-escaping closure through this generic slice.

## Yellow Tea and closure roots

Any future closure environment containing `String`, object, array, boxed value, or other managed
references must participate in Yellow Tea's root/heap tracing contract. The descriptor for the
environment must identify reference slots at their precise offsets; source type names or guessed
field layouts are insufficient.

The creation path must root values while the environment is allocated and initialized. Stores into
an already published environment must obey the collector's barrier and memory-order rules. Calls
must keep both the environment and any loaded managed values live across safepoints. When a closure
is stored in a field, returned, placed in a collection, or sent to an ORC guest, ownership and
tracing transfer must be explicit.

Mutable captured variables may need a separate heap cell. That cell has its own descriptor, identity,
and barrier behavior. Two closures that capture the same local must refer to the same cell unless the
source semantics explicitly prove that no write can be observed.

`allows ref struct` only permits a ref-like type argument under the documented generic rule. It does
not permit storing `Span<T>`, a borrow, or a byref in a heap closure environment. An escaping closure
must not extend a stack borrow.

## Indirect call execution

An indirect call evaluates the target once, evaluates positional arguments once in source order, and
invokes the concrete function type. The current verifier requires exact arity, parameter order,
passing mode, parameter type, and result type. It does not perform named/default/vararg mapping at
the function-value boundary. A vararg declaration's array parameter is one ordinary function-type
parameter.

The FIR flow graph records an indirect call with no direct target and the function type's declared
effect set. `ref` locations are read before the call; `out` locations become assigned only after the
normal call edge. This represents the declared function contract, not proof that an arbitrary
implementation actually writes each `out` location. Body verification and source binding must keep
the target signature anchored to the selected declaration.

Before execution, the compiler/runtime must validate or guarantee a non-null callable target. It
must preserve the actual target identity through control flow and optimization. A nullable function
container and a function returning a nullable value are distinct types.

If an indirect call can throw, the caller's catch and cleanup analysis must see the declared effect.
If the call is cancelled or fails, temporary roots and borrowed locations must still be released
correctly. AOT and ORC may lower the call differently, but cannot change these source semantics.

## Current FIR representation

The FIR model includes `FirFunctionReference(target, type)` and
`FirIndirectCall(function, arguments, result, origin)`, in addition to direct `FirCall` and ordinary
slots/statements. A direct generic `FirCall` records ordered closed `TypeArguments` and its exact
monomorphic function ID. A supported generic function reference points to the same closed instance;
the specialization graph records it as a root or edge so it cannot be omitted from code generation.

`FirVerifier` independently checks the reference against the source-selected declaration and exact
closed signature. It checks indirect-call arity, parameter order, passing modes, exact types, result,
effects, source correspondence, and writable caller storage for `ref`/`out`. The supported closure-
free ABI is a raw code pointer with an exact signature. It does not represent an environment,
receiver-bound method, nullable callable, or runtime generic dictionary. Capturing lambdas and
closure environments remain rejected before LLVM lowering.

`SOL2400` is the current native source-admission diagnostic for a shape outside this verified subset.
`SOL2402` identifies an independent FIR verifier failure. `SOL2403` identifies a generic
specialization work bound. A missing function-value implementation is not a resource exhaustion and
must not be reported as one.

## LLVM and artifact boundary

The LLVM backend consumes verified FIR and constructs LLVM objects through the LLVM 23.1.3 C++ API
bridge. The admitted raw function references lower to the target's exact code pointer, and indirect
calls use an LLVM `FunctionType` and `CreateCall`; no signature cast is used to emulate a source
conversion. Arrays of supported function values store pointer-width scalar elements whose Yellow Tea
descriptor has no managed-reference scan shape. Nested arrays retain normal tracing at each array
reference level. This representation is valid only for the non-null, receiver-free function-pointer
subset described above.

AOT and ORC need consistent callback semantics. ORC executes in a separate guest process; process
isolation does not transfer an untracked host pointer or a managed closure automatically. A future
guest callback protocol must specify which process owns the environment, how calls are serialized,
how effects and failure status cross the boundary, and how captured managed state is represented or
rejected.

The artifact kinds `bin`, `cdylib`, `staticlib`, and `solib` each need an explicit callable export
contract. A C ABI callback cannot expose a managed closure as a naked code address. A Sol library
container cannot promise generic callable export until its type identity, code version, environment,
dependencies, and runtime ownership are stable.

## Diagnostics and source origins

The diagnostic for a generic direct call names the source call and selected declaration, then
reports the closed instance context. A value conversion failure points at the function name/value
expression and notes candidate declarations and expected function type. An indirect-call failure
points at the invocation expression. A capture escape points at the capture and the escape sink when
both are known.

Diagnostics inside a specialized body point to the original lambda or function body span. The
instance identity is attached as context rather than manufacturing a new source location. Generated
wrappers, trampolines, environment allocation, and closure prologues use synthetic origin kinds tied
to the source reference or lambda expression that caused them.

Debug information must map generated instructions back to original source expressions while
preserving instance identity. It must not expose private build-machine paths by default.

## Local functions

A local function declaration can refer to values in its lexical parent. When it does, its callable
identity includes the environment that supplies those values. The declaration's source syntax does
not make it a module-level function and does not make its body eligible for the current generic
specialization path.

A future implementation may lift a noncapturing local function to a private static code symbol,
but that is an optimization after source semantics are established. It must not change recursion,
debug origins, visibility, exception behavior, or whether the function value can escape. If the local
function captures a mutable binding, all invocations must reach the same shared cell.

Recursive local functions need explicit rules for environment initialization. A closure whose
environment contains a reference to itself or to a mutually recursive sibling cannot be published
before its required cells and roots are initialized. The initialization sequence must avoid both
reading uninitialized captures and exposing partially constructed environments to another thread.

## Flow facts across callbacks

Calling an unknown function value can mutate storage reachable through its arguments or captures.
Flow analysis must invalidate smart casts and definite-assignment facts according to the callback's
declared modes, escape contract, and effect summary. It cannot assume that a callback is pure because
its visible function type lacks a mutation annotation.

For `out`, assignment is guaranteed only after a normal callback return and only if the callable
contract promises assignment on every normal exit. If the callback throws first, the normal-return
fact does not apply. For `ref`, the location begins assigned but its previous value can change after
the invocation. These state transitions need both normal and exceptional CFG edges.

A generic callback parameter introduces an additional composition step: the outer generic
substitution closes the callback's function type, then the selected concrete callback contract
controls flow. This must be recomputed for each concrete generic instance when a type or mode changes
the set of legal operations. Reusing the open-body flow result is insufficient for those cases.

## Evaluation order and adapters

If a future value conversion requires an adapter, wrapper creation and wrapper invocation are
separate observable moments. Creating the wrapper must not evaluate source arguments or defaults.
Invocation evaluates the target expression once, then evaluates explicit arguments in source order,
then evaluates omitted defaults according to the selected declaration's order. Vararg expansion
must also preserve the language's exactly-once rule.

The wrapper must not duplicate an argument to satisfy both a generic direct-call ABI and a function
type ABI. If it performs a representation conversion, that conversion must be permitted by source
typing and its failure behavior must be specified. A hidden allocation can affect GC, exceptions,
and performance; it cannot be introduced as an invisible semantic assumption.

Tail expressions in lambdas and anonymous functions return their final expression under the normal
Sol block rules. `return@label` targets the resolved callable boundary. A wrapper or trampoline must
preserve the result and local return behavior without allowing a label to jump out of the wrong
closure.

## Thread and process transfer

A function type does not by itself say that a value can be called from another thread. A closure
that captures mutable state may require synchronization or may be forbidden from transfer. A
captured stack borrow cannot cross a thread boundary. The runtime must keep the closure environment
and managed captures alive until the final invocation or release.

The ORC guest is a separate process. A host function pointer, delegate object, closure environment
address, or GC object address cannot be sent to it as a raw integer. A future cross-process callable
protocol would need a versioned message frame, a target identity, a serialized environment or remote
handle, call lifetime rules, checked error/status mapping, and explicit resource cleanup when either
process exits.

If no such protocol exists, function values are rejected at the process boundary. Keeping the guest
isolated does not relax type safety, ownership, or garbage-collector requirements.

## Negative representation rules

The following shortcuts cannot substitute for a language and runtime contract:

| Shortcut | Why it is insufficient |
|---|---|
| Cast a function address to another signature | Source parameter, result, mode, nullability, and effect compatibility may differ. |
| Erase all generic arguments | Constraints and nested layouts may depend on the concrete arguments. |
| Use one global environment pointer | Independent closures and mutable captures have distinct identities. |
| Copy every captured variable | Mutable aliases must remain shared when the source observes writes. |
| Store byref in a heap object | The referenced storage may expire before the closure. |
| Mark closure fields as untracked pointers | Yellow Tea needs precise roots and barriers for managed references. |
| Treat callback creation as callback execution | It changes side effects, exceptions, and evaluation order. |
| Pick the first overload with a matching name | Overload ranking is semantic and declaration-order independent. |
| Export a closure as a C function pointer | The code address omits environment, ownership, and release behavior. |

Each of these shortcuts can yield a superficially linkable binary while violating Sol semantics or
runtime safety. The source-to-FIR verifier must reject unsupported forms before the LLVM backend
receives them.

## Required conformance matrix

Before broad function-value support is considered complete, qualification should include the
following dimensions:

| Dimension | Positive and negative cases |
|---|---|
| Generic target | Expected-type inference, constraint rejection, missing evidence, closed specialization reachability |
| Value conversion | Unique candidate, no candidate, ambiguous candidates, deferred candidate |
| Lambda typing | Named, positional, typed, anonymous, trailing, nested, expected-result and bottom forms |
| Effects | Matching checked budget, missing checked budget, unchecked exception, effect substitution |
| Passing modes | Exact ref/out, wrong storage type, immutable location, unassigned out result |
| Captures | Immutable copy, shared mutable cell, receiver, nested closure, escaping managed reference |
| Lifetime | Non-escaping local, returned closure, field/array storage, ORC transfer, cancellation |
| Ref-like values | Stack span, borrow, `allows ref struct`, nested capture, rejected escape |
| ABI | AOT call, ORC call, C export rejection, Sol container import/export boundary |
| Diagnostics | Candidate origins, source body origin, instantiation chain, verifier/resource separation |

Each result records source analysis, selected declaration, ordered generic arguments, FIR admission,
artifact mode, runtime result and collection/root evidence separately. A syntax parse cannot stand in
for source binding. A bound callback cannot stand in for a closure environment. LLVM verification
does not prove that Yellow Tea sees every captured reference.

## Completion gates

Broad function-value support is complete only after all relevant gates are implemented together:

1. The language defines monomorphic generic function references, overload-to-value ranking, function
   variance, effect compatibility, default/vararg restrictions, and polymorphic-lambda syntax.
2. Source analysis produces stable target, type-argument, effect, capture, escape, and call-flow facts
   with source origins and deterministic candidate outcomes.
3. FIR represents direct references, closures, indirect calls, and environment operations without
   losing declaration or instance identity.
4. The independent verifier checks each node against the selected source declaration and proves
   closed generic arguments, exact passing modes, effects, control flow, capture lifetime, and
   environment layout.
5. The runtime defines code/environment ownership, shared mutable cells, managed roots, barriers,
   exception propagation, cancellation, and any cross-process callback protocol.
6. The LLVM bridge lowers only verified nodes for AOT and ORC and returns precise source/debug origins.
7. Artifact contracts either define stable callable exports or explicitly reject them.
8. Positive and negative conformance cases run under the project's authorized regression, sanitizer,
   and fuzz workflows before the status page broadens its claim.

The bounded generic-reference and indirect-call path is implemented for module-level signatures
that exactly match their native target, including closed generic references inferred from an
expected function type. Generic vararg declarations use an array-valued function parameter, while
generic `ref` and `out` parameters preserve their passing modes and `out` writes enter definite-
assignment analysis after the indirect call. A non-null array may store these raw function pointers;
its element descriptor has scalar pointer width and no Yellow Tea scan shape. The array may contain
direct references or a function value passed through a typed parameter, but every produced value
still uses the receiver-free code-pointer ABI. Capturing lambdas, receiver-bound methods, nullable
function values, and non-identity adapters remain outside it. This does not define closure storage or
make arbitrary function-shaped values safe to place in a managed array.
