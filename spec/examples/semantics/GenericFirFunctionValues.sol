// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative examples; closed generic function references, indirect FIR, and exact-ABI
// noncapturing lambdas are supported in a bounded subset.
module examples.semantics.generic_fir_function_values

// Generic direct calls, function references, lambdas, and closures
// ========================================================
//
// These examples separate source analysis from current native admission. `Bound` records a source
// result only. `Deferred` means the source rule or implementation is incomplete. `FIR: Rejected`
// records a representation boundary; it does not mean overload resolution should select a different
// function. The bounded native lambda path admits only noncapturing, exact-signature forms in a
// closed non-generic enclosing function. No line in this file claims to have run as a test.

func <T> identity(value: T): T = value

func inferredText(value: String) = value

func <T> applyOne(value: T, transform: (T) -> T): T = transform(value)

func <T> applyTwo(value: T, first: (T) -> T, second: (T) -> T): T = second(first(value))

func <T, E: Throwable> applyEffectful(value: T, transform: (T) -> T throws E): T throws E =
    transform(value)

func <T> choose(value: T): T = value

func choose(value: String): String = value

// 1. Direct calls close type arguments before FIR instance creation.
func directStringCall(): String = identity<String>("sol")
// expect: Source: Bound; target=identity<String>; direct-call specialization; FIR: Ready only for admitted body/ABI.

func inferredIntCall(): int = identity(10)
// expect: Source: Bound; T=int from argument; FIR: Ready only for admitted body/ABI.

func inferredFromResult(): String = identity("result")
// expect: Source: Bound; T=String from argument and expected result agree; direct call only.

func directGenericFunctionBody(): String = applyOne("text", { value -> value })
// expect: Candidate-local lambda replay may bind T=String. The lambda may lower as a synthetic
// function-pointer target when the closed contextual signature is exact; the specialized generic
// callee's indirect-call body still requires its independent FIR checks.

func genericCallFromLambda(): int {
    val callback: (int) -> int = { value -> identity(value) }
    callback(41)
}
// expect: The lambda body source binding closes identity<int>. FIR records the selected direct call
// and makes identity<int> reachable from the synthetic lambda function.

func genericReferenceFromLambda(): int {
    val provider: () -> ((int) -> int) = { -> identity }
    val callback = provider()
    callback(41)
}
// expect: Expected return context closes the generic function value as identity<int>; FIR verifies
// the reference edge and keeps the raw code-pointer ABI.

// 2. A generic function name closes from an expected function type.
func genericNameAsValue(): () {
    val callback: (String) -> String = identity
}
// expect: Source: Bound; the expected function type closes the target as identity<String>; no explicit reference suffix is used.

func inferredResultAsValue(): () {
    val callback: (String) -> String = inferredText
}
// expect: Source: Bound; the ordinary expression-body result is String; no body execution occurs.

// 2a. Stateless top-level object methods share the receiver-free function-value ABI.
object IdentityOperations {
    func <T> identity(value: T): T = value
    func describe(value: String): String = value
}

func singletonMethodAsValue(): () {
    val callback: (String) -> String = IdentityOperations.identity
}
// expect: Source: Bound; expected type closes identity<String>; FIR: Ready for the exact supported signature.

func singletonMethodCall(): String = IdentityOperations.describe("sol")
// expect: FIR: Ready only when the complete top-level object is stateless and the method uses no receiver.

object StatefulOperations {
    val prefix = "sol"
    func <T> identity(value: T): T = value
}

func statefulSingletonMethodAsValue(): () {
    val callback: (String) -> String = StatefulOperations.identity
}
// expect: Source: Deferred; FIR: Rejected(SOL2400); no receiver or singleton storage is fabricated.

func genericValueCannotUseDummyCall(): () {
    val callback: (int) -> int = identity(0)
}
// expect: Source: Rejected; expression is int, not a function value; no dummy invocation convention.

func genericValueMustNotGuessType(): () {
    val callback = identity
}
// expect: Deferred; no expected function type closes T and no polymorphic runtime value is synthesized.

func genericReferenceNeedsAllParameters(): () {
    val callback: (String) -> String = choose
}
// expect: Source: Bound or Ambiguous according to generic inference and the final nongeneric tie preference; declaration order never chooses.

// 3. A non-generic function reference is still a value conversion.
func fixed(value: String): String = value

func fixedReference(): () {
    val callback: (String) -> String = fixed
}
// expect: Source: Bound; FIR: Ready for an exact supported signature and source body.

func fixedWrongParameterReference(): () {
    val callback: (int) -> String = fixed
}
// expect: Source: Rejected; parameter mismatch is not repaired by an LLVM function-pointer cast.

func fixedWrongResultReference(): () {
    val callback: (String) -> int = fixed
}
// expect: Source: Rejected; result mismatch is checked before native emission.

// 4. Overload value selection uses the expected function type without calling a candidate.
func selectGenericOverload(): () {
    val callback: (String) -> String = choose
}
// expect: Generic and nongeneric candidates close to the same signature; the nongeneric final tie preference selects choose(String).

func chooseAmbiguous(value: String): String = value
func chooseAmbiguous(value: std.core.Object): String = "object"

func ambiguousOverloadValue(): () {
    val callback: (String) -> String = chooseAmbiguous
}
// expect: Ambiguous if neither candidate has a unique best conversion; no candidate body executes.

func overloadValueNoFallback(): () {
    val callback: (String) -> String = choose
}
// expect: A strictly better generic candidate remains selected; if FIR cannot represent that exact winner, it rejects it instead of retrying another overload.

// 5. Expected function types contextually type lambda parameters.
func namedLambdaExpectedType(): () {
    val callback: (int, int) -> int = { left, right -> left + right }
}
// expect: Source: Bound; FIR: Ready for this noncapturing, exact-signature form in a closed,
// non-generic enclosing function, provided the body uses supported operations.

func positionalLambdaExpectedType(): () {
    val callback: (int, int) -> int = { $0 + $1 }
}
// expect: `$0` and `$1` bind to this lambda's expected arity, not an implicit `it`. The current
// bounded FIR path admits positional lambdas only with value parameters and an exact native ABI.

func headerlessLambdaExpectedType(): () {
    val callback: () -> int = { 42 }
}
// expect: A headerless lambda has zero parameters. This exact-signature case is represented by a
// synthetic source-linked FIR function; it has no allocated closure environment.

func noImplicitItLambda(): () {
    val callback: (int) -> int = { it }
}
// expect: Source: Rejected; there is no implicit it variable.

func explicitLambdaHeaderMismatch(): () {
    val callback: (String) -> String = { number: int -> number.toString() }
}
// expect: Source: Rejected; a written header must agree with expected parameter type.

func contextualLambdaFromGenericArgument(): String = applyOne("value") { input -> input }
// expect: Source inference may close T=String from the value; the callback body gets that context only after candidate replay.

func contextualLambdaBodyConflict(): String = applyOne("value") { input -> input.length }
// expect: Candidate is rejected unless result inference and expected context agree; a losing lambda candidate leaks no facts.

// 6. Nested lambdas own separate parameter namespaces and captures.
func nestedLambdaValue(): () {
    val combine: (int) -> (int) -> int = { outer -> { inner -> outer + inner } }
}
// expect: Inner lambda's parameter scope is independent; outer is an explicit capture.

func nestedPositionalLambdas(): () {
    val combine: (int) -> (int) -> int = { $0 + 1 }
}
// expect: Outer lambda has one parameter; an inner function value is not synthesized by the body.

func nestedCapturesMutable(): () {
    var total = 0
    val increment: () -> int = { total = total + 1; total }
    val read: () -> int = { total }
}
// expect: Both closures share one mutable cell; independent snapshots are incorrect.

func immutableCapture(): () {
    val label = "sol"
    val read: () -> String = { label }
}
// expect: Capture may copy immutable value if representation/lifetime permits; still requires environment accounting.

// 7. A generic callback parameter is not a generic function value.
func <T> genericCallbackParameter(value: T, callback: (T) -> T): T = callback(value)

func genericCallbackUse(): int = genericCallbackParameter(2) { item -> item + 1 }
// expect: Source binding may select T=int and contextually type the lambda. Its exact noncapturing
// function pointer and the specialized callee's indirect invocation are FIR-ready when both pass
// their independent source/type/effect checks.

func <T> callbackStoredForLater(callback: () -> T): () -> T = callback
// expect: Returning the callback extends its lifetime; no generic closure ABI is implied by substitution.

func <T> callbackReturnedByLambda(seed: T): () -> T = { seed }
// expect: Source may type result as () -> T; native FIR rejects closure conversion and captured T storage.

// 8. A function-value call has an invocation edge and effect behavior.
func <E: Throwable> indirectEffect(callback: () -> () throws E): () throws E {
    callback()
}
// expect: Source effect is E; FIR retains the specialized indirect-call effect set. Native exception dispatch remains a separate backend requirement.

func callbackEffectBudgetTooSmall(callback: () -> () throws IOException): () {
    callback()
}
// expect: Source: Rejected; callback's checked IOException cannot be dropped from caller budget.

func callbackUncheckedBudget(callback: () -> () throws RuntimeException): () {
    callback()
}
// expect: Unchecked classification follows RuntimeException; the function value itself is not treated as a throwing creation step.

func callbackCreationDoesNotInvoke(): () {
    val callback: () -> int = { throw IOException() }
}
// expect: Callback body effect is checked when type-checked; closure creation does not execute its body.

// 9. Ref/out function types preserve exact writable locations.
func refOutLambda(): () {
    val exchange: (ref int, out int) -> () = { ref source: int, out destination: int ->
        destination = source
    }
}
// expect: Source preserves exact ref/out modes. FIR: Ready for this closed, noncapturing lambda
// because its signature is exact and the out parameter is assigned on its normal return path.

func <T> invokeRefOut(callback: (ref T, out T) -> (), ref source: T, out destination: T): () {
    callback(ref source, out destination)
}
// expect: Source obligations include exact T storage and out assignment; native indirect call is rejected.

func refLocationCannotBeCaptured(): () {
    var value = 1
    val callback: () -> int = { value }
    val use: (ref int) -> int = { ref location: int -> callback() }
}
// expect: Capturing a ref/out location or borrowed address is rejected absent a non-escape proof.

func refOutDoesNotUseTemporary(): () {
    var value: short = 1
    val callback: (ref int) -> () = { ref location: int -> () }
    callback(ref value)
}
// expect: Source: Rejected; short-to-int conversion cannot create ref storage.

// 10. Defaults and varargs do not become properties of a function type accidentally.
func withDefault(value: int = 4): int = value

func defaultFunctionValue(): () {
    val callback: (int) -> int = withDefault
}
// expect: Bound; the reference has type (int) -> int. An indirect call must supply the int argument;
// the default expression is only used by a direct call to withDefault.

func withVararg(vararg values: int): int = values.length

func varargFunctionValue(): () {
    val callback: ([]int) -> int = withVararg
}
// expect: Bound; vararg is one array-valued function parameter. An indirect call passes one []int;
// it does not expand elements.

func defaultsAtCreationAreWrong(): () {
    val callback: (int) -> int = withDefault
}
// expect: Creating the function value does not evaluate the default; only a direct omitted-argument
// call evaluates it.

// 11. Generic owner identity survives nested callback types.
func <T> nestedSignature(value: T, callback: (T) -> (T) -> T): T = value

func nestedSignatureValue(): () {
    val callback: (String) -> (String) -> String = { first -> { second -> second } }
}
// expect: Each nested function result is a distinct function type layer; no flattening to one callback.

func <T, U> twoOwners(first: T, callback: (U) -> U): (T, (U) -> U) = (first, callback)
// expect: T and U are distinct owner+ordinal identities even if their written names are reused elsewhere.

func sameNameShadow(): () {
    val outer: (int) -> int = { T ->
        val inner: (String) -> String = { T -> T }
        T
    }
}
// expect: Lambda parameter spelling T does not alias a generic type parameter named T.

// 12. Nullable and bottom callback results remain explicit.
func <T> nullableCallback(callback: () -> T?): T? = callback()

func nullableCallbackUse(): String? = nullableCallback<String> { null }
// expect: Source nullable result remains String?; native admission rejects its current layout or
// flow obligations independently of lambda closure conversion.

func bottomCallback(callback: () -> !): ! = callback()
// expect: Verified FIR preserves the bottom result as a no-return indirect call.

func nullableFunctionValue(): () {
    val maybe: (() -> String)? = null
}
// expect: Nullable function container and nullable function result are distinct source shapes.

// 13. Captured managed references remain visible to Yellow Tea.
func escapingStringClosure(seed: String): () -> String = { seed }
// expect: Future closure environment must trace seed for as long as returned closure is reachable.

func escapingArrayClosure(seed: []String): () -> String = { seed[0] }
// expect: Future environment must root array; indexing must preserve bounds/exception semantics.

func twoClosuresShareArray(seed: []String): (() -> int, () -> String) =
    ({ seed.length }, { seed[0] })
// expect: Both environments may retain the same managed array; liveness is object identity, not source name.

func closureSurvivesCreatingFrame(): () -> String {
    val captured = String("captured")
    { captured }
}
// expect: Returning closure requires environment ownership after frame exit; not a stack-only raw pointer.

// 14. Ref-like and borrowed captures remain rejected even under generic constraints.
func <T> captureRefLike(value: T): () -> T where T: allows ref struct = { value }
// expect: A ref-like T cannot enter a heap closure environment or escape the call.

func spanCallback(span: Span<int>): () -> int = { span[0] }
// expect: Source borrow analysis must reject escaping capture of Span storage.

func nonEscapingSpanCallback(span: Span<int>, consume: ((() -> int)) -> ()): () {
    consume { span[0] }
}
// expect: Only a proven no-escape callback contract could admit this; this source form alone proves nothing.

// 15. A function pointer does not describe a closure environment.
func staticTarget(value: int): int = value + 1

func plainPointerCandidate(): () {
    val callback: (int) -> int = staticTarget
}
// expect: Source: Bound; FIR: Ready for this exact module-level function signature and supported
// body. The value references the existing declaration instead of a synthetic lambda target.

func capturedPointerCandidate(seed: int): () {
    val callback: (int) -> int = { value -> value + seed }
}
// expect: Code pointer plus environment pointer is required; a raw code pointer loses seed.

func codePointerDoesNotKeepEnvironmentAlive(): () {
    val owned = String("owned")
    val callback: () -> String = { owned }
}
// expect: Environment references need precise GC roots at allocation and call safepoints.

// 16. AOT and ORC share source-level function-value behavior.
func modeIndependentCallback(callback: () -> int): int = callback()
// expect: AOT and ORC must preserve one-time target evaluation, argument order, effects and capture identity.

func cancelDuringIndirectCall(callback: () -> int): int = callback()
// expect: A future backend specifies cancellation polling around calls; it cannot leave borrowed roots stale.

func callbackThrowsDuringUnwind(callback: () -> ()): () {
    try { callback() } finally { println("cleanup") }
}
// expect: Source EH/cleanup and function-value FIR are separate pending native gates.

// 17. Artifact boundaries do not export function values implicitly.
func <T> exportedGenericTarget(value: T): T = value

func libraryCallbackFactory(seed: int): () -> int = { seed }
// expect: A `.solib` export cannot expose this capturing closure until container ownership/version
// rules are specified.

func cAbiCallback(seed: int): () -> int = { seed }
// expect: A C ABI cannot export a managed closure as a function pointer without an explicit trampoline/lifetime contract.

func orcGuestCallback(): int = applyOne(1) { value -> value + 1 }
// expect: The exact noncapturing lambda pointer and its target are compiled into the ORC guest.
// A guest-process boundary still does not permit a host function pointer or captured host heap
// address to cross into that process.

// 18. Current and future outcomes remain distinguishable.
func noncapturingLambdaValue(): () {
    val callback: (int) -> int = { value -> value }
}
// expect: Source: Bound; FIR: Ready for this simple noncapturing lambda in a closed, non-generic
// enclosing function. Captures, generic parent bodies, bound receiver/member access, and ABI
// adapters remain rejected even when source lambda typing succeeds.

func rejectedCallbackBody(): int = applyOne(1) { value -> value + "x" }
// expect: Source candidate/body error is not a FIR verifier error and must not create an instance.

func resourceFailureIsNotCallableFailure(): int = applyOne(1) { value -> value }
// expect: If the specialization work budget is exhausted report SOL2403, not source overload failure.

func verifierFailureIsNotDeferredSource(): int = applyOne(1) { value -> value }
// expect: Corrupted constructed FIR reports verifier failure (SOL2402); it is not silently reclassified as Deferred.

// 19. Expected types do not execute generic candidates.
func candidateHasDefault(value: int = println("default")): int = value

func resolveValueWithoutRunningDefault(): () {
    val callback: (int) -> int = candidateHasDefault
}
// expect: Any future conversion resolves the declaration without evaluating its default; it runs only when a call omits value.

// 20. A generic function value does not share the direct-call cache key by accident.
func genericCallAndValueAreDistinct(): () {
    val direct = identity<String>("x")
    val callable: (String) -> String = identity
}
// expect: Direct and value references to identity<String> use the same closed function instance; this value has no environment or conversion adapter.

// 21. Captured mutable aliases preserve one source variable.
func sharedCaptureIdentity(): () {
    var count = 0
    val add: (int) -> int = { amount -> count = count + amount; count }
    val get: () -> int = { count }
}
// expect: `add` and `get` observe one shared cell; closure construction cannot clone `count` independently.

// 22. Source-order argument execution is preserved for indirect calls.
func sideEffectingArgument(): int = 1
func indirectOrder(target: (int, int) -> int): int = target(sideEffectingArgument(), sideEffectingArgument())
// expect: The target and each argument are evaluated once in specified source order; an adapter cannot reorder them.

// 23. Generic function type ownership composes through nested return types.
func <T> nestedFunctionResult(value: T): () -> T = { value }
func nestedFunctionResultUse(): () {
    val callback: () -> String = nestedFunctionResult("retained")
}
// expect: Source result is a closure that captures String; FIR rejects until both specialization and closure environment are verified.

// 24. Lambda result joins preserve bottom and nullable identities.
func branchLambda(): () {
    val callback: (bool) -> String? = { flag -> if flag { "present" } else { null } }
}
// expect: Source result is String?; native closure result layout remains unsupported.

func bottomBranchLambda(): () {
    val callback: (bool) -> String = { flag -> if flag { "present" } else { throw IOException() } }
}
// expect: Source join excludes bottom path; checked IOException still belongs to the callback effect set.

// 25. Captured objects remain reachable across a safepoint.
func collectInsideClosure(seed: String): () -> String {
    { Gc.collect(); seed }
}
// expect: Future lowering roots both environment and seed across collection; no raw stack pointer may survive the safepoint.

// 26. Publishing a closure requires the collector's store protocol.
class CallbackHolder(var callback: (() -> String)?)
func publishClosure(holder: CallbackHolder, value: String): () {
    holder.callback = { value }
}
// expect: Future heap publication uses the Yellow Tea write barrier and an environment descriptor.

// 27. A function value cannot be assumed thread-safe from its type alone.
func transferClosure(callback: () -> int): () {
    Thread.start { callback() }
}
// expect: Thread transfer needs capture mutability, synchronization, and environment ownership rules; source function type alone is insufficient.

// 28. Exception cleanup retains closure and local roots through unwinding.
func throwingClosureCleanup(callback: () -> () throws IOException): () throws IOException {
    try { callback() } finally { println("closed") }
}
// expect: Source flow must retain callback effects and cleanup order; current native EH/closure gates reject this combination.

// 29. ORC process isolation does not serialize a closure implicitly.
func guestClosureArgument(callback: () -> int): int = callback()
// expect: Sending this value to ORC guest requires a versioned callable protocol; host pointers and captured heap addresses are invalid.

// 30. Function values have no implied structural equality.
func compareCallbacks(left: () -> int, right: () -> int): bool = left == right
// expect: Source equality is Deferred unless the language defines code-plus-environment identity or another explicit relation.

// 31. Returning a callback defers body execution until a later call.
func makeDelayed(callback: () -> int): () -> int = callback
// expect: Wrapper returns the same callable identity without invoking it; source effects are not charged at creation.

// 32. A local function with captures needs the same environment proof as a lambda.
func localFunctionCapture(seed: int): () -> int {
    func read(): int = seed
    read
}
// expect: Local callable captures seed; its declaration form does not make it a direct module-level generic FIR function.

// 33. Candidate-local callback evidence is rolled back when its overload loses.
func lambdaCandidateRollback(): int = applyOne(1) { value -> value + 1 }
// expect: Temporary lambda types, scopes, effects, and diagnostics from losing candidates do not escape into the winner.

// 34. Storing callbacks in a generic container retains each closure environment.
func callbackArray(seed: String): [](() -> String) = [{ seed }]
// expect: Each stored closure remains a managed value; array element ABI and environment roots are separately required.

// 35. Conformance accounting
// --------------------------
// Each example above has independent source, FIR, ABI and runtime outcomes. A future executable
// fixture must record which layer ran, toolchain/LLVM version, target triple, and artifact mode. A
// parser-only check cannot establish overload selection or closure lifetime. A source binder result
// cannot establish native representation. An LLVM verifier pass cannot establish Yellow Tea root
// correctness. Do not mark these rows passed until each required stage has actual evidence.
