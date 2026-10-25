<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Control flow and checked effects

This page specifies how Sol combines normal control flow, definite assignment, value
refinement, checked exceptions, and cleanup. It is a source-language contract. The presence of
an example or a diagnostic in source analysis does not mean that the program has verified FIR,
native code, a running standard library, or a qualified runtime.

The normative source contracts are [ControlFlow.sol](../spec/ControlFlow.sol) and
[Exceptions.sol](../spec/Exceptions.sol). The independent fixtures are indexed in the
[source specification](../spec/README.md). Representative suites include
[branch joins](../spec/examples/semantics/FlowTransfer.BranchState.sol),
[loop transfer](../spec/examples/semantics/FlowTransfer.LoopEdges.sol),
[switch coverage](../spec/examples/semantics/FlowTransfer.SwitchCoverage.sol),
[nullable facts](../spec/examples/semantics/FlowTransfer.NullableFacts.sol),
[ref/out edges](../spec/examples/semantics/FlowTransfer.RefOutEdges.sol),
[try and cleanup](../spec/examples/semantics/FlowTransfer.TryCleanup.sol),
[call effects](../spec/examples/semantics/CheckedEffects.CallEdges.sol),
[catch hierarchy](../spec/examples/semantics/CheckedEffects.HierarchyCatch.sol),
[callbacks](../spec/examples/semantics/CheckedEffects.Callbacks.sol),
[defaults and varargs](../spec/examples/semantics/CheckedEffects.DefaultsVarargs.sol),
[accessors](../spec/examples/semantics/CheckedEffects.Accessors.sol),
[constructors](../spec/examples/semantics/CheckedEffects.Constructors.sol),
[overrides](../spec/examples/semantics/CheckedEffects.Overrides.sol),
[cleanup ordering](../spec/examples/semantics/CheckedEffects.CleanupOrdering.sol), and the
[process boundary](../spec/examples/semantics/CheckedEffects.UnhandledBoundary.sol).

## Analysis stages

The compiler pipeline is CST → PSI → AST → FIR → LLVM IR. Flow facts are computed over source
analysis and are later required to establish a verified FIR. They are not a second compiler
intermediate representation. A source analysis may expose expression and callable facts while
still reporting proof gaps. Native lowering accepts only the verified representation and its
side tables; it must not infer missing flow edges from syntax text.

Each source fact is tied to a resolved symbol and its owning syntax snapshot. A name is not a
storage identity. Shadowed names in nested scopes are separate locals; two aliases to the same
location are the same storage identity. Source offsets identify diagnostics and do not define
data-flow ordering independently of control-flow edges.

The conceptual flow state records:

- whether the current continuation is reachable;
- local identities definitely assigned at this program point;
- required instance fields assigned before object publication;
- stable refinements that are valid on every path reaching this point;
- checked effects that may escape from this path;
- active cleanup registrations associated with lexical scopes; and
- loop, handler, and callable exit edges that are not ordinary fallthrough.

The concrete implementation may use immutable snapshots, mutable work states, or side tables,
provided joins and transfer functions preserve the contract. Every bounded traversal is
cancellable. A resource limit produces a diagnostic or a Deferred result; it never certifies
the last partially visited state.

## Reachability and joins

A branch condition is evaluated once. The true and false successor states receive facts proved
by that condition. The then and else statements are analyzed independently. Their states join
only after reachable normal exits have been identified.

At a join, unreachable incoming states are ignored. Definite assignment is the intersection
of the assigned-local sets for all reachable predecessors. Required-field assignment follows
the same rule. Outstanding effects are the union of effects escaping through any reachable
predecessor. A refinement survives only if every reachable predecessor establishes the same
refined type for the same stable storage identity.

This rule makes the following outcomes distinct:

| Branch shape | Normal predecessors at the join | Consequence |
|---|---|---|
| Both arms assign the same local | Both arms | The local is definitely assigned. |
| One arm assigns and the other falls through | Both arms | The local is not definitely assigned. |
| One arm returns or throws, the other assigns | The assigning arm | The assignment is definite after the branch. |
| Both arms return or throw | None | There is no normal continuation after the branch. |
| Both arms prove a local non-null | Both arms | The common non-null fact survives. |
| Only one arm proves a local non-null | Both arms | The refinement is discarded at the join. |

The empty tuple type, written (), is an ordinary result type. The bottom type, written !,
has no normal value or successor. A ! branch does not add a normal predecessor. Return,
throw, break, and continue end their current continuation even though their state may still
contribute to a callable, handler, or loop exit.

Statements after an unreachable point are diagnosed at their source locations. Error recovery
continues in a bounded fashion, but unreachable code does not become a synthetic reachable
predecessor merely to suppress follow-on diagnostics.

## Definite assignment

A local without an initializer begins unassigned. Reading it before every reachable path has
assigned it is an error. Assignment updates the resolved storage identity and kills any
refinement associated with its previous value.

A declaration with an initializer becomes assigned only after its initializer has completed
normally. If evaluation of the initializer throws, the exceptional edge observes the
pre-assignment state. Assignment to a property or field also evaluates the receiver, location,
index, right-hand side, operator, and setter in the language-defined order. A failure before
the final write does not prove that the target was assigned.

Required instance fields are tracked separately from ordinary locals. A constructor must
assign required fields on every normal path before publishing the instance. A throw during
construction does not create a usable partially initialized object. Base-constructor chains,
partial-type initialization, delegated initialization, and virtual dispatch during construction
have additional contracts described by the object and initialization specifications.

Out parameters are unassigned at callee entry and cannot be read before assignment. Every
normal return from the callee must assign every out parameter. A throw path is not a normal
return and does not promise assignment to the caller. At a call site, a normally returning
out call assigns the supplied location. If the call can be skipped, as with safe-call or
short-circuit evaluation, the skipped path does not gain the assignment.

Ref parameters are assigned writable aliases at entry. Ref and out require explicit markers
at declaration and call site, exact storage type, and a location that may be written. They
cannot be satisfied by conversion, boxing, a temporary, or an ordinary property getter/setter
pair. An explicit ref-return property has its own location contract.

Every location expression for ref/out is evaluated once, in source argument order. The chosen
locations are then passed as aliases; re-evaluating a receiver or index after the call would
change observable behavior and is forbidden. Aliasing the same location through two parameters
is permitted and has ordinary shared-storage behavior.

## Refinement and invalidation

A type test or null comparison can refine a stable local on the branch where its result proves
the refinement. For example, the right operand of value != null && value.member is evaluated
only after the left operand has established non-nullness. The right operand of a short-circuit
expression does not run when that proof-producing branch is skipped.

Refinement is an expression view, not a mutation of the declared type. It does not alter a
public signature, generic constraint, overload identity, or underlying storage representation.
It cannot be transferred to a distinct alias simply because both had the same value earlier.

A write kills the previous refinement. A ref/out call invalidates the caller facts for locations
that the callee may change. An ordinary call may invalidate mutable captured storage when a
closure can write it. Unknown call effects or unresolved alias origins must conservatively clear
the affected facts or leave the analysis Deferred.

Property and indexer reads are not stable local facts. A later property read may call a getter
again and produce a different result. To keep a value stable, store it in a local and refine
that local. Mutable locals captured by an escaping closure do not retain smart-cast assumptions
across an invocation that could write them.

Safe member access evaluates its receiver once and skips the member operation if the receiver
is null. The result is lifted to a nullable value as required by the member type. It does not
make a separate local non-null outside the safe-call expression. An assertion operator can
fail at runtime; it does not change the local's declared type.

Type-test facts are scoped to the corresponding true or false edge. When the type system can
prove a final or sealed hierarchy, an exhaustive partition can contribute to a join. An open
hierarchy cannot be treated as a complete set based only on the types visible in the current
file.

## Loop transfer

The initializer of a counted loop runs once before the first condition. A condition-tested loop
has a zero-iteration edge when its condition may be false initially. An iterable loop may have
an empty-input edge. Therefore a local assigned only in a loop body is not definitely assigned
after a loop that may execute zero times.

The body backedge reaches the loop header after ordinary completion and continue. A counted
loop update runs after those body exits and before the next condition. Break leaves the loop
without running the update. Return and throw leave the enclosing callable or propagate an
exception edge. A break in an inner loop belongs to that inner loop and does not exit the outer
loop.

The loop header is a fixed point over the initializer state, condition transfer, body transfer,
update transfer, and backedge. Refinements at the header are facts stable across all iterations,
not facts observed in one pass. Implementations may choose a finite iteration bound; reaching
the bound must produce a resource/deferred outcome rather than a guessed invariant.

A literal infinite loop with no reachable break has no normal successor. If a reachable break
exists, each break state contributes to the loop's normal exit. If every body path returns or
throws and no break exists, the loop does not complete normally.

Cleanup registered in a loop body runs when that iteration exits, including continue and break.
The loop's outer cleanup registrations remain active until the outer lexical scope exits.
Nested-loop transfer lists and cleanup stacks are distinct.

## Switch transfer

The switch scrutinee is evaluated once before dispatch. Every case has an independent lexical
scope. Pattern-bound names belong only to their case. There is no implicit fallthrough from
one case body to the next.

Boolean and closed enum/sealed domains can be exhaustive. An open scalar or String domain
requires a default case. The unmatched edge of a non-exhaustive switch participates in
definite-assignment and return analysis. Duplicate or subsumed labels do not become valid
through source order.

Case payload bindings have the declared payload types and are definitely assigned only inside
their case. The underscore pattern discards a payload. Guard expressions in case headers and
arbitrary nested pattern forms are outside the language contract; use an if statement inside
the case body.

## Checked-effect sets

Throwable and its descendants are checked by default. RuntimeException and its descendants are
unchecked. The static type of a thrown expression determines the effect. If a value is stored
as Throwable, the caller must account for Throwable unless a valid type test narrows it first.
An exception's dynamic subtype does not silently narrow a broad static declaration.

A callable's throws list is an upper budget for checked effects. An escaping checked effect
must be covered by a listed type that is identical or a supertype. An unrelated sibling type
does not cover it. Unchecked effects may be caught, but do not create a checked budget
obligation.

Effects are represented as a set of resolved nominal types after generic substitution.
Equivalent duplicate entries do not create a stronger contract. Source order of effect names
does not determine overload selection, catch selection, or a primary exception.

The effect set of a selected call consists of that selected declaration's checked effects and
the effects of its actual argument/default evaluation. Effects from rejected overload
candidates are not included. A deferred or ambiguous call has no chosen effect set and cannot
be treated as pure.

Checked effect propagation includes direct throws, calls, constructors, initialization,
property accessors, operators, delegates, defaults, and cleanup. The exact edges executed at
runtime determine which operations may contribute, but every path that may escape must be
covered. Side effects that happened before a later throw are not rolled back.

## Catch matching

A catch type must be a non-null Throwable subtype. A catch matches its type and all runtime
subtypes. Catch clauses are ordered most-specific first. If a preceding handler covers every
value of a later handler's type, the later handler is unreachable.

A catch removes only incoming effects contained by its type. Catching RuntimeException does
not remove IOException. Catching IOException does not remove unrelated parse or network
failures. Effects thrown by a handler are analyzed as new effects and are not removed by the
same handler that is already running.

The catch binding is definitely assigned on handler entry and has the declared catch type.
Rethrowing that binding contributes the narrowed static type. Dynamic exception identity may be
retained by the runtime object, but effect checking does not infer a narrower subtype than the
binding proves.

A catch declared as Throwable covers every Throwable from the protected region and must be
last when no later type domain remains. The grammar has no untyped catch form. A Throwable
handler does not make its own body infallible. A catch with no possible matching incoming effect
may be diagnosed as unreachable once the compiler has a complete effect set; if the set is
incomplete, analysis must preserve that uncertainty as a proof gap.

## Finally, defer, and cleanup

Finally executes on each outgoing path from the try: normal completion, return, throw, break,
and continue. Its side effects and checked effects are applied before the pending transfer
continues. It does not create a result value and cannot use return, break, or continue to alter
the pending transfer. An explicit `throw` that escapes a cleanup body is rejected; a throw caught
inside that body remains local. If a cleanup call fails on a normal exit, its failure cancels the
pending return/value and escapes as primary; during an existing exception unwind it is suppressed
under the original primary. See the [cleanup and construction contract](cleanup-construction.md)
for ordering and the analyzer's current proof boundary.

Defer registers a cleanup action for the lexical scope containing the declaration. Only active
registrations execute. Multiple actions execute in last-in-first-out order. A nested scope
cleans up before its parent, and a loop-body scope cleans up on every iteration exit.

Cleanup may itself throw a checked effect. If a primary failure is already active, the primary
failure remains primary and the cleanup failure is attached through the bounded suppressed
failure policy. If no primary failure exists, the cleanup failure escapes normally. Neither
effect may be silently dropped.

Cleanup ordering is deterministic. It does not depend on hash iteration, source path sorting,
partial declaration order, or garbage-collector finalization. Resource exhaustion during
suppressed-failure recording follows the runtime's explicit bounded fallback contract.

The companion [cleanup and construction effects guide](cleanup-construction.md) details
registration-point snapshots, initializer evaluation order, required-field publication, delegated
property effects, and the cases the bounded body analyzer leaves Deferred. The normative source
contracts and independent syntax examples are indexed in [`spec/README.md`](../spec/README.md).

## Function types, lambdas, and overrides

Checked effects are part of a function type. A pure callback type accepts a lambda only when
its checked effects fit the empty set. A callback with a broader budget can accept a narrower
effect set. Conversion in the reverse direction would erase possible exceptions and is invalid.

Creating a lambda value does not execute the lambda body. The body is checked against its
expected function type when binding is known. Calling the function value adds its effect set at
the invocation edge. Callback effects are neither caught nor wrapped by the compiler.

Generic effect variables require a Throwable constraint. Substitution produces concrete effects
that continue to participate in call and override checking. If inference cannot determine a
unique effect set within its resource bounds, overload resolution stays ambiguous or deferred.

An override may preserve or narrow the inherited checked budget, but may not widen it with an
unrelated checked type. A virtual call checks the statically selected contract; it does not
assume that a particular runtime implementation will be chosen.

Throws clauses are not callable overload identity. Two declarations differing only by
parameter names, return type, default expressions, constraints, or effect lists do not form
separate overloads. The signature's source parameter and passing-mode rules remain authoritative.

## Constructors, accessors, and defaults

A constructor's checked budget includes its body and any required initialization operations.
Every call site must handle or declare that effect before the instance can be observed. Base
constructor and delegated initializer effects are not erased by a derived type.

An object or module-level initializer has an explicit lazy state: Uninitialized, Initializing,
Ready, or Failed. The declared checked effect remains visible on access even when the value may
already be cached. Repeated access after failure follows the cached failure contract; it does
not turn the original failure into success.

Getter and setter effects are independent. Reading a property adds the getter effects. Plain
assignment adds the setter effects. Compound assignment evaluates the receiver/location once,
then performs the read, right operand, operator, and write. Its escaping set is the union of
effects from each operation that can run.

For delegation, getValue effects belong to reads, setValue effects belong to writes, and
provideDelegate effects belong to initialization. SolProperty is declaration metadata rather
than the current value. Candidate selection and visibility are specific to the operation in use.

Explicit arguments are evaluated once in source order. Missing defaults then run once, in
declaration order. A default can reference only earlier parameter values. Its effects belong to
each call that omits that parameter. Supplying an explicit argument suppresses only that
parameter's default, including when the explicit value is an empty vararg array.

## Diagnostics and recovery

Flow and effect diagnostics retain the source snapshot and UTF-8 byte span that identifies the
failed read, branch, call, return, catch, or cleanup transfer. They are emitted deterministically
subject to the configured diagnostic cap.

The current diagnostic families include:

| Code | Meaning |
|---|---|
| SOL2240 | Read of a local that is not definitely assigned. |
| SOL2241 | Assignment to a non-writable location. |
| SOL2242 | Out parameter is not assigned on every normal exit. |
| SOL2243 | Nullable receiver is used without a valid non-null proof. |
| SOL2245 | Statement follows a non-completing path. |
| SOL2246 | Break or continue has no valid loop target. |
| SOL2247 | A normal return path does not provide the required result. |
| SOL2250 | A checked effect escapes without a covering budget. |
| SOL2251 | A thrown or caught type is not a valid Throwable type. |
| SOL2252 | A catch is subsumed by an earlier catch. |
| SOL2253 | A callback's checked effects exceed its target function type. |
| SOL2254 | Cleanup attempts a forbidden outward transfer or throw. |

Diagnostics do not themselves establish whole-program safety. An analyzer that cannot prove
an origin, effect set, exceptional edge, alias, iterator contract, or cleanup order must expose
a Deferred/unverified state rather than quietly accepting a guess.

## Current implementation boundary

The repository's source analysis provides an optional, partial body-analysis API. It checks
selected branch joins, local assignment, return coverage, some stable local refinements,
supported calls, and a bounded checked-effect subset. It also reports unverified nodes for
proofs that are not available. This API does not ship in Sol 0.0.1 and is not verified FIR.

The language contract is broader than the implemented subset. In particular, a syntax-checked
fixture does not prove complete loop fixed points, exhaustive sealed hierarchies, precise
exceptional assignment for every call, every alias invalidation, generic effect-set inference,
cleanup action publication, native Windows unwind, garbage-collected roots, or exception
runtime behavior. These contracts remain requirements until each has semantic regression
coverage and verified-FIR support.

The source corpus is copied into the test project and parsed through CST, PSI, and AST
ownership verification. That check validates the file's syntax and AST ownership; it does not
resolve every type, execute the program, prove the comments, or emit native code. Semantic
tests, FIR verification, AOT and ORC parity, and runtime validation are separate gates.

The current implementation follows the supported branch and effect paths with explicit
diagnostics. No coverage, latency, or native correctness claim should be inferred from the
number of fixtures or from a zero-error syntax parse.

## Review checklist for a new transfer rule

Before treating a new flow rule as implemented, identify its source operation, its normal
successors, its exceptional successors, and the state facts each successor observes.

Check whether each edge:

1. reads or writes a resolved local, field, property, array element, or ref/out alias;
2. creates a value whose type or nullability depends on a branch condition;
3. can throw before or after publishing an assignment;
4. terminates normal completion through return, throw, break, continue, or bottom;
5. registers, runs, or suppresses cleanup;
6. invokes a closure or virtual operation that can mutate captured storage;
7. adds a checked effect after generic substitution;
8. runs only conditionally through safe-call or short-circuit evaluation;
9. participates in an exhaustive or open-domain switch; and
10. reaches a bounded analysis result without losing source provenance.

For each rule, add one positive and one single-rule negative semantic test. Keep syntax acceptance
separate from semantic success. Assert the diagnostic code and source span for failures. Add an
observable API/FIR assertion for resolved assignment, effect, reachability, or function type where
that API exists. If a property remains unverified, assert that the analysis returns Deferred or
exposes the unverified source node.

## Fixture map

| Fixture | Principal invariant |
|---|---|
| FlowTransfer.BranchState.sol | Reachable-path joins and local refinement. |
| FlowTransfer.LoopEdges.sol | Zero-iteration, backedge, update, continue, and break edges. |
| FlowTransfer.SwitchCoverage.sol | Exhaustiveness, unmatched paths, and case scopes. |
| FlowTransfer.NullableFacts.sol | Refinement stability and invalidation. |
| FlowTransfer.RefOutEdges.sol | Exact storage, call completion, and out assignment. |
| FlowTransfer.TryCleanup.sol | Catch state, finally, defer, and normal results. |
| CheckedEffects.CallEdges.sol | Selected calls, static types, and set union. |
| CheckedEffects.HierarchyCatch.sol | Subtype matching, ordering, and rethrow. |
| CheckedEffects.Callbacks.sol | Function type budgets and invocation-time effects. |
| CheckedEffects.DefaultsVarargs.sol | Actual argument mapping and effectful defaults. |
| CheckedEffects.Accessors.sol | Getter, setter, operator, and delegate effect composition. |
| CheckedEffects.Constructors.sol | Constructor, field, and lazy initialization effects. |
| CheckedEffects.Overrides.sol | Inherited budgets and generic effect substitution. |
| CheckedEffects.CleanupOrdering.sol | LIFO cleanup and primary/suppressed failure policy. |
| CheckedEffects.UnhandledBoundary.sol | Main and foreign-boundary obligations. |

## Evaluation order and edge ownership

Flow and effects follow the order in which the language evaluates operations. The table
describes the required source-level order; it does not claim that the current backend executes
each row.

| Operation | Required order | State/effect consequence |
|---|---|---|
| Direct call | Receiver, explicit arguments in source order, missing defaults in declaration order, selected body. | Selected declaration and executed defaults contribute effects. |
| Named call | Explicit arguments in their textual source order. | Parameter mapping does not reorder side effects. |
| Ref/out call | Resolve each location once in source order, then pass aliases. | Ref may mutate the caller fact; out assigns on normal return only. |
| Short-circuit AND | Left operand, then right operand only when left is true. | Right-side facts are conditional and cannot be exported from a skipped edge. |
| Short-circuit OR | Left operand, then right operand only when left is false. | A true result does not identify which operand proved the disjunction. |
| Safe member call | Evaluate receiver once, test null, then evaluate arguments and invoke only when non-null. | The skipped path contributes neither call effects nor out assignment. |
| Compound property update | Receiver/index once, getter, right operand, operator, setter. | Read, argument, operator, and write effects accumulate; earlier side effects are not rolled back. |
| Try statement | Protected operations, matching handler, then finally on the selected exit. | A handler receives the state at its exception edge; handler effects escape outward. |
| Defer | Register at declaration, execute active registrations in LIFO order at scope exit. | Cleanup effects and writes apply to each exit that registered the action. |
| For-in iteration | Resolve iterator/cursor, advance, obtain current value, run body, clean up on exit. | Iteration and close effects follow their own exception and transfer edges. |

The order is observable when expressions write through ref/out, invoke a property getter, mutate
a captured cell, throw a checked exception, or register cleanup. A compiler must not reorder
named arguments to parameter order or execute a missing default before the explicit arguments.
The call mapping and body analysis therefore share one selected candidate and one immutable
argument map.

## Location and capture invalidation

A mutable local may be narrowed before a call and invalidated by an operation that can write
its storage. Ref/out passing identifies direct writable aliases. A captured mutable local
uses a shared cell; a closure invocation may update it. Passing an ordinary reference value by
copy does not itself permit the callee to rebind the caller's local slot, though the referenced
object may be mutable.

When an operation's alias or capture effects are incomplete, the analyzer cannot keep a
non-null/type fact solely because it found no direct assignment in the current syntax node.
It must either invalidate conservatively or expose the unresolved proof. A getter call does not
turn a property into stable local storage. Storing a getter result into a val provides a
separate stable local identity.

Ref/out aliases cannot be captured by an escaping closure. Ref struct values and stack borrows
cannot be boxed, stored in ordinary heap fields, or captured beyond their permitted scope.
Returning a borrow requires an explicit origin relation to caller-owned storage. The language
does not treat a non-moving object address as proof of lifetime.

## Iterator and cursor cleanup

An array or supported collection iteration has an empty-input edge. The loop body may execute
zero times, so body-only assignments are not definite after the loop. The update/backedge
rules still apply when the collection is nonempty.

The resolved cursor contract can include iterator creation, moveNext, current, and close
operations. Each operation has its own callable and effect identity. A cursor is closed on
normal exhaustion, break, return, and exceptional unwind. Continue exits the current
iteration scope and runs iteration cleanup before the next advance. Nested cursors close inner
before outer.

A ref-like cursor remains in a scoped location; lowering cannot box it to satisfy an ordinary
interface call. If cursor resolution, exception edges, or cleanup registration is not fully
known, body analysis does not certify the collection loop. It must retain Deferred state until
the verifier has the resolved protocol and lifetime facts.

## Effect identity and source provenance

Effects use resolved nominal type identities. A simple type name is insufficient when two
modules declare a type with the same spelling. Generic effect parameters are substituted before
budget comparison. A subtype is covered by a base type only when the resolved inheritance path
proves that relation.

The source origin for a propagated effect includes the declaration that introduced the
contract and the call/access site that may expose it. For a default, both the default expression
span and the call that omitted it matter. For a delegated property, getValue, setValue, and
provideDelegate have different operation spans. For cleanup, the registration and exit sites
explain why a deferred action is active.

Diagnostics are stable for one syntax snapshot and use UTF-8 source byte spans. Related origins
are ordered deterministically. When many candidate origins contribute to a conflict, the
diagnostic limit applies without changing set membership or selecting a candidate.

## Inherited defaults and effect budgets

The statically selected declaration supplies default expressions; virtual dispatch selects the
body. An override cannot replace a base parameter's default. The selected default is evaluated
at each call that omits the argument, after explicit arguments. Its checked effect remains
visible even if the virtual implementation is a pure cache hit.

If multiple inherited declarations provide different defaults for the same parameter, the
compiler reports a conflict that requires explicit resolution. Source path, interface order,
and hash-map order do not choose a default. An explicit argument avoids evaluating either
default, but it does not remove effects of other omitted parameters or of the selected body.

When several interfaces contribute effects, an implementation must satisfy each contract that
applies to the resolved signature. A compatible narrower implementation may serve multiple
views. Unrelated effects cannot be merged by choosing an arbitrary declaration; incompatible
defaults or contracts require a source-level resolution or a diagnostic.

## Native and process boundaries

The Sol source contract ends at explicit adapters. The managed compiler process uses the CLR
for its own state. AOT Sol executables and the ORC execution host use the guest runtime and
Yellow Tea heap in their own processes. A compiler-side LLVM bridge does not execute guest
objects in the host CLR process.

At a C ABI boundary, a foreign exception is caught by the bridge and converted into a declared
status, diagnostic, result carrier, or Sol Throwable. A Sol callback catches checked failures
before returning through a foreign stack. The same failure is not represented as both a raw
foreign exception and a Sol object crossing the boundary.

The Sol wrapper's declared effect is analyzed like any other call. An extern declaration does
not implicitly declare arbitrary foreign exceptions. A status adapter may explicitly map a
known failure code to a Sol exception type; that mapping is part of the wrapper implementation
and its test contract.

## Acceptance matrix

Every new control-flow/effect rule should have evidence at the layer that owns the rule:

| Evidence layer | Example question |
|---|---|
| CST parse | Does the source spelling form the intended statement, type, lambda, or effect clause? |
| PSI/AST | Are grouped parameters, catch types, case bindings, and source origins retained? |
| Source analysis | Does a positive case resolve, and does a single-rule negative case identify the expected diagnostic? |
| FIR | Are reachable successors, exact storage, selected calls, effect sets, and cleanup actions explicit? |
| FIR verifier | Are all normal/exceptional paths covered and are resource limits fail-closed? |
| LLVM lowering | Do AOT and ORC lower the verified transfers and effects with matching semantics? |
| Runtime | Do Windows unwind, cleanup suppression, roots, and exit reporting behave as specified? |

Passing one row does not imply success in the following rows. A syntax-checked .sol file is not
a semantic test. A source-analysis success is not a verified-FIR certificate. An LLVM verifier
success is not a runtime test. CI status and package/release qualification are separate evidence.

## Flow state and proof boundaries

The source analyzer tracks a flow state for each reachable program point. It associates facts
with resolved storage identities rather than identifier text. A local slot records whether a
value has been assigned, its declared and currently narrowed type, nullability facts, and the
write version that produced the value. Ref/out aliases and mutable captures point to the storage
they can update. A field or property read is a separate evaluation and is not treated as a stable
local unless the compiler proves that the storage cannot change.

At a branch, the analyzer evaluates the condition once and creates true/false states. Stable
local tests can refine each state. At a join, only facts true on every incoming normal edge
survive. If one branch returns or throws, it contributes no normal state. If a branch is
unreachable, it contributes no state and receives an unreachable-code diagnostic. A missing
exhaustive switch case or a loop's zero-iteration edge remains a real path; the compiler cannot
assume a body executes just because its source contains an assignment.

Assignments have explicit evaluation order. The compiler evaluates a target's receiver and
indices once, evaluates the right side once, and writes only after both complete. A compound
assignment reads the target, evaluates the right operand, calls the selected operator, then
writes the result. A throw at any intermediate step creates an exceptional successor before
later steps. Earlier side effects remain in that successor. There is no rollback implied by
assignment syntax.

Facts about a local reference and facts about the referenced object are different. Copying a
reference into an immutable local preserves that local value if the source field changes. It does
not freeze mutable fields of the object. Assigning one local alias does not reassign another local
slot. Passing an exact slot by `ref`, invoking a closure that writes a captured cell, or calling
a method that can reach the slot can invalidate a prior refinement. A compiler summary can
retain unrelated facts when alias analysis proves the call cannot affect them.

### Path joins

For `if flag { result = 1 } else { result = 2 }`, `result` is definitely assigned after the
conditional if both arms complete normally. If the `else` arm returns, the continuing state
contains only the write from the true arm. If the `else` arm can complete without writing,
definite assignment is not established. The same rule applies to `out` parameters, except that
the declaration must establish the required writes on every normal function return.

For a loop, the first condition edge is included in the join. An assignment performed only in
the body is not definite after a loop that can execute zero times. The analyzer computes a
bounded fixed point over entry and backedge states. `continue` runs the counted-loop update before
the next condition; `break` bypasses the update and contributes an exit state. Nested loops retain
separate transfer targets. If the iteration budget is exceeded, the result is deferred or
resource-limited; the last partial state is not accepted as a proof.

For a switch, the scrutinee is evaluated once, then each matching label produces an isolated
case state. Payload bindings exist only in that case. There is no implicit fallthrough. Normal
completion joins the cases that can complete and the unmatched path when the domain is not
exhaustive. Duplicate labels and impossible defaults are errors; they do not create useful
assignment paths. A branch that returns, throws, or has type `!` is removed from the normal join.

### Exception edges and cleanup

Every potentially throwing operation creates a successor at its evaluation point. If `read()`
throws before a later assignment, the catch state cannot observe that assignment. If an earlier
assignment completed before `read()`, the exceptional state can observe it. A callee's `out`
contract guarantees assignment after normal return; it does not imply assignment when the call
throws. An interprocedural body summary may provide stronger exceptional-edge facts when verified.

Each typed catch receives only the protected exceptional edges whose types it covers. A catch
body's own failure leaves that handler and proceeds to an outer handler or the current function's
effect boundary. Sibling catches do not catch each other. `finally` runs after handler selection
and before the pending transfer leaves the try. Its assignments affect a pending return and its
effects can become the outgoing failure. A cleanup exception is primary when there was no earlier
exception; otherwise it is recorded as suppressed under the runtime cleanup policy.

`defer` is active only after execution reaches its declaration. It belongs to the current lexical
scope and executes in last-in-first-out order when that scope exits normally or abruptly. A defer
inside a loop body runs per iteration, including on `continue` and `break`. Conditional branches
can therefore carry different active cleanup stacks. Cleanup cannot return from the enclosing
function or jump to an outer loop. The CFG must represent the pending transfer and cleanup stack
without flattening away path identity.

### Checked-effect collection

The compiler first resolves the selected callable, substitutes generic arguments, and determines
virtual dispatch and callback types. It evaluates the receiver and explicit arguments in source
order, then selected defaults in declaration order. An explicit argument skips that parameter's
default expression and its effects. After arguments complete, the call contributes the selected
declaration's checked budget. Creating a function value does not invoke it; calling that value
does. A callback's own effect set survives nesting, conversion, and return.

Property and operator syntax contributes the effects of the actual selected operations. A simple
property assignment needs the setter's budget. A compound property assignment needs the getter,
operator, and setter budgets in execution order. A delegate's initialization hook is distinct
from later reads and writes. An effect from a rejected overload does not leak into the selected
candidate. Branch-local effects are unioned only when they can escape; a matching catch removes
the covered incoming type family, then handler and cleanup effects are added.

Coverage uses resolved exception identity and nominal subtype relationships. A declared base
type covers a checked descendant. A declared descendant does not cover a possible base or
unrelated sibling. `RuntimeException` descendants are unchecked according to the language's
hierarchy; a value statically typed as `Throwable` still carries the broader checked obligation
until narrowed. An unknown or legacy metadata contract is not silently treated as pure.

## Diagnostic and implementation status

| Situation | Planned source result | Current compiler status |
|---|---|---|
| Read of a local before definite assignment | Error at the read | Syntax tree only |
| Missing `out` write on a normal exit | Error at the function exit/parameter | Syntax tree only |
| Nullable local read after a write-capable call | Require a new proof or report unsafe access | Syntax tree only |
| Missing return on a reachable normal path | Error at the function body | Syntax tree only |
| Uncovered checked effect | Error at the operation introducing it | Syntax tree only |
| Duplicate or unreachable catch | Error at the handler type | Syntax tree only |
| Cleanup transfer escaping its region | Error at the transfer | Syntax tree only |
| Incomplete graph/effect summary | Deferred or resource diagnostic | Syntax tree only |
| Verified FIR with complete control/effect edges | Required before native lowering | Not implemented |

The source span on a diagnostic identifies the read, operation, branch join, handler, or transfer
that needs attention. Diagnostics are ordered deterministically and retain UTF-8 spans tied to
the parsed syntax snapshot. A recovery tree can support IDE navigation, but recovered or
incomplete analysis is not a verified body. A successful parse does not mean that control flow,
exception coverage, definite assignment, or native safety has been established.

The examples in `spec/examples/semantics/` are standalone modules for syntax ingestion and future
semantic fixtures. Each example's comments label the intended result. Until semantic flow and
effect analysis are implemented and tested, those comments are explanatory contract text only.
No example here is evidence of a runtime guarantee or a passing semantic test.
