// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: throws/catch syntax and a bounded checked-effect subset are represented by source
// analysis. Complete effect inference, exceptional CFG proof, cleanup lowering, verified FIR,
// native Windows unwind, and runtime exception behavior remain incomplete.
module source.exceptions.rules

import std.stdio

class ConfigException(message: String, cause: Throwable? = null): Exception(message, cause)

func requireName(name: String): String throws ConfigException {
    if name.byteLength == 0 { throw ConfigException("Missing name") }
    name
}

func recover(name: String): String {
    val result = try {
        requireName(name)
    } catch (error: ConfigException) {
        "fallback"
    } finally {
        // Cleanup has no result and cannot transfer control out of this scope.
    }
    result
}

func <E: Throwable> raise(error: E): ! throws E { throw error }

// Throwable and its subtypes are checked, except RuntimeException and its subtree.
// The checked I/O chain is Throwable -> Exception -> std.stdio.IOException.
// Static thrown type determines the obligation; casting RuntimeException to Throwable
// does not hide that static checked obligation. Throw requires non-null Throwable.
// A checked effect must be caught or explicitly declared. Effect lists are not overload keys.
// Typed catch clauses run most-specific first; an unreachable later catch is an error.
// Override effect budgets may narrow, never widen. Callback types carry checked effects.
// Main may declare checked effects; its adapter is the final reporting boundary.
// Finally/defer run on normal and exceptional exit. A cleanup failure preserves the
// primary failure and records a suppressed failure; it does not silently replace it.

// EFFECT SETS AND STATIC TYPES
//
// The checked effect of a throw is classified from the static type of the thrown expression.
// A non-null Throwable subtype other than RuntimeException or a subtype of RuntimeException is
// checked. RuntimeException and its descendants may escape without declaration, but they still
// participate in catch matching and cleanup behavior. A value stored in a broad Exception or
// Throwable local has the broad static effect unless a type test smart-casts that local first.
// Runtime history does not narrow a declared effect on its own.
//
// A callable's throws clause is an upper budget for checked effects. Every checked effect
// escaping through a normal call, direct throw, property accessor, constructor, delegate
// operation, default expression, or cleanup must be covered by a declared type that is the
// same type or a supertype. Unchecked effects do not need budget entries. Declaring an unchecked
// type is permitted as API documentation but does not convert the subtree into a checked one.
// There is no implicit catch, wrap, conversion to String, error-code translation, or effect
// erasure at an ordinary call boundary.

class NetworkFailure: Exception {}
class TimeoutFailure: NetworkFailure {}
class ParseFailure: Exception {}
class BoundsFailure: RuntimeException {}

func openConnection(): int throws NetworkFailure { 1 }

func declaredSupertypeCoversSubtype(): int throws Exception {
    openConnection()
}

func uncoveredSubtypeDoesNotDisappear(): int {
    // Negative: NetworkFailure remains checked without a covering declaration.
    openConnection()
}

func declaredSiblingDoesNotCover(): int throws ParseFailure {
    // Negative: ParseFailure is not a supertype of NetworkFailure.
    openConnection()
}

func uncheckedRuntimeFailureNeedsNoBudget(): ! {
    throw BoundsFailure()
}

func broadStaticValueRemainsChecked(error: Throwable): ! throws Throwable {
    throw error
}

func narrowedRuntimeValueIsUnchecked(error: Throwable): ! {
    if error is RuntimeException {
        throw error
    }
    throw error
}

// DIRECT CALLS AND VIRTUAL CONTRACTS
//
// A selected call contributes its declared checked-effect set, not every effect ever observed
// in any overload. Effects are attached to the selected declaration after generic substitution.
// Failed or deferred overload candidates do not leak their effects into the caller. Ambiguous
// calls have no selected effect set and cannot be treated as effect-free.
//
// Override and interface implementation contracts may retain or narrow the inherited checked
// budget, but may not add an unrelated checked type. A type in the base budget can be replaced
// by one of its checked subtypes because the narrower contract accepts fewer failures. Throws
// clauses are not overload keys, and changing only a throws list never declares a second
// callable.

open class Reader {
    open func read(): String throws NetworkFailure {
        "base"
    }
}

class CachedReader: Reader {
    override func read(): String {
        "cached"
    }
}

class RemoteReader: Reader {
    override func read(): String throws TimeoutFailure {
        "remote"
    }
}

class InvalidReader: Reader {
    // Negative: ParseFailure is unrelated to Reader.read's declared budget.
    override func read(): String throws ParseFailure {
        "invalid"
    }
}

func dispatchThroughBase(reader: Reader): String throws NetworkFailure {
    // The statically selected base contract is checked even when runtime dispatch may choose
    // CachedReader, which narrows the budget.
    reader.read()
}

func duplicateThrowsAreNotOverloads(): () {
    // Negative declarations would conflict: throws is excluded from callable identity.
}

func sameName(value: int): int throws NetworkFailure { value }
// Negative: this declaration differs only in the effect budget and is not a distinct overload.
func sameName(value: int): int throws ParseFailure { value }

// CATCH MATCHING AND RETHROW
//
// Catch matching is subtype-based. Catch handlers are ordered from the most-specific type to
// broader supertypes. Once a preceding handler catches a type, a later handler whose complete
// type domain is covered is unreachable. Catching a supertype removes all effects contained by
// that supertype and no others. A handler's own effects are analyzed on the handler path and
// join the effects that escape the try expression. A catch parameter has the declared, narrowed
// exception type and is definitely assigned at handler entry.
//
// A catch declared as Throwable covers every thrown value, but its handler can still throw.
// The grammar has no untyped catch clause. A rethrow of the catch binding contributes the
// binding's static type; it does not automatically recover the exact dynamic exception type.
// A typed rethrow may be narrowed by an explicit is test.

func catchSpecificThenGeneral(): int {
    try {
        openConnection()
    } catch (timeout: TimeoutFailure) {
        1
    } catch (network: NetworkFailure) {
        2
    }
}

func catchesOnlyContainedEffects(): int throws ParseFailure {
    try {
        openConnection()
        parseDocument()
    } catch (network: NetworkFailure) {
        1
    }
}

func parseDocument(): int throws ParseFailure { 1 }

func unrelatedEffectSurvivesCatch(): int throws ParseFailure {
    try {
        openConnection()
        parseDocument()
    } catch (network: NetworkFailure) {
        1
    }
}

func broadCatchBeforeSpecific(): int {
    try {
        openConnection()
    } catch (network: NetworkFailure) {
        1
    } catch (timeout: TimeoutFailure) {
        // Negative: the earlier NetworkFailure handler includes TimeoutFailure.
        2
    }
}

func rethrowRetainsCatchType(): ! throws NetworkFailure {
    try {
        openConnection()
    } catch (network: NetworkFailure) {
        throw network
    }
}

func catchRuntimeDoesNotCatchChecked(): int throws NetworkFailure {
    try {
        openConnection()
    } catch (failure: RuntimeException) {
        0
    }
}

// EXPRESSION RESULTS AND NORMAL COMPLETION
//
// A try used as a value joins the normal result types from its body and reachable catch
// handlers. A throwing branch contributes no result type because ! has no normal continuation.
// If every branch is non-completing, the try expression has type !. Finally is not a result
// branch: its expressions are evaluated for side effects and effects, then the pending result
// or control transfer resumes. A finally body cannot return a value or use return, break, or
// continue to alter the pending transfer. If a cleanup call fails on normal completion, that
// failure becomes primary and cancels the pending return/value; during an existing unwind it is
// suppressed under the original primary. An explicit throw that escapes cleanup is rejected;
// a throw caught inside cleanup remains local. See CleanupConstruction.sol.

func recoveredResult(flag: bool): int {
    try {
        if flag {
            openConnection()
        }
        10
    } catch (failure: NetworkFailure) {
        20
    } finally {
        recordEffect(1)
    }
}

func allBranchesThrow(): ! throws NetworkFailure {
    try {
        throw NetworkFailure()
    } catch (failure: NetworkFailure) {
        throw failure
    }
}

func finallyCannotReplaceValue(): int {
    try {
        1
    } finally {
        // Negative: finally has no result and cannot override the value 1.
        2
    }
}

func finallyCannotHideFailure(): int throws NetworkFailure {
    try {
        throw NetworkFailure()
    } finally {
        recordEffect(2)
    }
}

func recordEffect(value: int): () {}

// FUNCTION TYPES, LAMBDAS, AND CALLBACKS
//
// The checked-effect set is part of a function type's identity and metadata. A callback
// contract with no checked effects accepts only lambdas/callables whose checked effect set is
// empty. A callback with a broad budget can accept a narrower effect set. A broad-effect
// function cannot be silently converted to a no-effect function. Lambda effects are checked
// when the lambda value is formed against its expected type; the body is not executed then.
// Calling a lambda later contributes its stored effect set at the invocation point.
//
// Function effect matching is set containment after type substitution. Type order in the
// written throws list is retained for source display but has no semantic ranking value.
// Duplicate equivalent effect entries are invalid. An open generic effect variable must have a
// Throwable constraint. If inference cannot establish a unique permitted effect set, overload
// resolution remains deferred or ambiguous instead of selecting a declaration by source order.

func pureCallback(action: () -> int): int {
    action()
}

func checkedCallback(action: () -> int throws NetworkFailure): int throws NetworkFailure {
    action()
}

func makeSafeCallback(): () -> int {
    { 42 }
}

func makeCheckedCallback(): () -> int throws NetworkFailure {
    { throw NetworkFailure() }
}

func callbackCannotDropEffect(): () {
    // Negative: a checked callback is not assignable to the pure function type.
    val action: () -> int = makeCheckedCallback()
}

func callbackCallSiteCarriesEffect(action: () -> int throws NetworkFailure): int {
    // Negative without a covering throws clause or local catch.
    action()
}

func callbackHandledAtInvocation(action: () -> int throws NetworkFailure): int {
    try {
        action()
    } catch (failure: NetworkFailure) {
        0
    }
}

func lambdaConstructionChecksBudget(): () {
    // Negative: this declared callback has no budget for the body below.
    val action: () -> int = { throw NetworkFailure() }
}

func lambdaBodyIsNotExecutedHere(): () -> int {
    // Positive: constructing this callback does not throw NetworkFailure now.
    { 42 }
}

// FUNCTION DEFAULTS AND ARGUMENT EVALUATION
//
// Explicit arguments evaluate once, from left to right in source order. Missing default
// expressions evaluate at the call, after explicit arguments, in declaration-parameter order.
// A default may refer only to earlier parameter values. The checked effect of every selected
// default expression is part of the call's effect set even if the call supplies other
// arguments. Supplying an explicit argument suppresses that parameter's default evaluation.
// A provided vararg array also suppresses its array default; omitted varargs use the declared
// array default or an empty array. Effect computation follows the actual mapping, not a union of
// every default on every candidate.

func readDefault(): int throws NetworkFailure { 0 }

func withEffectfulDefault(value: int = readDefault(), label: String = "default"): int {
    value
}

func effectfulDefaultMustBeCovered(): int throws NetworkFailure {
    withEffectfulDefault(label = "explicit")
}

func explicitArgumentSuppressesDefault(): int {
    // Positive: readDefault is not evaluated for value.
    withEffectfulDefault(value = 4)
}

func varargDefault(values: []int = readValues()): int {
    values.length
}

func readValues(): []int throws NetworkFailure { [] }

func varargDefaultEffectDependsOnMapping(): int throws NetworkFailure {
    varargDefault()
}

func explicitVarargSuppressesDefault(): int {
    varargDefault(values = [])
}

func effectfulDefaultCannotBeIgnored(): int {
    // Negative: every omitted checked default contributes to this call.
    withEffectfulDefault()
}

// PROPERTY ACCESS, DELEGATION, AND INITIALIZATION
//
// A custom getter and setter are independently effect-bearing callables. A read contributes
// only the getter effect. A plain assignment contributes the setter effect. A compound
// assignment first reads the current value, evaluates the right operand, resolves the operator,
// then writes the result; it accumulates getter, operator, and setter effects in evaluation
// order. The receiver and any index expressions are evaluated once. Access control is checked
// for the particular accessor that is actually used.
//
// A delegated read uses getValue effects, a delegated write uses setValue effects, and
// provideDelegate effects belong to initialization. The descriptor value represents declaration
// metadata and is not the owner's current property value. A selected delegation contract is
// stored once; failed candidate or property access paths cannot contribute phantom effects.

class RemoteBox {
    var content: int {
        get throws NetworkFailure = fetchContent()
        set throws ParseFailure { storeContent(value) }
    }
}

func fetchContent(): int throws NetworkFailure { 0 }
func storeContent(value: int): () throws ParseFailure {}
func combine(left, right: int): int throws TimeoutFailure = left + right

func getterBudget(box: RemoteBox): int throws NetworkFailure {
    box.content
}

func setterBudget(box: RemoteBox): () throws ParseFailure {
    box.content = 1
}

func compoundBudget(box: RemoteBox): () throws NetworkFailure, ParseFailure {
    box.content += 1
}

func insufficientGetterBudget(box: RemoteBox): int {
    // Negative: reading content invokes its checked getter.
    box.content
}

func insufficientSetterBudget(box: RemoteBox): () {
    // Negative: writing content invokes its checked setter.
    box.content = 2
}

func operatorEffectIsPartOfCompound(box: RemoteBox): () throws NetworkFailure, TimeoutFailure, ParseFailure {
    box.content += combine(1, 2)
}

// A class constructor may declare checked effects. Every source construction site must catch
// or declare that effect. Field initializers and initialization blocks are analyzed as part of
// the constructor contract; a partially initialized object is never published. Object and
// module initialization have explicit lazy-state contracts, and their checked effects are
// visible on first and subsequent accesses rather than disappearing after a cache hit.

class RemoteResource {
    val content: int
    constructor() throws NetworkFailure: super() {
        content = fetchContent()
    }
}

func constructRemote(): RemoteResource throws NetworkFailure {
    RemoteResource()
}

func constructWithoutBudget(): RemoteResource {
    // Negative: constructor effect has to be handled at this site.
    RemoteResource()
}

object RemoteSettings {
    val current: int = fetchContent()
}

func lazyObjectEffect(): int throws NetworkFailure {
    RemoteSettings.current
}

// DEFER AND FINALLY EFFECTS
//
// Cleanup actions run in LIFO order on normal exits and exception unwind. A cleanup action may
// have checked effects. Its effect is not discarded because another exception is already
// active. When the protected operation throws and cleanup also throws, the protected operation
// remains primary; cleanup failures are attached to the primary failure through the bounded
// suppressed-failure policy. If cleanup alone throws, its checked effect escapes normally.
// Cleanup cannot use return, break, or continue to alter the pending transfer.
//
// Registration is conditional when it occurs under a branch, and scope exit runs only actions
// whose registration paths are active. Loop-body cleanup is executed on each exit from that
// iteration. A transfer leaving a nested scope runs the nested cleanup stack before the parent
// stack. The selected order must not depend on hash iteration, partial-file order, or GC
// finalization.

func flush(): () throws NetworkFailure {}

func finallyEffectMustBeCovered(): () throws NetworkFailure {
    try {
        recordEffect(1)
    } finally {
        flush()
    }
}

func finallyEffectCannotBeDropped(): () {
    try {
        recordEffect(1)
    } finally {
        // Negative: flush contributes a checked effect.
        flush()
    }
}

func deferEffectMustBeCovered(): () throws NetworkFailure {
    defer { flush() }
    recordEffect(2)
}

func deferEffectCannotBeDropped(): () {
    defer { flush() }
}

func twoDefersRunInLifoOrder(): () {
    defer { recordEffect(1) }
    defer { recordEffect(2) }
    recordEffect(3)
}

func nestedCleanupOrder(): () {
    defer { recordEffect(1) }
    for {
        defer { recordEffect(2) }
        break
    }
}

func cleanupCannotTransferControl(flag: bool): () {
    for {
        defer {
            if flag {
                // Negative: cleanup cannot escape the enclosing loop.
                break
            }
        }
        break
    }
}

// REF/OUT AND EXCEPTIONAL EXITS
//
// A ref parameter reads and writes caller storage through an exact-type alias. It is assigned
// on entry and has no temporary default. An out parameter is not readable on entry and must be
// definitely assigned on every normal callee exit. A throw edge makes no promise about out
// state. A call with out establishes caller assignment only when it returns normally; if a
// later operation throws, the enclosing catch state reflects assignments completed before that
// later throw. Ref/out argument location evaluation occurs once and in source order.
//
// Catch state comes from the throw edge, not the eventual normal state. For a call whose
// exception may occur before it writes one or more out arguments, those out locations remain
// unassigned in the handler. Finally assignment is applied before a pending normal return,
// whereas a throw from finally follows the cleanup primary/suppressed policy.

func readThenFail(out value: int): () throws NetworkFailure {
    value = 1
    throw NetworkFailure()
}

func catchDoesNotAssumeThrowingCallOut(): int throws NetworkFailure {
    var value: int
    try {
        readThenFail(out value)
    } catch (failure: NetworkFailure) {
        // Positive only if the callee's contract guarantees assignment before this throw;
        // the current signature has no such effect-sensitive out guarantee, so this is
        // intentionally a proof obligation rather than a general language promise.
        return value
    }
    value
}

func finallyCompletesPendingReturn(out value: int): () {
    try {
        return
    } finally {
        value = 1
    }
}

func exceptionDoesNotCompleteOut(out value: int): () throws NetworkFailure {
    value = 1
    throw NetworkFailure()
}

func outMissingOnNormalReturn(flag: bool, out value: int): bool {
    if flag {
        value = 1
        return true
    }
    // Negative: a normal return remains with value unassigned.
    false
}

// MAIN AND FOREIGN BOUNDARIES
//
// Main.main(args: []String): () is an ordinary Sol callable for checked-effect analysis. It
// may declare a checked budget. Only the process entry adapter is a final reporting boundary;
// it catches an uncaught Throwable, writes a sanitized diagnostic to stderr, and returns a
// nonzero status. There is no top-level function exemption.
//
// CLR, C++/CLI, C++, Ruby and other foreign exception objects do not cross into Sol by accident.
// A bridge catches them at its boundary and returns a declared status/diagnostic/result carrier.
// A native callback similarly catches Sol failures before returning through a C ABI. The
// explicit carrier policy is separate from source-level checked-effect coverage.

object Main {
    func main(args: []String): () throws NetworkFailure {
        openConnection()
    }
}

func nativeBoundary(): int throws NetworkFailure {
    // The bridge's declared Sol contract is checked like any other callable.
    openConnection()
}

// DIAGNOSTICS AND METADATA
//
// Source diagnostics identify the escaping effect and the operation that introduces it. The
// source span is UTF-8 based and stable for the syntax snapshot. Candidate diagnostics distinguish
// rejected, ambiguous, deferred, and resource-limited outcomes; they do not union effects from
// every rejected overload. Duplicate throws entries, non-Throwable types, nullable thrown
// values, uncovered checked effects, invalid override widening, unreachable catch handlers,
// illegal cleanup transfers, and callback effect narrowing have distinct error conditions.
//
// Function effects survive generic substitution and are serialized with source/API metadata.
// A generic throws E parameter must constrain E to Throwable. Function type effects, accessor
// effects, constructor effects, and object-initialization effects are not recovered from method
// names or generated string signatures. A metadata consumer rejects incompatible schema/compiler
// identity rather than silently dropping a checked effect list.
//
// The rules in this file state the intended language contract. Parsing this source file through
// CST, PSI, and AST only validates syntax and tree ownership. It does not prove that every
// comment's semantics is implemented, that a library API exists, that a callback executes, or
// that a native exception can unwind through the runtime.

// EFFECT-SET COMPUTATION ORDER
//
// The analyzer first resolves the selected callable and substitutes its type parameters. It
// then visits explicit arguments once in source order, evaluates only the selected missing
// defaults in declaration order, and adds effects from the selected declaration contract or
// callback type. Property and compound-operator evaluation contributes accessor/operator
// effects in execution order. Branches union only effects that can escape their corresponding
// path. Matching catches remove their contained incoming effects; handler and cleanup effects
// are added after that subtraction. Finally, the remaining checked set is compared with the
// callable's declared budget.
//
// This order prevents false results. A rejected overload must not leak an effect into a call
// that selected a different overload. An explicit argument must not retain the default
// expression's effect for the same formal. A catch for one branch cannot remove a sibling
// effect. A cleanup failure cannot be erased because the protected operation already threw.
// A callback's effect belongs to invocation rather than lambda construction.
//
// Diagnostic output sorts effect names deterministically for display, but the ordering is not
// semantic. Type identity, generic substitution, and subtype relationships determine set
// membership. Two exception classes with the same simple name in separate modules are not the
// same effect. A subtype is covered only through the resolved nominal hierarchy.

// RESOURCE AND COMPLETENESS BOUNDARY
//
// Call graph recursion, inheritance traversal, generic effect matching, and path-state growth
// have explicit budgets. A budget is checked before expanding work that would exceed it.
// Cancellation is observed during large traversals. A resource diagnostic is not converted to
// an empty effect set, a warning, or success. A Deferred result identifies source nodes for
// which the compiler lacks a complete effect or flow proof.
//
// A consumer may use source-analysis facts for navigation, diagnostics, or a conservative IDE
// experience. It may not use a partial effect set as proof that native code generation is safe.
// Verified FIR independently confirms selected targets, exception edges, cleanup order, and
// operation effects before either AOT or ORC lowering consumes the body.

// EFFECT SUBTRACTION, COVERAGE, AND SUBTYPE IDENTITY
//
// A declared throws list is a budget of checked exception types, not a promise that every listed
// type will be thrown. The compiler may report a redundant entry when it can prove that no
// reachable operation contributes that effect, but it must not silently reinterpret a declared
// type as a return value or as a catch-all for unrelated failures. RuntimeException descendants
// are unchecked for budget coverage; Throwable and Exception descendants outside that subtree
// remain checked according to their resolved nominal ancestry.
//
// Coverage asks whether each escaping checked effect is a subtype of at least one declared budget
// type. A budget containing a base type covers its descendants. A budget containing only a
// descendant does not cover a possible base or sibling. Nullable exception references cannot be
// thrown without a non-null proof. Non-Throwable classes cannot appear in a throws clause or as
// a throw operand. Type aliases are resolved before these checks; textual equality is not enough.
//
// A catch removes only the incoming effects that can be represented by its catch type. Catching
// a subtype does not erase the remaining base effect. Catching a broad type makes later narrower
// catches unreachable only when the earlier handler covers every possible value of those types
// and the handler ordering is otherwise legal. Exception hierarchy cycles invalidate hierarchy
// analysis and cannot be repaired by treating all involved classes as siblings.
//
// For example, if a protected expression may produce NetworkFailure and TimeoutFailure, then
// `catch (error: NetworkFailure)` removes only the network alternative. The timeout path still
// reaches a later matching handler or the enclosing checked budget. A handler's own throw is not
// reconsidered by sibling catches. It is an outgoing effect of the handler body and may be
// caught by an enclosing try or must be covered by the current callable.

// EFFECTS OF EXPRESSIONS AND SELECTED OPERATIONS
//
// Effect accumulation follows evaluation order. A constructor call evaluates explicit
// arguments in source order, evaluates selected defaults in declaration order, allocates or
// initializes according to the runtime contract, then executes the selected initializer. Each
// step contributes only its own possible effects. If argument evaluation throws, later arguments,
// defaults, and constructor body do not execute. A default expression's effect is omitted when
// the caller supplied that parameter explicitly.
//
// Property read and write are distinct operations. A simple assignment evaluates the receiver,
// index arguments, and right-hand side once, then invokes the selected setter. Compound update
// additionally invokes the getter and selected operator before the setter. If a getter or
// operator throws, the setter is not called. If the setter throws, prior getter/operator side
// effects remain. A delegate's initialization hook contributes only during the specified
// initialization phase; later reads do not re-run it.
//
// Virtual call effects are determined from the callable contract valid for the static receiver
// and dispatch slot. Overrides cannot widen a checked budget in a way that invalidates callers
// typed against the base declaration. If a closed-world target set is unavailable, the selected
// slot contract or an explicit effect-safe interface contract governs. A missing target body is
// not evidence of an empty effect set.
//
// Function values carry their checked-effect budget. Creating a closure does not execute its
// body; invoking it does. Conversion to a function type may preserve or safely widen the allowed
// effect set, but it cannot narrow away a possible checked effect. A callback parameter's type is
// part of the caller-visible contract even when a particular invocation happens conditionally.
// If a callback is not invoked on a reachable path, its body contributes no effect on that path.
// If a callback may be invoked by a foreign or asynchronous consumer outside the analyzed call,
// the boundary must retain its declared effect contract.

// CLEANUP COMPOSITION AND SUPPRESSED FAILURES
//
// Finally and defer are cleanup edges attached to all exits from their protected lexical region.
// A pending return value is evaluated before cleanup and delivered only if cleanup completes
// normally. An assignment in cleanup is visible to the caller after a pending return. Cleanup
// cannot manufacture a return value, return from the enclosing function, break an outer loop, or
// continue an outer loop. Such source is rejected rather than treated as an override.
//
// When the body has a primary exception and cleanup throws another exception, the original
// exception remains primary and the cleanup exception is recorded as suppressed. When there is
// no primary exception, a cleanup exception becomes the outgoing failure. Multiple cleanups run
// in reverse registration order; failures are retained in deterministic execution order without
// erasing the primary. The runtime recording policy has bounded, allocation-aware behavior under
// memory pressure, but the source analyzer still includes every statically known cleanup effect.
//
// A catch around a try/finally sees the exception after finally completes. Therefore a cleanup
// failure can replace normal completion or become suppressed under a pending primary exception,
// and it can change which handler receives the outgoing primary. A handler surrounding the
// cleanup region cannot catch a failure that has not yet been produced. FIR must represent the
// ordered cleanup and exceptional successors explicitly; a flat list of throws names is
// insufficient to reconstruct runtime behavior.

// SOURCE CONTRACTS, METADATA, AND FOREIGN CARRIERS
//
// Public checked effects are part of the source/API contract. Renaming an exception, moving it
// into another module, changing its base class, or changing a function-type budget may alter
// source compatibility even when the runtime ABI representation is unchanged. API diff tooling
// compares resolved symbol identities, generic substitutions, nullable annotations, and effect
// ancestry. It does not compare only printed names or declaration text.
//
// Compiled metadata carries enough information to rebuild nominal subtype coverage and callback
// compatibility. Metadata without effect information is marked legacy/incomplete; it is not
// interpreted as pure. The compiler may offer an explicit compatibility mode with diagnostics,
// but verified native lowering requires a complete contract for all reachable calls and cleanup
// edges. A schema-version mismatch or corrupted hierarchy produces a hard or deferred analysis
// outcome according to the failure, never an empty effect set.
//
// Foreign code exchanges explicit status/result carriers at C ABI, C++/CLI, CLR, Ruby, and
// process boundaries. A carrier maps a bounded foreign failure description to a declared Sol
// exception or result variant. Raw foreign exceptions do not unwind through Sol frames unless a
// separately verified runtime bridge defines that ABI. Sol exceptions crossing into a foreign
// callback are caught and converted before control returns to the foreign caller.
//
// The carrier's message is data, not a format string or source location. It is length-bounded,
// encoding-checked, and safe to retain through out-of-memory paths according to the runtime
// policy. Failure to allocate an exception wrapper cannot lead to a partially initialized Sol
// object escaping. The implementation may use a preallocated emergency Throwable, but it must
// preserve the primary/suppressed ordering and report when detailed trace information is absent.
