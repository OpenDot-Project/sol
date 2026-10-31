<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Lexical cleanup and construction effects

This page defines the source contract for `defer`, cleanup ordering, and checked effects
introduced by object construction. It is normative language documentation. A written rule does
not establish that the current compiler, runtime, or native backend implements that rule.

The source contract is [CleanupConstruction.sol](../spec/CleanupConstruction.sol), read together
with [ControlFlow.sol](../spec/ControlFlow.sol), [Exceptions.sol](../spec/Exceptions.sol),
[Objects.sol](../spec/Objects.sol), and [Delegation.sol](../spec/Delegation.sol). The examples
are independent parser inputs under `spec/examples/semantics/`. They illustrate required syntax
and analysis outcomes; they are not evidence that the source program was executed.

## Status

Sol 0.0.1 contains the initial CST frontend. It has no semantic analysis, verified FIR, LLVM
code generation, native cleanup lowering, Windows exception delivery, or runnable Sol program.
The examples and contracts in this page state requirements for later implementation. The source
analyzer's bounded body subset is not a complete proof of exceptional flow, cleanup order,
constructor validity, or native safety. No native cleanup behavior is currently claimed.

The compiler pipeline remains CST → PSI → AST → FIR → LLVM IR. Flow and effect analysis must
prove the source facts, and FIR must independently validate the operations and metadata consumed
by code generation. A parser accepting one of the forms below demonstrates only syntax and tree
ownership.

### Current development source-analysis subset

The unreleased body analyzer keeps distinct active-registration stacks with per-path assignment,
refinement, and required-field facts. It runs registered actions in reverse order when a represented
scope completes normally, returns, throws, breaks, continues, or receives a captured exceptional
edge. Cleanup exceptions during an active unwind are recorded as suppressed analysis effects; the
original primary edge remains the one that reaches catch matching. A cleanup call failure on a
normal exit can become primary. An escaping explicit `throw` in a cleanup is diagnosed, while a
locally caught throw and an exception from an ordinary call follow their respective rules.

The current implementation limits one body to 64 distinct cleanup-state alternatives, 128 active
registrations per lexical scope, and 256 exceptional unwind alternatives. These are implementation
resource limits, not language limits. Crossing a limit marks affected cleanup nodes Deferred; it
does not silently omit an action or certify the body.

The development analyzer shares source proofs for implicit, primary, and secondary constructors.
It resolves `this` and concrete-base chains, substitutes constructed owners, and carries required
storage facts through initializers, `init` blocks, helper calls, and represented cleanup edges.
Private or effectively final source helpers and known lambda callbacks are analyzed against the
current construction state. Property delegate initialization is distinct from invocation effects.
An unavailable body, uncertain receiver alias, or unfinished recursive proof remains Deferred.
These source proofs do not establish native construction lowering, receiver ABI, or publication.

Construction proof limits are 128 dependency levels, 1,024 instances, 8,192 edges, 65,536 nodes per
body, and 128 recursive-summary iterations. Exceeding a budget leaves an incomplete proof. The
existing cleanup limits also apply; no truncated prefix is accepted as a complete construction.

## Contract vocabulary

- **Scope** means the lexical owner resolved from the parsed source tree. It is not inferred from
  path sorting or visual indentation.
- **Registration point** means the execution point where control reaches a `defer` declaration.
- **Active action** means a cleanup whose registration point was reached and whose owner has not
  finished unwinding it.
- **Exit edge** means ordinary completion, return, throw, break, continue, or non-completion
  leaving one or more scopes.
- **Primary failure** means the Throwable that is delivered to a catch or caller after cleanup.
- **Suppressed failure** means a later cleanup failure retained under an existing primary.
- **Required field** means a field or backing slot that must have a valid value before the
  constructed instance is published.

“Must” and “must not” express compatibility requirements. “May” expresses permitted behavior.
An incomplete or deferred proof cannot be promoted to a successful verification result.

## `defer` registration

The syntax is a statement:

```sol
module docs.cleanup

func releaseResource(resource: Resource): () {
    defer { release(resource) }
}
```

The body is not a function value, does not return a value, and does not execute where it is
declared. It registers exactly one action for the lexical scope containing the declaration. An
empty body is allowed and has no effect when run.

Registration occurs only after execution reaches the declaration. For the branch below, a false
flag creates no registration:

```sol
module docs.cleanup

func conditional(flag: bool): () {
    if flag {
        defer { record(10) }
        record(11)
    }
    record(12)
}
```

When `flag` is true, the action runs after `record(11)` and before `record(12)`, because the
branch body is a nested scope. When it is false, `record(10)` does not occur. A join must retain
the branch-specific registration state instead of executing both paths' actions or treating the
registration as unconditional.

Expressions inside the body run when cleanup runs. To capture a value at registration time,
create an explicit local snapshot before the declaration:

```sol
module docs.cleanup

func snapshotAtRegistration(value: int): () {
    val saved = value
    defer { record(saved) }
}
```

The body uses ordinary lexical name resolution, access checks, borrow rules, and closure capture
semantics. If it reads a mutable shared cell, it observes the value available at cleanup time.
`defer` does not extend a local, reference, ref struct, or resource lifetime beyond the owning
scope. It is not a finalizer and does not acquire ownership by itself.

Declarations reached in different branches are distinct actions, even if their bodies are textually
identical. A compiler may share generated machine code, but it must keep each declaration's
activation bit and owning scope distinct. A source declaration after an unconditional transfer
cannot activate. An operation that throws before reaching a declaration leaves that declaration
inactive.

## Scope lifetime and LIFO order

Actions in one scope run in reverse registration order. Nested scopes drain from the inside out.
Consider:

```sol
module docs.cleanup

func nested(flag: bool): () {
    defer { record(1) }
    if flag {
        defer { record(2) }
        record(3)
    }
    record(4)
}
```

When `flag` is true, the observable call order is `record(3)`, `record(2)`, `record(4)`,
`record(1)`. The inner action finishes before control reaches the statement after the branch.
When `flag` is false, `record(2)` is omitted. A later outer cleanup can observe mutations made
by an inner cleanup; this ordering is part of the language contract.

The required abstract state for a scope is an ordered set of actions that have actually
registered. Implementations may use a runtime stack, static cleanup ladder, indexed frame area,
or equivalent representation. Whichever representation is chosen must preserve action identity,
activation, one-time execution, and reverse order. A flat source-order list cannot describe
conditional or repeated registration.

Cleanup actions are attempted once. A failing action is considered consumed and is not retried.
Older active actions still run. A defer reached while executing cleanup may belong to a nested
scope entered and exited entirely within that cleanup body. It cannot be appended to an owner
whose drain is already in progress.

### Scope map

| Source region | Cleanup lifetime |
|---|---|
| Callable body | Until normal callable exit or unwind |
| Nested ordinary block | Until that block is left |
| If/else arm | Until that arm completes or transfers |
| Switch case | Until that case completes or transfers |
| Catch body | Until handler completion or transfer |
| Loop body | One dynamic iteration |
| Block inside loop body | Until that nested block exits, before iteration cleanup |
| Block containing a loop | Across iterations, until the block itself exits |
| Lambda body | The lambda's own callable scope |
| Deferred action's nested block | Within that action's execution |

The table describes scope ownership, not a claim about runtime implementation. If a syntax form
does not create an independent block scope, the resolved scope tree decides the cleanup owner.

## Exit behavior

Cleanup runs on each transfer leaving its scope. The transfer target determines how many scopes
are left. Cleanup does not run merely because execution reaches a statement that looks like a
boundary in the source text.

### Fallthrough

The scope drains before execution proceeds after its closing brace. If every action succeeds,
normal execution continues. If a cleanup action fails, the normal continuation becomes an
exceptional one. Remaining actions in the same scope still run.

### Return

The return expression evaluates once before cleanup. Its resulting value is kept alive and rooted
while cleanup runs. If cleanup succeeds, the saved value is delivered. If cleanup fails and there
was no earlier failure, the return is cancelled and cleanup's first failure becomes primary.
Cleanup cannot choose a different return value or return out of its body.

```sol
module docs.cleanup

func returnAfterCleanup(): int {
    defer { record(1) }
    return computeResult()
}
```

`computeResult` runs before `record(1)`. The caller sees its value only after cleanup completes.

### Throw

The thrown non-null Throwable is selected before unwind. It remains primary while all active
actions execute. Any cleanup failure becomes suppressed under it. A matching catch receives the
original primary object with its suppression metadata intact.

### Break and continue

The nearest resolved loop target determines which scopes unwind. Leaving an iteration body runs
that iteration's cleanup. A `break` skips the counted-loop update. A `continue` runs cleanup
before the counted-loop update or collection cursor advancement. If cleanup fails, the pending
break or continue is cancelled and the failure propagates. An inner loop transfer does not drain
an outer loop's still-active registrations.

### Bottom and non-completion

`!` has no normal value or successor. If the operation throws a Throwable, the throw rules apply.
A process abort, heap corruption fail-fast, or native crash may terminate without cleanup; `!`
alone does not promise recovery from those runtime failures.

### Finally composition

`finally` and `defer` share cleanup effect and transfer rules. Their ordering follows lexical
nesting. A defer scoped inside a try body drains before that try's finally body. A defer around
the entire try remains active until the try/finally construct has completed. A return in finally
or a nonlocal return/break/continue in cleanup is invalid. An explicit `throw` that escapes a
defer or finally body is also invalid and receives SOL2254. A `throw` caught by a try/catch wholly
inside the cleanup body may be permitted because it does not escape that cleanup. An ordinary
function call inside cleanup may raise; its runtime exception follows the checked-effect and
primary/suppressed rules below.

## Cleanup failures and checked effects

If a primary exception exists, cleanup failures are suppressed under it. If no failure exists,
the first cleanup failure becomes primary. Older actions continue to run, and every later failure
is retained in the order it occurred. A cleanup body cannot directly throw an escaping exception;
it calls an ordinary operation that may raise. For example:

```sol
module docs.cleanup

class PrimaryFailure: Exception {}
class CleanupFailure: Exception {}
class EarlierCleanupFailure: Exception {}

func closeCurrent(): () throws CleanupFailure { throw CleanupFailure() }
func flushPrevious(): () throws EarlierCleanupFailure { throw EarlierCleanupFailure() }

func work(): () throws PrimaryFailure, CleanupFailure, EarlierCleanupFailure {
    defer { closeCurrent() }
    defer { flushPrevious() }
    throw PrimaryFailure()
}
```

Execution order is: `PrimaryFailure`, then `EarlierCleanupFailure`, then `CleanupFailure`. The
first cleanup failure is the newest defer because registrations are LIFO. Both cleanup failures
are suppressed under `PrimaryFailure`.

On a normal exit from the same scope, `EarlierCleanupFailure` becomes primary first; the older
cleanup failure is suppressed under it. The source analysis retains checked effects that may be
primary or suppressed. Suppression does not erase a checked effect or make an uncovered call
valid.

| Pending state on entering cleanup | Cleanup result | Outgoing primary | Suppressed entry |
|---|---|---|---|
| Normal | All actions succeed | Normal | None |
| Normal | First action throws A | A | None yet |
| Normal | A throws, later B throws | A | B |
| Primary P pending | All actions succeed | P | None |
| Primary P pending | Action throws A | P | A |
| Primary P pending | Actions throw A, then B | P | A then B |

The suppression store must retain the primary as a GC root and use bounded, allocation-aware
storage. Under memory pressure it follows a documented fallback and must never overwrite the
primary or claim to retain detail that was lost. Static effect checking includes all possible
cleanup failures even when the runtime suppression reserve is exhausted.

A checked cleanup effect must be caught or covered by the enclosing callable's `throws` budget.
The effect applies only on paths where the cleanup can execute, but branch joins must include
every action that may be active on an escaping path. A normal-exit cleanup exception can become
the caller-visible primary. A cleanup exception during unwind remains statically relevant even
though it is suppressed dynamically.

```sol
module docs.cleanup

func releaseNetwork(): () throws NetworkFailure {}

func useAndRelease(): () throws NetworkFailure {
    defer { releaseNetwork() }
    performWork()
}
```

Removing the `throws NetworkFailure` budget is invalid unless the effect is caught in a way that
preserves the intended primary/suppressed policy. `RuntimeException` descendants do not need a
checked budget, but still participate in cleanup composition and catch matching.

## Construction evaluation order

Construction is an effect-bearing operation. Explicit arguments evaluate once, left to right in
source order. Missing defaults run after explicit arguments, once each in declaration order.
Supplying a parameter suppresses its default. If an argument throws, later arguments, defaults,
initializers, and constructor body do not run. Effects of the selected constructor and executed
argument/default expressions contribute to the call site's checked effect set.

For a reference class, the initialization sequence is:

1. Evaluate explicit arguments left to right.
2. Evaluate selected missing defaults in declaration order.
3. Allocate storage and establish a private construction root.
4. Call the selected explicit base constructor.
5. Assign primary `val/var` property parameters to their storage.
6. Initialize interface `by` targets in base-header declaration order.
7. Evaluate instance field/property initializers and `init` blocks in declaration order.
8. Execute the selected constructor body.
9. Verify required fields and backing slots.
10. Publish the completed object.

Base arguments can use primary parameters as local values. The corresponding property storage
is assigned only after the base constructor completes. A `this(...)` chain runs this initialization
sequence once, before the delegating constructor body.

No user-visible reference to the new object may escape before the last step. A throw during an
initializer or body leaves earlier effects visible but does not publish a partly initialized
instance. The runtime must know which reference slots contain valid Sol references at every
safepoint.

### Constructor chaining

A secondary constructor may delegate to one constructor in its own type with `this(...)`. That
chain must be acyclic and reaches the initialization chain once. A base constructor call is
explicit and runs before derived initializers. A constructor cannot call both `this(...)` and a
base constructor directly, postpone base construction until after a field initializer, or use
source order to select among ambiguous base constructors. Missing or invalid chains are
diagnostics.

An implicit parameterless-base call is valid only when the base contract provides one accessible
constructor with exactly zero formal parameters. An overload whose parameters all have defaults
does not qualify. Explicit `super(...)` uses ordinary argument and default mapping. Struct and ref struct
construction obey their value-layout rules and do not synthesize a class base chain. Constructor
overloads differing only in their `throws` lists are not distinct overloads.

### Required fields and publication

Every required field begins unassigned. Assignment counts only after the initializer, assignment,
or selected init accessor completes normally. A field read before assignment is invalid. A
constructor cannot publish `this`, return it, pass it to unknown code, store it globally, register
an event handler, or expose it through a delegate while required fields remain uninitialized.

Calls on `this` are subject to the same proof because a virtual call can dispatch to user code
or re-enter the object. A final/private operation is allowed only if analysis proves it cannot
escape or observe incomplete state. An initializer can depend on earlier valid fields where
specified, but cannot assume that later fields have been assigned. A setter call is not proof
that an arbitrary backing slot was initialized.

GC descriptors, native stack maps, and flow facts must agree about initialized reference slots.
Zero-filled memory does not make a non-null String or Object field valid. Nullable references may
start as null where the type contract allows it. A partly initialized struct value cannot be read,
copied, returned, or stored as a complete value.

## Partial types

Partial declarations merge only when module, nesting, nominal name, generic arity, base contract,
and constraints agree. Every part must be marked `partial`. There is one primary constructor and
one logical type identity.

At most one partial part may contain side-effectful field initializers or `init` blocks. That
part's declaration order is fixed. Other parts may add members and uninitialized fields, which
the central constructor must assign. Source path, directory enumeration, manifest order, and
parallel parse order do not define initialization order. A second initializer-bearing part is
rejected even if the build currently sorts files deterministically.

Partial struct field layout follows the canonical logical field-order contract; it is not
independently sorted by the initializer part. ABI metadata, serializers, and GC descriptors use
the same resolved layout. The initialization side-effect sequence remains owned by the single
designated part.

## Checked effects from implicit construction

A constructor's checked budget covers its body and all required initialization operations,
including selected base and `this` constructors, field initializers, `init` blocks, delegated
property initialization, and property hooks used during initialization. Construction-site effects
include the selected constructor contract, explicit argument evaluation, selected defaults, and
runtime initialization operations that may execute.

`Throwable` descendants are checked except `RuntimeException` and its descendants. A type in a
`throws` list covers itself and its nominal descendants. A checked effect from a base constructor
cannot disappear in the derived constructor. `throws` lists are not overload keys, and effects
from rejected constructor candidates do not leak into the selected call.

Object, companion, and module initialization use `Uninitialized`, `Initializing`, `Ready`, and
`Failed` states. The declared initialization effect is checked at every access site, including a
read that happens after the cached value becomes Ready. A prior initialization failure remains
visible according to the cached-failure contract. It is not converted to success on the next read.

Delegated-property expression and `provideDelegate` run during initialization and contribute
construction effects. `getValue` effects belong to a read; `setValue` effects belong to a write.
`SolProperty` describes the property declaration and its accessors, not each owner's current
property value. Passing `this` to a delegate is permitted only if the owner cannot escape or be
observed before field validity is established.

## Analysis and backend requirements

Source flow state tracks reachability, assigned local identities, required field validity, active
cleanup registrations, pending transfer kind/target/value, and escaping checked effects. A
throwing operation adds an exceptional successor at its execution point. Assignments before that
operation remain visible on its exception edge; later assignments do not.

Loop analysis models cleanup for each iteration, including break and continue. A counted loop's
update occurs after iteration cleanup on normal and continue edges, and not after break. Return
values and primary Throwables stay rooted while cleanup executes. A cleanup transfer is emitted
once per active registration and does not overtake the transfer target.

FIR verification independently checks that all exceptional edges, cleanup order, checked-effect
coverage, constructor chains, required fields, initialized root slots, suppression paths, and
foreign boundaries are complete. Native lowering must preserve the verified cleanup and
exception personality behavior for Windows. A string representation of LLVM IR, syntax-tree
acceptance, or a source-only flow result does not prove that native unwinding works.

Foreign C, C++, C++/CLI, CLR, Ruby, LLVM, and OS exceptions require a verified bridge that
converts them to a Sol Throwable or explicit status carrier. Arbitrary foreign unwinding across a
Sol frame is outside the contract. Sol failures crossing a foreign callback boundary must also
be caught and translated before returning through that ABI.

## Diagnostic expectations

Diagnostics retain the source snapshot and UTF-8 byte span. At a minimum, analysis should
distinguish:

| Condition | Source location |
|---|---|
| Unreachable registration | `defer` token |
| Illegal outward transfer in cleanup | Transfer token |
| Cleanup effect not covered | The escaping call or throw |
| Missing branch-specific activation proof | Join or cleanup edge |
| Return value lost to cleanup failure | Failing cleanup operation |
| Required field read before assignment | Field read |
| Required field missing on normal constructor exit | Constructor exit |
| Possible `this` escape | Call, store, capture, or registration site |
| Constructor-chain cycle | Cycle edge or involved constructor |
| Invalid `this` plus base chain | Conflicting delegation syntax |
| Missing usable base constructor | Derived construction declaration |
| Multiple partial initializer parts | Conflicting part declarations |
| Uncovered base or field-init effect | Effect-producing operation or constructor header |
| Incomplete effect metadata | Metadata origin and consuming call |

An unavailable proof is reported as incomplete, Deferred, or resource-limited. It must not be
converted to an empty cleanup stack, pure effect set, valid `this` escape, or initialized field.
Recovery may continue to find additional diagnostics but cannot authorize native code generation.

## Source examples

The fixtures separate independent semantic cases rather than defining one monolithic program:

- `CleanupTransfer.Registration.sol`: exact activation point, conditional registration, and
  early transfer before a later declaration.
- `CleanupTransfer.ScopeNesting.sol`: inner-to-outer scope stacks and mutations observed by
  later cleanup.
- `CleanupTransfer.Loops.sol`: per-iteration stack lifetime and update ordering.
- `CleanupTransfer.ExitForms.sol`: fallthrough, return, throw, break, continue, and bottom edges.
- `CleanupTransfer.Failures.sol`: primary selection, suppression order, and checked cleanup
  effects.
- `ConstructionEffects.Order.sol`: argument/default/base/field/init/body execution order.
- `ConstructionEffects.Fields.sol`: required-field validity, failure paths, and publication.
- `ConstructionEffects.Chaining.sol`: valid delegation shape and unsupported chains.
- `ConstructionEffects.Partial.sol`: single initialization-part rule and stable logical layout.
- `ConstructionEffects.Delegates.sol`: initialization hooks versus later accessor effects.
- `ConstructionEffects.Budgets.sol`: call-site checked effects from selected construction.

Comments in fixtures identify expected analyzer outcomes. Unless a test report states otherwise,
they are design vectors and syntax inputs, not executed behavior.

## Review checklist

Before changing `defer`, answer: which lexical scope owns it; where it becomes active; which
transfers leave that scope; what order nested scopes use; when body expressions run; what happens
if a cleanup throws; which checked effects reach the caller; and how roots are retained while
unwinding. Add positive and negative independent examples for the changed rule.

Before changing construction, answer: which overload is selected; how arguments and defaults
evaluate; where base and `this` chains run; which initializer part owns side effects; which fields
are valid at each point; when `this` may escape; which effects the call site must handle; and what
the exceptional path retains. Verify metadata and ABI consumers use the same field layout and
effect identity.

Before claiming runtime support, demonstrate the exact backend and target behavior with executed
tests. A parser check, source analyzer test, LLVM textual dump, or sanitizer build alone does not
establish Windows exception unwinding, suppression, GC rooting, or publication safety.

## Worked cleanup traces

The following table makes the activation rule explicit. “Registered” means the body has been
reached; it does not mean the body has run.

| Path | Events before scope exit | Active actions at exit | Required order |
|---|---|---|---|
| `defer A; statement` | A registers, statement completes | A | A |
| `if flag { defer A }` with true | Enter arm, A registers | A in arm | A before post-arm code |
| `if flag { defer A }` with false | Arm is skipped | None | No cleanup call |
| `return; defer A` | Return transfers first | None | A is unreachable |
| `throw X; defer A` | Throw transfers first | None | X propagates without A |
| loop body reaches A | Current iteration registers A | A in iteration | A before next iteration |
| loop body skips A | Current iteration skips declaration | None for this iteration | No phantom action |
| outer A, inner B | Both declarations are reached | B in inner, A in outer | B, then A |
| outer A, branch B, then return | A and B are active | B in branch, A in callable | B, then A |

The order between a cleanup and the next ordinary statement matters. If a branch owns action B,
then leaving the branch drains B before execution proceeds after the `if`. If the callable owns
action A, A remains pending through that later statement and runs only when the callable exits.
The language does not collect all actions and run them together at the function epilogue.

### Transfer trace with a counted loop

For this shape:

```sol
module docs.cleanup

func processItems(limit: int): () {
    for var index: int = 0; index < limit; index += update(index) {
        defer { closeIteration(index) }
        if shouldSkip(index) { continue }
        process(index)
    }
}
```

The normal edge is `process` → iteration cleanup → `update` → condition. The continue edge is
`continue` → iteration cleanup → `update` → condition. The break edge is `break` → iteration
cleanup → loop exit; it skips `update`. A throw from `process` is `throw` → iteration cleanup →
outer scopes → catch or caller. An exception raised by `closeIteration` cancels normal/continue/break and
skips any later loop step. A loop-scope action declared before the loop stays active on every
iteration and is drained only when its own owner exits.

For nested loops, resolve the target before constructing the cleanup path. An inner `continue`
drains the inner iteration body. The outer iteration body stays active because the transfer target
does not leave it. An outer `continue` would drain both the inner scope being exited and the outer
iteration scope. Syntax that cannot resolve a legal target remains an error; the compiler does not
guess from nearest source indentation.

### Cleanup failure trace

Suppose actions A, B, and C are registered in that order. The runtime order is C, B, A.

| C result | B result | A result | Outgoing primary | Suppressed order |
|---|---|---|---|---|
| Success | Success | Success | Pending transfer continues | None |
| Throws Cx | Success | Success | Cx | None |
| Throws Cx | Throws Bx | Success | Cx | Bx |
| Throws Cx | Throws Bx | Throws Ax | Cx | Bx, Ax |

If the scope was already unwinding primary P, the outgoing primary remains P in all four rows.
Failures from C, B, and A become suppressed in the order they are observed. A handler matching
P receives P; a handler for Bx alone does not receive Bx as the primary. Code that wants to
recover from suppressed failures reads them from P through a separately specified API.

The implementation retains the primary while it runs C, B, and A. If a cleanup allocates, the
root map includes that pending exception. If a cleanup returns a reference-valued result for a
pending normal return, that result is retained through older cleanups as required. If the first
cleanup failure cancels a pending return, the saved return value can be released after no resume
path needs it. These root-lifetime details are part of native correctness, not optional debug
metadata.

## Worked construction traces

Construction effects follow the selected call and exact evaluation prefix. This table describes
the order and which later operations are skipped when an earlier step fails.

| Step | Operation | Failure consequence |
|---:|---|---|
| 1 | Evaluate explicit argument 1 | Stop later explicit args, defaults, allocation, and constructor |
| 2 | Evaluate explicit argument 2 | Preserve argument 1 side effects; stop defaults and constructor |
| 3 | Evaluate omitted default 1 | Preserve completed arguments; stop later defaults and constructor |
| 4 | Establish private allocation/root state | Do not publish the reference |
| 5 | Invoke selected base constructor | Do not run derived initializers or body |
| 6 | Assign primary property parameters | Storage becomes valid after the base completes |
| 7 | Initialize header interface `by` targets | Skip later targets and members after failure |
| 8 | Run field initializer or `init` block | Keep prior valid fields private; skip later initialization/body |
| 9 | Run constructor body | Keep the object private if the body throws |
| 10 | Verify required fields and publish | Publish only if every required invariant succeeds |

Operations 1–3 occur before the new object's constructor body. A nested object constructed in an
argument can be fully published before the outer allocation begins. If that argument later
throws, already completed external side effects remain; the outer constructor has not started.
Arguments are not reordered into parameter declaration order after named binding: source order
determines evaluation, then resolved parameter mapping feeds the constructor.

For an object whose initializer contains three declarations, let `a` complete, `b` throw, and `c`
follow. The state transitions are:

| Point | Binding state | `a` slot | `b` slot | `c` slot |
|---|---|---|---|---|
| Before access | Uninitialized | Not valid | Not valid | Not valid |
| `a` completes | Initializing | Valid | Not valid | Not valid |
| `b` throws | Failed | Private valid prefix | Failed/no valid value | Not evaluated |
| Later access | Failed | Not published | Failure contract retained | Not evaluated |

No reader sees the private prefix. The exact storage used for a failed prefix may be reclaimed
once its roots and failure cause are safely retained, but the implementation cannot return `a`
as though the object completed. A Ready cache fact applies only after all initializer work and
publication are complete.

## Checked-effect source matrix

| Operation | When it contributes | Example boundary |
|---|---|---|
| Explicit argument | Whenever that expression is evaluated | Call site |
| Omitted default | When the selected call omits that parameter | Call site and constructor contract |
| Base constructor | Whenever the selected chain invokes it | Derived constructor and caller |
| `this(...)` target | Whenever secondary delegation selects it | Secondary constructor and caller |
| Field initializer | When that type instance is constructed | Constructor declaration and call site |
| `init` block | At its declaration-order position | Constructor declaration and call site |
| `provideDelegate` | At delegated-property initialization | Owning initializer/constructor |
| `getValue` | On delegated property read | Read site |
| `setValue` | On delegated property write | Write site |
| Cleanup body | On paths that activate and exit its scope | Enclosing callable |
| Object/module initialization | On each access contract, including cache hit | Access site |
| Rejected overload | Never for the selected call | No effect contribution |

The table is an effect-origin map. It does not mean every operation always executes on every
path. Flow analysis selects reachable operations, overload resolution selects one declaration,
generic substitution resolves nominal effect identities, and catch analysis removes only effects
that the catch actually covers. A missing step in any of those analyses leaves the body incomplete.

## Proof evidence by layer

| Layer | Evidence it can establish | Evidence it cannot establish alone |
|---|---|---|
| CST | Token, delimiter, and syntax-tree ownership | Reachability, exception effects, or execution order |
| PSI/AST | Resolved source structure to the implemented extent | Complete cleanup edges or field validity |
| Source analysis | The facts its complete transfer functions prove | Missing callee effects, native unwind, or runtime roots |
| Verified FIR | Selected operations, effects, transfers, and field-state invariants | Correct machine execution if lowering is wrong |
| LLVM lowering | Encoded cleanup/personality/root operations | Actual Windows runtime behavior without execution |
| Executed Windows test | The exercised behavior for that exact binary/configuration | Unexecuted paths, sanitizer coverage, or universal safety |

The historical 0.0.1 release stopped at the initial CST frontend. Sol 0.1.0 admits only the source and native paths separately documented as implemented; every unsupported row remains a qualification boundary. A report must name the layer reached and the exact evidence collected. “Compiles” must
not be used as a substitute for “verified,” and “verified source” must not be used as a substitute
for “native behavior executed.”
