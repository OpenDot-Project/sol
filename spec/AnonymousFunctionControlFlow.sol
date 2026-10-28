// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: bounded source flow and checked-effect analysis are implemented; this file records the
// semantic contract independently from native lowering eligibility.
module source.anonymous_function_control_flow

// Body entry and expression completion
// ====================================
//
// An anonymous function has an independent body-analysis context. Its parameter symbols, result
// type, checked-effect budget, normal exits, exception edges, definite-assignment facts, and
// return owner do not become part of the surrounding callable merely because the function value
// is created there. The function body executes only when the value is invoked.
//
// In a block body, the last reachable expression statement is the implicit result expression.
// A declaration, assignment, loop, empty statement, or an unreachable statement is not a value-
// producing tail. The empty tuple `()` is the no-value result type. There is no `Unit` built-in or
// implicit conversion from a missing result to a non-empty tuple. A non-unit result requires a
// compatible value on every reachable normal completion path.
//
// An expression body consists of one expression and has the type of that expression. `return`
// statements remain explicit control transfers and participate in the same result contract. A
// result type can be written after the parameter list or supplied by the contextual function type.
// If neither is available, the body analyzer joins the types of reachable explicit return values
// and the normal tail. It must find one unique common type under Sol's ordinary conversion rules.
// A missing value candidate produces `()` only when the body actually completes normally without
// a value; unresolved or resource-limited analysis is not manufactured into an empty tuple.

func expressionResult(): int = 42

func blockResult(flag: bool): int {
    val callback = func(condition: bool): int {
        if condition { return 10 }
        20
    }
    callback(flag)
}

func emptyTupleResult(): () {
    val callback: () -> () = func() { () }
    callback()
}

func emptyBodyResult(): () {
    val callback: () -> () = func() {}
    callback()
}

// Reachability and result joins
// =============================
//
// Result inference considers only reachable exits from the current anonymous-function body.
// An explicit return inside a nested lambda, local function, or nested anonymous function belongs
// to that nested callable and contributes no result candidate to its parent. The nested callable
// is analyzed independently under its own parameters and effects. A return from the parent does
// not complete or assign an `out` parameter belonging only to a child closure.
//
// A bottom-typed expression (`!`) has no normal continuation. A throw-only body therefore has no
// normal result candidate and can infer `!`; it is not treated as a fallthrough `()` body. A
// branch that returns or throws contributes no tail value after that transfer. A reachable branch
// that falls through must provide a valid tail if the result is non-unit. The inferred type is the
// unique most-specific common type supported by the type relation, not an arbitrary first result.

open class Failure: Exception {}
open class SpecificFailure: Failure {}

func neverReturns(): ! throws Failure { throw Failure() }

func bottomResult(): ! throws Failure {
    val callback = func(): ! throws Failure { neverReturns() }
    callback()
}

func nestedReturnIsNotAParentResult(flag: bool): int {
    val callback = func(): () { return }
    if flag { return 1 }
    callback()
    2
}

func joinReturns(flag: bool): Failure {
    val callback = func(flag: bool) {
        if flag { return SpecificFailure() }
        return Failure()
    }
}

func incompatibleReturnPaths(flag: bool) {
    val callback = func(flag: bool) {
        if flag { return 1 }
        return "text"
        // expect: source error; the current callable has no unique result type.
    }
}

func reachableFallthrough(flag: bool) {
    val callback = func(flag: bool): int {
        if flag { return 1 }
        // expect: source error; this normal path reaches the end without an int.
    }
}

// Return ownership and labels
// ===========================
//
// An unlabeled `return` targets the nearest enclosing callable. Entering an anonymous function
// establishes a new return owner, even if the anonymous body is nested lexically inside another
// function. A child return cannot jump through that owner. Labeled returns resolve to a lexical
// callable label that is visible at the source location; spelling alone is never used to guess a
// target after name binding. An unresolved, non-callable, or non-enclosing label is a source error.
//
// A return with a value contributes that value to the target callable's result contract. A
// valueless return contributes `()`. A labeled return to an enclosing callable is not permitted
// to bypass the function-value boundary. A local function declared inside an anonymous function
// has yet another nearest-callable owner; its returns remain local to that function.

func returnBoundary(flag: bool): int {
    val callback: () -> int = func(): int {
        if flag { return 1 }
        2
    }
    callback()
}

func nestedReturnBoundary(flag: bool): () {
    val outer = func(): int {
        val inner = func(): int {
            if flag { return 3 }
            4
        }
        inner()
    }
    outer()
}

func localFunctionReturnBoundary(flag: bool): int {
    func local(): int {
        if flag { return 5 }
        6
    }
    local()
}

// Definite assignment and out parameters
// ======================================
//
// Every anonymous-function `out` parameter must be assigned on every reachable normal exit. The
// check includes explicit returns and implicit block-tail completion. A path that throws has no
// normal exit and does not create a caller-visible normal assignment. Assignments in a nested
// callable do not assign the parent callable's `out` storage. The parameter's lexical symbol
// identity is used for the check, not its name.
//
// Assignments in both sides of an if join by intersection. Early returns contribute their own
// exit state. A finally block runs on transfers that leave its try region and can assign storage
// on those exits; a return or throw inside finally may replace the pending transfer according to
// the language's ordinary finally rules. A closure created in the try body does not run merely
// because control reaches the end of the try block.

func assignedOut(flag: bool, out result: int): () {
    val callback: (bool, out int) -> () = func(flag: bool, out value: int) {
        if flag { value = 1; return }
        value = 2
    }
    callback(flag, out result)
}

func failedOutPath(flag: bool, out result: int): () {
    val callback = func(flag: bool, out value: int) {
        if flag { value = 1; return }
        return
        // expect: source error; one normal return does not assign value.
    }
}

func throwOnlyOut(out result: int) throws Failure {
    val callback: (out int) -> () throws Failure = func(out value: int) throws Failure {
        throw Failure()
    }
    callback(out result)
}

func finallyAssignsOut(out result: int): () {
    val callback: (out int) -> () = func(out value: int) {
        try { return }
        finally { value = 9 }
    }
    callback(out result)
}

func childDoesNotAssignParentOut(out result: int): () {
    val child = func() { result = 9 }
    // expect: a child's body is deferred and does not satisfy the enclosing function's out rule.
}

// Checked effects are attached to execution
// =========================================
//
// The effect set of an anonymous function is not executed when the value is created, copied, or
// passed as a value. It is charged to the enclosing callable when the function value is invoked.
// The function type carries the effect contract. An explicit `throws` clause must cover every
// checked effect produced by the body. With no written clause, an expected function type supplies
// the effect budget; otherwise the body analysis infers the effect set from executed operations.
//
// A surrounding try/catch handles effects raised by a call in its protected body. It does not
// consume effects from a deferred function body's definition. A checked effect caught inside the
// anonymous body is removed from that body's escaping set. An unchecked RuntimeException subtype
// does not become a checked effect. The target expression and actual arguments are evaluated
// before invocation and contribute their own effects at their evaluation sites.

open class IOException: Exception {}

func read(): int throws IOException { return 1 }

func deferredEffect(): () -> int throws IOException {
    val callback = func(): int throws IOException { read() }
    callback
}

func caughtInvocation(): int {
    val callback = deferredEffect()
    try { callback() }
    catch (error: IOException) { 0 }
}

func uncaughtInvocation(callback: () -> int throws IOException): int throws IOException {
    callback()
}

func catchesInsideBody(): () -> int {
    val callback = func(): int {
        try { read() }
        catch (error: IOException) { 0 }
    }
    callback
}

// An anonymous function's inferred checked effects are independent from the enclosing try that
// encloses its definition. A catch around the definition cannot handle a future invocation. A
// catch inside the function body can handle the operation before the function type is constructed.

// Cleanup, calls, and state invalidation
// =====================================
//
// A nested function body is not an executed call at its declaration site. A call or throwing
// operation in an uninvoked nested callable must not create an exception edge for the enclosing
// try statement. Likewise, declaring a closure that mentions a mutable local does not execute a
// call through that local and does not invalidate the enclosing body's cleanup smart-cast facts.
// Invoking the closure or passing it to an unverified callback remains conservative because the
// callee may mutate captured state.
//
// Cleanup analysis applies the ordinary callable rule: a cleanup's effectful or call-containing
// source is relevant only when the cleanup runs. A nested function declaration inside cleanup is
// not itself an invocation. If the cleanup invokes the nested value, call invalidation applies at
// that invocation site. This distinction prevents both unsound retained facts and unnecessary
// loss of facts when a callable is merely declared.

open class RuntimeException: Exception {}

func nestedThrowDoesNotEscapeTry() {
    try {
        val callback = func() { throw RuntimeException() }
    }
    catch (error: RuntimeException) {}
}

func nestedCallDoesNotInvalidateCleanup(value: String?) {
    if (value != null) {
        defer {
            val callback = func() { mutateGlobal() }
            value.length
        }
        value.length
    }
}

var globalCount = 0
func mutateGlobal() { globalCount += 1 }

func invocationCanInvalidateCapturedFacts(value: String?) {
    if (value != null) {
        val callback = func() { mutateGlobal() }
        callback()
        value.length
    }
}

// Deterministic resource bounds
// ============================
//
// Result inference walks only the current anonymous-function body and has deterministic node and
// result-path budgets. The current implementation permits at most 65,536 visited body nodes and
// at most 4,096 result candidates. Crossing either budget reports SOL2290 and returns an
// incomplete analysis. It must not silently drop later branches, pretend the last visited result
// is unique, produce `()`, or recurse until process stack exhaustion.
//
// Cancellation is checked while scanning expressions, statements, nested control-flow branches,
// and result paths. A cancelled query propagates cancellation rather than returning a plausible
// partial type. A nested callable has its own body budget; scanning its subtree is not charged to
// the parent's result-candidate set.
//
// The syntax parser and AST verifier independently impose structural depth/size limits. A body
// analysis budget does not replace those earlier limits. Each phase must report its own resource
// failure at the source node responsible for the incomplete proof.

func sourceBudgetsRemainVisible(flag: bool): int {
    val callback = func(): int {
        if flag { return 1 }
        2
    }
    callback()
}

// Normal-flow summary contract
// ============================
//
// `AnonymousFunctionBodySummary` belongs to the exact source analysis and exact AST function node.
// It reports a resolved function type, the number of abstract reachable normal exits, whether a
// normal fallthrough state exists, per-out-parameter assignment on every normal exit, and whether
// this subtree is complete. The count describes abstract flow states, not runtime executions or
// path multiplicity after arbitrary loop iteration.
//
// A summary is absent when the function's written/contextual signature cannot be resolved or the
// body is missing. A present summary with `IsComplete == false` is not a verified function. The
// body result and the source diagnostics remain queryable for IDE tooling, but FIR must not treat
// an incomplete summary as successful input. Queries from a different snapshot are rejected.

func summaryContract(out result: int): () {
    val callback = func(out value: int) { value = 42 }
    callback(out result)
}

object Main {
    func main(args: []String) {
        val callback: (int) -> int = func(value: int) { value + 1 }
        println(callback(41))
    }
}
