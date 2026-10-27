<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Generic functions and function values

This guide defines the boundary between a direct generic call, a function value, a lambda, and a
closure. These forms can share the same visible parameter and result types, but they have different
selection, lifetime, execution, and ABI requirements.

The current implementation has a bounded native FIR path for selected module-level generic free
function calls. It does not have a native representation for function references, closure
environments, generic function values, or indirect calls. Source body analysis can understand
selected function-value and lambda shapes, but source analysis alone does not authorize native
emission.

The normative examples are in [GenericFirFunctionValues.sol](../../spec/GenericFirFunctionValues.sol)
and [the semantic case matrix](../../spec/examples/semantics/GenericFirFunctionValues.sol). This page
describes implementation requirements and how to interpret the cases; it does not claim that all
listed scenarios have been executed.

## Four related language objects

| Object | What is selected or represented | Current native status |
|---|---|---|
| Generic function declaration | One source declaration with owner-scoped type parameters | Source declaration and constraints are available to analysis. |
| Direct generic call | One selected declaration and one ordered, closed type-argument list | Bounded monomorphic FIR specialization is implemented. |
| Function value | A callable value with one resolved function type and possibly an environment | No verified FIR node or native ABI. |
| Lambda or anonymous function | A source expression with contextual parameter/result types, effects, flow and captures | Partial source body analysis; native closure conversion is rejected. |

The distinctions are observable. A direct call executes a selected function now. A function value
can be stored, returned, passed to another function, or invoked after the expression that created
it. A closure can retain locals beyond their lexical stack frame. A generic function value may need
to preserve type parameters after the expression site has disappeared. These are not consequences
of simple type substitution.

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
implemented generic body facts are substituted from source analysis; full per-instance semantic
rebinding remains future work.

## Function values are not inferred by calling a function

The expression `identity` in a value context is not equivalent to `identity(0)`. The latter is a
call that evaluates an argument and returns a value. The former requests a callable object or code
reference. It needs a rule that selects an overload, closes generic parameters, preserves declared
effects, and specifies whether the result carries an environment.

The language does not currently specify a generic reference suffix or a runtime-polymorphic function
value. The implementation must not invent syntax such as an ad hoc type-argument suffix, synthesize
a dummy call, or create an invisible closure as a convenience. A proposal must define the surface
syntax and semantics before a backend chooses a representation.

An expected function type can constrain a future value conversion, but it must not execute the
target or evaluate declaration defaults. The target may escape and be invoked in a different context.
Any inference performed at the value expression must close all required generic parameters and
remain valid for every invocation permitted by the resulting type.

## Candidate selection in a value context

An overloaded function name in a value position is a candidate group. A future conversion rule must
filter candidates by visibility, parameter count, parameter types/modes, result type, and declared
effect compatibility. It must then apply one documented ranking relation over the complete
candidate set.

Candidate analysis is non-executing. It does not run a getter, default expression, lambda body,
constructor, or generic body to learn whether a candidate fits. Candidate-local inference facts are
isolated and are published only after one candidate wins. Deferred candidates cannot be silently
discarded when they could change the result.

| Candidate result | Required behavior |
|---|---|
| One unique applicable candidate | Convert only if all generic arguments and ABI obligations are closed. |
| No applicable candidate | Report a source value-conversion error. |
| Equal best candidates | Report ambiguity with candidate origins. |
| A candidate remains semantically deferred | Preserve Deferred if the candidate could alter the winner. |
| Winner is outside native FIR | Reject native emission for that winner; never fall back to another overload. |

The detailed ranking rules belong to generic call and overload analysis. A backend must not choose a
candidate based on code-generation convenience, symbol order, declaration order, or whether its body
happens to be lowerable.

## Function types and identity

Function types are resolved structures. Their identity includes the resolved parameter types,
parameter passing modes, result type, nullability, and declared checked-effect set. Type-parameter
references include declaration owner and ordinal; a display name such as `T` is not sufficient.

Function effects use set semantics. Reordering an effect list does not create a different semantic
budget. A conversion cannot erase a checked effect merely because an LLVM calling convention does
not encode exceptions. A callback that can throw `IOException` cannot be assigned to a context that
promises no checked effect unless a source rule handles that effect.

Sol has not completed general function variance and effect conversion. Until those rules are
implemented, exact structural function-type identity is a safe boundary. Machine-code pointer
compatibility, equal word size, or a cast does not establish source compatibility.

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

This candidate-local source analysis does not mean the lambda can be emitted inside a generic FIR
instance. If the generic body invokes a callback, current verified FIR has no indirect-call node and
the native gate rejects the function-value operation.

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

The current body analyzer has ref/out call and assignment facts for supported forms. That does not
qualify a native function-value call; the FIR call node is a direct target ID and the verifier checks
the direct source binding.

## Checked callback effects

The checked-effect set is part of the function type. Creating a closure does not execute its body.
Invoking it does, so a direct or indirect call edge contributes its declared effects to the enclosing
function budget. A wrapper that returns a callback without invoking it has different effect flow from
a wrapper that invokes it immediately.

For a generic effect parameter, substitution preserves the owning parameter identity until the call
is selected. The concrete effect must be a `Throwable` subtype and must fit the caller's checked
budget after any catches or cleanup are applied. Effect sets are not matched positionally.

The current source analysis includes checked effects for some function-value calls and lambda
bodies. The generic native specialization verifier now validates selected direct-call effect
metadata, but it cannot lower an indirect callback invocation. Runtime exception dispatch and source
catch/finally/defer lowering remain separate backend requirements.

## Defaults and varargs

Defaults and varargs belong to a selected declaration. A function type does not, by itself, identify
the declaration whose default expression scope should be used. A generic function reference may
also be overloaded across declarations with different defaults or vararg positions.

A future reference conversion must decide whether a generated wrapper preserves defaults, requires
the caller to pass every function-type parameter, or has another explicit rule. It must define the
vararg's source-level sequence and whether a call passes a named array, expanded elements, or both.
Defaults are call-time expressions evaluated in the declaring callable's parameter scope; a value
conversion cannot eagerly execute them.

Until that rule exists, the generic direct-call argument mapper cannot be used as proof that a
function value accepts omitted arguments or a vararg expansion.

## Capture and closure environments

An immutable local captured by a closure can be copied only if its representation and lifetime allow
the copy. A mutable `var` uses a shared cell when multiple closures or the creating frame must observe
the same storage. Copying each closure's own snapshot would change program behavior.

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

An indirect call evaluates the target once, evaluates arguments once in source order, performs
declared default/vararg mapping if allowed, and invokes the concrete function type. It needs explicit
exceptional and normal flow edges. Ref/out effects update the caller's storage state only according
to the callback's normal-return contract.

Before execution, the compiler/runtime must validate or guarantee a non-null callable target. It
must preserve the actual target identity through control flow and optimization. A nullable function
container and a function returning a nullable value are distinct types.

If an indirect call can throw, the caller's catch and cleanup analysis must see the declared effect.
If the call is cancelled or fails, temporary roots and borrowed locations must still be released
correctly. AOT and ORC may lower the call differently, but cannot change these source semantics.

## Current FIR representation

The existing FIR model has `FirFunction`, `FirCall`, slots, statements, and expression nodes for the
admitted native subset. A direct generic `FirCall` records its ordered closed `TypeArguments` and
the exact monomorphic function ID. There is no `FirFunctionReference`, `FirClosure`, `FirIndirectCall`,
closure environment, or runtime generic descriptor.

`FirVerifier` requires calls to be anchored to a source call binding and checks the selected target,
type arguments, concrete signature, effects, source origins, and reachability. This model proves a
direct call edge. A local variable with a function type cannot be treated as the ID of an arbitrary
source function without a new FIR node and independent verification rules.

`SOL2400` is the current native source-admission diagnostic for a shape outside this verified subset.
`SOL2402` identifies an independent FIR verifier failure. `SOL2403` identifies a generic
specialization work bound. A missing function-value implementation is not a resource exhaustion and
must not be reported as one.

## LLVM and artifact boundary

The LLVM backend consumes verified FIR and constructs LLVM objects through the LLVM 23.1.3 C++ API
bridge. Function-value support must extend the verified FIR model and lowerer first. Creating a
function pointer by casting a direct function symbol is not a source-level implementation of
closure conversion.

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

Before generic function values are admitted to native FIR, qualification should include the
following dimensions:

| Dimension | Positive and negative cases |
|---|---|
| Generic target | Explicit closed arguments, inferred direct calls, open/foreign argument rejection |
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

Function values can enter generic native FIR only after all relevant gates are implemented together:

1. The language defines generic function references, overload-to-value ranking, function variance,
   effect compatibility, defaults/varargs, and any polymorphic-lambda syntax.
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

Until these gates pass, the native generic feature remains a direct-call specialization slice. The
language specifications may describe future function-value semantics, but public status must keep
those semantics separate from current executable behavior.
