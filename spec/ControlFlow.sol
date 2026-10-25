// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: the source analyzer checks a bounded subset of assignment, branch and return flow.
// Full CFG construction, loop fixed points, complete pattern exhaustiveness, borrow verification
// and verified FIR are not implemented by this source contract.
module source.flow.rules

enum Command { Stop, Move(x: int, y: int), Rename(value: String) }

func loops(items: []int): int {
    var total = 0
    for var index: int = 0; index < items.length; index += 1 {
        total += items[index]
    }
    for val item in items { total += item }
    for total < 100 { total += 1 }
    for {
        if total == 100 { break }
        continue
    }
    total
}

func describe(command: Command): String {
    switch command {
        case Command.Stop: return "stop"
        case Command.Move(x, y): return "move"
        case Command.Rename(value): return value
    }
}

func cleanup(): () {
    defer { /* Release a scoped resource here on every exit. */ }
    val answer: int = try { 42 } finally { /* No value or outward jump. */ }
}

// Conditions are bool, not truthy numbers/objects. if/for require braced bodies.
// Switch scrutinee evaluates once. Cases have independent scopes and no fallthrough.
// Payload patterns introduce flat bindings. Exhaustive enum/sealed/bool switches can
// prove a return; open scalar/String switches need a default. Type patterns use case is T.
// The final normal expression returns from an annotated block function or lambda.
// Assignment/declaration/loop does not yield a value. A ! path has no normal continuation.
// Collection cursors use resolved iterator/moveNext/current and optional cleanup contracts.

// FLOW STATE MODEL
//
// Analysis is over reachable continuations, not over source indentation. A state contains at
// least: reachability, definitely-assigned local identities, definitely-assigned required
// fields, stable local refinements, outstanding checked effects, and the active cleanup stack.
// Facts belong to resolved symbols and source locations. Equal names in different scopes are
// different storage identities. A source path or declaration order is never a substitute for
// a control-flow edge.
//
// At a join, unreachable predecessors are discarded. Definite assignment and required-field
// facts are the intersection of the remaining predecessors. A refinement survives only when
// every remaining predecessor proves the same non-null or narrowed type for the same stable
// local. Outstanding effects are the union of effects that can escape along any predecessor.
// The join is conservative: a fact proved on one arm cannot be borrowed by the other arm.
//
// A return, throw, break, continue, and bottom-valued expression terminate the current normal
// continuation. Their side effects and escaping effects still belong to the edge they leave.
// A branch that terminates does not weaken facts on the other continuing branch. If every arm
// terminates, the join has no normal continuation. Diagnostics after that point identify the
// first unreachable statement in each source sequence; cascading unreachable diagnostics are
// bounded by the configured per-compilation diagnostic limit.

func assignOnBothArms(flag: bool): int {
    var answer: int
    if flag {
        answer = 10
    } else {
        answer = 20
    }
    answer
}

func assignmentOnOneArmIsInsufficient(flag: bool): int {
    var answer: int
    if flag {
        answer = 10
    }
    // Negative: the false edge reaches this read with answer unassigned.
    answer
}

func terminatedArmDoesNotParticipate(flag: bool): int {
    var answer: int
    if flag {
        return 10
    } else {
        answer = 20
    }
    // Positive: the only normal predecessor assigns answer.
    answer
}

func allArmsTerminate(flag: bool): int {
    if flag {
        throw Failure("left")
    } else {
        return 20
    }
    // Negative: this statement has no reachable predecessor.
    30
}

// FLOW-SENSITIVE LOCAL REFINEMENT
//
// A local can be narrowed by a positive type test or a non-null comparison. Refinement is
// attached to the local's exact storage identity. A property expression is not a local snapshot:
// reading owner.current once does not prove a later read has the same value. A mutable captured
// local is not stable across closure invocation. A write to a local invalidates its old fact
// before the next read. Ref/out arguments invalidate the affected storage facts according to the
// callee's passing mode; an out write establishes assignment only on a call path that returns
// normally. A short-circuit right operand may use facts established by the left operand, but
// those facts do not escape the conditional expression unless every normal path proves them.
//
// Smart-cast facts never change the declared type, overload identity, storage representation,
// generic argument, or object identity of the local. They select a safe view for an expression.

open class Animal {
    val age: int = 1
}

class Dog: Animal {
    val bark: int = 2
}

class MutableHolder {
    var current: Animal? = null
}

func nonNullArm(value: Animal?): int {
    if value == null {
        return 0
    }
    // Positive: the remaining edge proves value is non-null.
    value.age
}

func falseArmIsAlsoAProof(value: Animal?): int {
    if value != null {
        return value.age
    } else {
        return 0
    }
}

func positiveTypeArm(value: Animal): int {
    if value is Dog {
        return value.bark
    }
    0
}

func mutationKillsOldRefinement(value: Animal?): int {
    var current = value
    if current == null {
        return 0
    }
    current = null
    // Negative: the assignment killed the previous non-null fact.
    current.age
}

func propertyReadIsNotAStableSnapshot(owner: MutableHolder): int {
    if owner.current != null {
        // Negative: each property read invokes a getter and may observe a different value.
        return owner.current.age
    }
    0
}

func captureDoesNotCreateImplicitStableStorage(value: Animal?): int {
    var current = value
    if current == null {
        return 0
    }
    val observe = { current?.age ?: 0 }
    val afterCapture = observe()
    current.age + afterCapture
}

func shortCircuitRightOperand(value: Animal?): bool {
    // Positive: the right operand is evaluated only while value is non-null.
    value != null && value.age > 0
}

func conditionalWriteDoesNotEscape(value: Animal?): int {
    var current = value
    current != null && clear(out current)
    // Negative: the right side may be skipped, and the out call may replace current.
    current.age
}

func clear(out value: Animal?): bool {
    value = null
    true
}

// LOOP TRANSFER
//
// Loop entry is the zero-iteration predecessor for a condition-tested loop and a collection
// loop. Consequently, assignments made only by the body are not definitely assigned after a
// loop that may execute zero times. A for loop's initializer executes once before the first
// condition. Its update executes after each normally completed body and after continue, but not
// after break, return, or throw. continue targets the nearest loop's update/condition edge;
// break targets the nearest loop's normal exit. Inner-loop exits do not become outer-loop exits.
//
// A fixed-point algorithm must be finite and cancellable. The input is the previous header
// state, and the output is the join of the initializer edge with all reachable backedges. A
// refinement is retained only if it is invariant across that fixed point. If a resource bound
// prevents convergence, analysis returns a resource diagnostic or Deferred result; it must not
// guess that the last observed iteration is stable.

func zeroIterationDoesNotAssign(flag: bool): int {
    var value: int
    for flag {
        value = 1
        break
    }
    // Negative: the first condition can be false.
    value
}

func initializerExecutesOnce(limit: int): int {
    for var index: int = 0; index < limit; index += 1 {
        if index == 4 {
            break
        }
    }
    // Positive: index is initialized before the first condition and remains in scope
    // according to the loop declaration's lexical scope rule.
    index
}

func continueRunsUpdate(limit: int): int {
    var total = 0
    for var index: int = 0; index < limit; index += 1 {
        if index == 2 {
            continue
        }
        total += index
    }
    total
}

func breakDoesNotRunUpdate(limit: int): int {
    var count = 0
    for var index: int = 0; index < limit; index += 1 {
        if index == 2 {
            break
        }
        count += 1
    }
    count
}

func infiniteLoopWithoutBreak(): ! {
    for {
        continue
    }
}

func infiniteLoopWithReachableBreak(flag: bool): int {
    for {
        if flag {
            break
        }
        continue
    }
    // This exit is reachable only through the break edge.
    1
}

func nestedBreakIsLocal(flag: bool): int {
    for {
        for {
            if flag {
                break
            }
            break
        }
        if flag {
            break
        }
    }
    0
}

// SWITCH TRANSFER
//
// The scrutinee is evaluated once before dispatch. Each case has its own pattern bindings and
// lexical locals. No case implicitly falls through. Exhaustive enum, sealed-type, and Boolean
// switches have no unmatched edge; open scalar and String domains require a default case.
// Pattern exhaustiveness is based on the resolved closed type set, not only visible syntax.
// Repeated equivalent labels are errors. A guarded case header is not part of the language;
// use an if statement inside the case body. An explicit default is still unreachable when
// preceding cases cover the entire closed domain and is diagnosed as such.

enum Step {
    Done,
    Moved(distance: int),
    Failed(reason: int),
}

func exhaustiveEnum(step: Step): int {
    switch step {
        case Step.Done:
            return 0
        case Step.Moved(distance):
            return distance
        case Step.Failed(reason):
            return -reason
    }
}

func nonExhaustiveEnum(step: Step): int {
    switch step {
        case Step.Done:
            return 0
        case Step.Moved(distance):
            return distance
        // Negative: Step.Failed remains an unmatched continuation.
    }
}

func openDomainNeedsDefault(code: int): int {
    switch code {
        case 0:
            return 1
        case 1:
            return 2
        default:
            return -1
    }
}

func caseScopesAreIndependent(step: Step): int {
    switch step {
        case Step.Moved(value):
            val local = value
            return local
        case Step.Failed(value):
            val local = value
            return -local
        case Step.Done:
            return 0
    }
}

func defaultOnExhaustiveBoolean(value: bool): int {
    switch value {
        case true:
            return 1
        case false:
            return 0
        // Negative: every Boolean value is already covered.
        default:
            return -1
    }
}

// TRY, CATCH, FINALLY, AND DEFER
//
// A try statement's body and each catch handler are distinct incoming paths. A handler receives
// the definite-assignment facts available at its exception edge, not assignments that occur
// later on the normal path. A catch removes only effects whose possible runtime types are
// subtypes of its declared type. Effects thrown by the handler are added to the outgoing set.
// Catch ordering is most-specific first; a later handler whose type is already covered by an
// earlier handler is unreachable.
//
// Finally runs on every outgoing normal or exceptional edge, including return, throw, break,
// and continue. It cannot supply a try-value result or transfer control out of the enclosing
// scope. An assignment in finally occurs before a pending return becomes observable. Cleanup
// effects remain in the enclosing effect set. If both a primary operation and cleanup throw,
// the original failure remains primary and the cleanup failure is recorded as suppressed.
//
// Defer is attached to the lexical scope in which it appears. Several active defer actions run
// in last-in-first-out order. A defer registered in an arm/loop body does not outlive that scope.
// The action runs on normal exit and unwind. It cannot return, break, or continue out of the
// cleanup body. Conditional registration and exceptional cleanup edges must be represented
// explicitly; source-order approximation is insufficient.

open class Failure: Exception {}
class SpecificFailure: Failure {}

func risky(flag: bool): int throws Failure {
    if flag {
        throw SpecificFailure()
    }
    4
}

func catchSeesPreTryAssignment(): int {
    var value: int
    try {
        risky(true)
        value = 1
    } catch (failure: Failure) {
        // Negative: the throw can occur before value is assigned.
        return value
    }
    value
}

func catchAfterPreTryAssignment(): int {
    var value: int
    value = 0
    try {
        risky(true)
        value = 1
    } catch (failure: Failure) {
        // Positive: this assignment dominates both the normal and exceptional edges.
        return value
    }
    value
}

func finallyCompletesOut(out result: int): () {
    try {
        return
    } finally {
        result = 1
    }
}

func finallyCannotReturn(): int {
    try {
        return 1
    } finally {
        // Negative: cleanup may not replace the pending return value.
        return 2
    }
}

func deferRunsInReverseOrder(): () {
    defer { record(1) }
    defer { record(2) }
    record(3)
}

func record(value: int): () {}

func deferCannotEscape(): () {
    for {
        defer {
            // Negative: this break would transfer out of the cleanup body.
            break
        }
        break
    }
}

// RETURN AND BOTTOM
//
// For a non-empty declared return type, every reachable normal completion must provide a value
// convertible to that type. An explicit return terminates only its own callable scope. A lambda
// cannot use a bare return to leave its enclosing function; a label-qualified return targets
// the resolved labelled lambda. A throw expression has type ! and no normal successor. A loop
// proven infinite with no reachable break has no normal successor. Statements following a
// proven non-completing path are unreachable. () is the empty tuple value and is distinct from
// the uninhabited bottom type !.

func bothBranchesReturn(flag: bool): int {
    if flag {
        return 1
    } else {
        return 2
    }
}

func missingBranchResult(flag: bool): int {
    if flag {
        return 1
    }
    // Negative: the false edge falls through without an int.
}

func valueTry(flag: bool): int {
    try {
        if flag {
            return 1
        }
        throw Failure()
    } catch (failure: Failure) {
        2
    } finally {
        record(0)
    }
}

func bottomTry(): ! {
    try {
        throw Failure()
    } catch (failure: Failure) {
        throw SpecificFailure()
    }
}

// Ref/out completeness is path-sensitive. Ref starts assigned and writable. Out starts
// unassigned at the callee and at the caller. Every normal callee exit must assign it; throwing
// exits make no assignment promise. At the caller, a normally returning call establishes the
// out location as assigned, while a safe call only establishes it on the branch where invocation
// occurred. The same location can be passed more than once; the aliases refer to one storage
// identity and are not independent copies.

func tryRead(flag: bool, out value: int): bool {
    if flag {
        value = 1
        return true
    }
    value = 0
    false
}

func outAllNormalPaths(): int {
    var value: int
    tryRead(true, out value)
    value
}

func outMissingNormalPath(flag: bool, out value: int): bool {
    if flag {
        value = 1
        return true
    }
    // Negative: the false normal exit does not assign value.
    false
}

func refAlias(ref left: int, ref right: int): () {
    left = 1
    right = 2
}

// FLOW DIAGNOSTIC AND RESOURCE CONTRACT
//
// A flow diagnostic names the source use or transfer that cannot be proved and carries the
// original UTF-8 source span. Diagnostics should distinguish an unassigned read, a stale
// nullable/type refinement, a missing return, an out parameter missing on a normal exit, an
// unreachable handler, and a forbidden cleanup transfer. Multiple failures are reported in
// deterministic source order subject to the configured cap. An incomplete implementation must
// retain an explicit deferred proof state and must not turn absence of an error into successful
// verification.
//
// Source flow analysis is not FIR verification. It does not establish that every alias,
// exception edge, iterator protocol, native transition, cleanup action, borrow escape, GC root,
// or emitted operation is represented. LLVM lowering may consume only a separately verified FIR.

// CORRELATED BRANCHES AND FACT PRECISION
//
// A path-sensitive analysis may keep several states separate while a condition or cleanup
// region is unresolved. When it joins those states, it may preserve a fact only if the fact
// holds for every reachable incoming edge. For example, after
//
//     if flag {
//         if left == null { return 0 }
//         if right == null { return 0 }
//     } else {
//         if left == null { return 0 }
//         if right == null { return 0 }
//     }
//
// both local storage identities are non-null on every continuation. After
//
//     if flag {
//         if left == null { return 0 }
//     } else {
//         if right == null { return 0 }
//     }
//
// neither conclusion follows for both locals. The analysis cannot union one arm's left fact
// with the other arm's right fact and report both as established.
//
// Boolean algebra is not a license for unsound relational inference. An expression of the form
// a != null OR b != null proves that at least one value is non-null, not which one. Its false
// result proves both are null only if both operands are pure stable reads and the expression
// completed normally. Calls, volatile/native reads, overloaded Boolean operands, cancellation,
// and exceptional exits may invalidate such reasoning. The supported condition evaluator must
// record whether a fact came from a stable local test or from an operation whose effects are
// not known.
//
// A proof representation records its source predicate, local identity, narrowed type, branch
// polarity, and invalidating writes. It must not persist source text as the identity of a
// predicate. Repeated syntax at a different program point may have different inputs and a
// different outcome.

// EXCEPTION-EDGE SNAPSHOTS
//
// A throwing operation creates an exceptional successor at the exact point the operation
// occurs. Assignments completed before it belong to the exceptional state; assignments after
// it do not. For example, after a call to read followed by result = 1, a catch state cannot
// observe result as assigned if read threw. After result = 1 followed by read, a catch of
// read's effect may observe that earlier assignment. A call that writes an out/ref location
// and then throws cannot be modeled as an atomic successful write unless its contract specifies
// the exceptional state.
//
// Each catch handles the protected body's exception edges that match its type. A catch body's
// own exception edge leaves the handler and reaches an outer handler or the enclosing budget;
// sibling catches do not catch one another. A finally action is composed with every normal and
// exceptional edge of its try. This ordering applies to return, throw, break, continue, and
// ordinary completion.
//
// A catch/finally analysis must retain enough path identity to know whether an out assignment
// occurred before a throw, whether a cleanup action was registered, and whether a primary
// exception already exists. Merging all try states before the handlers run loses this
// information and can incorrectly accept an unassigned read or erase an effect.

// LOCAL STORAGE, HEAP STATE, AND INVALIDATION
//
// Flow facts belong to resolved storage identities. A mutable local is a slot, not a textual
// name: shadowed declarations have different identities, and assigning one slot does not assign
// another slot that previously held the same reference. Immutable locals preserve their value
// after initialization. Rebinding a mutable local invalidates facts about that local according
// to the value written; it does not mutate the object previously referenced by the slot.
//
// A field read is not a local snapshot. If `val first = owner.child` is a reference value, the
// value in `first` can remain non-null while `owner.child` changes through an alias. A repeated
// property read can call a getter, invoke a delegate, read volatile/native storage, or throw.
// The compiler must not transfer a refinement from one property evaluation to a later one
// unless the storage model explicitly proves the read stable. The same rule applies to indexers
// and overloaded operators: source spelling does not establish purity or identity.
//
// A local alias of an array or object does not copy its mutable contents. Refining the local
// reference proves a property of the reference value, not of all future field reads. Array
// element initialization is tracked only where the language's array model and the exact index
// prove a location; a dynamic index cannot stand for every element. Two index expressions that
// look equal in source are not assumed equal when evaluating either expression can mutate state
// or throw. Interior pointers and ref aliases retain owner and offset identity in the flow model.
//
// Assignment evaluates a target address before its right-hand value, then performs the write.
// The address and value expressions each execute once. For `target += value`, the selected
// writable location is evaluated once, read once, passed with the right operand to the selected
// operator, and written once after the operator returns normally. If address evaluation, the
// getter, the right operand, or the operator throws, later steps do not occur. Side effects from
// earlier completed steps remain visible on the exceptional edge. This ordering is part of both
// flow transfer and checked-effect analysis.
//
// Short-circuit Boolean evaluation creates distinct edges. For `left && right`, the right side
// is evaluated only after a true left result; for `left || right`, only after false. A local
// fact established by the left expression may be used on the edge that evaluates the right
// expression, subject to writes and calls within that evaluation. The result join contains only
// facts valid on every path producing that result. A custom Boolean operator, overloaded equality,
// or effectful getter cannot be modeled as a primitive stable predicate merely because its source
// token is `&&`, `==`, or a property name.

// TRANSFER SUMMARY AND CONSERVATIVE INVALIDATION
//
// A callable summary may describe definite writes, possible writes, returned values, checked
// effects, and storage identities that may be invalidated. The summary applies only to the
// selected declaration after overload resolution, generic substitution, virtual dispatch, and
// receiver binding. A summary from a rejected overload is discarded. A call with incomplete
// body/effect information is not pure by default: facts dependent on mutable aliases remain
// unproven after the call.
//
// A call does not automatically erase every local fact. A private, non-escaping local value
// whose storage cannot be reached by the selected call may retain its refinement. In contrast,
// a mutable capture passed as a callback, a ref argument, an overridable property access, or an
// unknown native callback can invalidate reachable facts. The caller may preserve unrelated
// local facts when alias analysis proves the target cannot write them. A resource cap or unknown
// dispatch target produces a deferred proof rather than a guessed purity result.
//
// The flow state at a call's exceptional successor is a prefix of the evaluation: receiver and
// explicit arguments that completed may have effects; later arguments and defaults did not run
// if an earlier evaluation threw. The callee may have partially changed state before throwing.
// Definite assignment after an exceptional call is therefore not inferred from its normal
// return contract. A richer interprocedural summary may describe writes on exceptional exits;
// absent such a verified summary, the caller retains only facts independent of potentially
// modified storage.

// TERMINATION, LOOP FIXPOINTS, AND SWITCH EXITS
//
// The analyzer represents normal completion, return, throw, break, and continue as separate
// transfer kinds. `!` is the bottom type: an expression of that type has no normal result edge.
// It does not mean an uninitialized value or an unknown type. A call returning ! cannot make a
// following statement reachable. Diagnostics for unreachable source remain distinct from
// missing-return and definite-assignment diagnostics.
//
// A loop header state is the join of the initial entry and reachable backedges. The body is
// revisited until the state stabilizes or the configured analysis budget is reached. A `continue`
// edge for a counted loop runs its update expression before reaching the header; a `break` edge
// bypasses that update and leaves the loop. In a collection loop, the zero-item path always
// exists unless a separate proof establishes non-emptiness. A loop assignment is definite after
// the loop only if it holds on every normal exit, including the condition-false and break edges.
//
// A switch evaluates its scrutinee once. Each case receives a state refined by its matched
// label; a case that cannot match has no body edge. The cases do not implicitly fall through.
// The normal successor joins case completion, explicit break exits, and the unmatched input
// path when the value domain is not exhaustive. Return/throw cases do not contribute a normal
// state. Duplicate labels and impossible defaults are diagnostics, not extra paths that may be
// used to satisfy definite assignment.
//
// Nested loops maintain distinct break/continue targets. A transfer applies to the nearest
// enclosing loop target permitted by the grammar; it does not exit an enclosing function or
// switch by textual proximity. A transfer forbidden in a lambda, finally, or defer is rejected
// at its source site. If malformed or incomplete control flow prevents target resolution, the
// transfer graph remains unverified and cannot be lowered as a successful body.

// REFINEMENT PROVENANCE AND DIAGNOSTIC PRECISION
//
// A nullable refinement is a predicate about a particular value at a particular point. It is
// invalidated by reassignment to the tracked slot, by a reachable ref/out write, by an invoked
// writer closure, and by any call or alias operation whose verified summary can change the
// tracked storage. A call that merely receives a distinct immutable copy does not invalidate
// the original slot. Capturing a mutable slot shares its cell; capture creation and closure
// invocation are separate events, and escape analysis determines whether invocation is possible
// before a later read.
//
// Narrowing through a type test or successful cast records the tested type and source value
// identity. A failing cast edge does not inherit the success fact. A smart cast may use the
// narrowed static type only while the source value is stable; a mutable property, open member,
// captured variable, or ref alias may require a fresh check. Numeric range facts are separate
// from nominal type facts and are preserved only for operations whose overflow/conversion
// semantics have been proven.
//
// Diagnostics should point to the first read that needs a missing fact, the branch join that
// loses a proof when no single read is responsible, or the transfer that violates a cleanup
// boundary. A fix suggestion may introduce an explicit null check or immutable snapshot, but it
// must not silently insert side-effecting reads, change exception behavior, or reorder argument
// evaluation. Diagnostic recovery preserves parser ownership and does not produce verified FIR.
