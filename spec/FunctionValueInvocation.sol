// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: source binding and bounded FIR contracts describe fixed-arity function values;
// native coverage exercises closed noncapturing anonymous functions and indirect calls.
module source.function_value_invocation
import std.stdio

// Purpose and vocabulary
// ======================
//
// This contract specifies invocation of an expression whose resolved type is a function type.
// The expression may be a local value initialized from an anonymous function, a named function
// reference selected by context, a function returned by another function, or a value read from an
// array. Such a call is an indirect function-value call. The source call target is still evaluated
// as an expression; its runtime representation is not a source-level name or declaration target.
//
// A function type has an ordered sequence of parameter types, an ordered sequence of parameter
// passing modes, one result type, and an ordered checked-effect set. The function type does not
// retain declaration parameter names, default expressions, annotations, source declaration
// identity, or the original spelling of a vararg declaration. These omissions are intentional:
// they are not part of the callable ABI represented by a function value.
//
//     type shape: (mode_0 T_0, mode_1 T_1, ...) -> R throws E_0, E_1
//     call shape: target(value_0, value_1, ...)
//
// A call through a function type is bound against that ordered shape. The compiler must not
// recover names or defaults by searching for a declaration that once produced the value. Two
// code pointers with the same function type have the same call contract even when their source
// declarations used different parameter names or default expressions.

func add(left: int, right: int = 1): int = left + right

func declarationAndValueCalls(): int {
    val declarationCall = add(40)
    val callback: (int, int) -> int = add
    val valueCall = callback(40, 2)
    declarationCall + valueCall
}

// `add(40)` selects the declaration and may use its default. `callback(40, 2)` invokes a function
// value and must supply both slots. The callback's type has two `int` parameters; it does not have
// an optional second slot. A call with zero or one actual argument is invalid even though the
// selected value originated from a declaration whose second parameter had a default.

val callbackArity: (int, int) -> int = add

func rejectedFunctionValueArities(): () {
    callbackArity(1)
    // expect: source error; a function type has no declaration-default metadata.
    callbackArity()
    // expect: source error; positional calls supply one actual for each function-type parameter.
    callbackArity(1, 2, 3)
    // expect: source error; excess actuals cannot be packed into a function-type parameter.
}

// Argument form and parameter identity
// ====================================
//
// Function-value calls use positional arguments in function-type parameter order. They do not
// support named arguments because the function type has no parameter names. This remains true
// after assignment, grouping, return, array storage, or a cast that preserves the same function
// type. No source declaration lookup is performed to recover a matching name.
//
// The call's actual count must equal the function type's parameter count. An omitted value
// parameter is not synthesized from a default. A surplus value parameter is not ignored. A
// function type with zero parameters is called with `()`; `()` is a real empty tuple type and is
// also the result spelling for a no-value function. There is no `Unit` pseudo-type.

func combine(first: int, second: int = 9): int = first + second

func exactPositionalInvocation() {
    val callback: (int, int) -> int = combine
    val result = callback(3, 4)
    println(result)
}

func rejectedNamedInvocation() {
    val callback: (int, int) -> int = combine
    // expect: source error; callback has no source-level parameter names.
    val result = callback(second = 4, first = 3)
}

func noValueAndEmptyTuple(): () {
    val callback: () -> () = func() { () }
    callback()
}

func immediateAnonymousInvocation(): int {
    val result = (func(value: int): int { value + 1 })(41)
    result
}

func anonymousFunctionArray(): int {
    val callbacks: []((int) -> int) = [
        func(value: int): int { value + 1 },
        func(value: int): int { value * 2 }
    ]
    callbacks[1](21)
}

// The target and source arguments are evaluated once
// ================================================
//
// The function-value target is evaluated exactly once before the call's actual arguments. The
// actual argument expressions are evaluated exactly once in their source order, left to right.
// The formal slot order is the function type's declaration order. Because indirect calls are
// positional, source order and slot order coincide. A target expression that returns a callback
// is evaluated before any actual argument expression.
//
// The code pointer is retained across argument evaluation. It is not reevaluated after a side
// effect changes the variable from which the target was loaded. Each result value remains live
// until the indirect call consumes it. This matters for managed references and Yellow Tea roots;
// backend lowering must use the ordinary call-evaluation and root-preservation rules.

var evaluationTrace = 0

func mark(value: int): int {
    evaluationTrace = evaluationTrace * 10 + value
    value
}

func selectCallback(): (int, int) -> int {
    val selected: (int, int) -> int = func(left: int, right: int) { left + right }
    selected
}

func indirectCallEvaluationOrder(): int {
    evaluationTrace = 0
    val result = selectCallback()(mark(1), mark(2))
    // expect: result is 3 and evaluationTrace is 12.
    // The target factory runs before mark(1), which runs before mark(2).
    result
}

func targetIsLoadedOnce(): int {
    var selected: (int) -> int = func(value: int) { value + 1 }
    val target: (int) -> int = selected
    selected = func(value: int) { value + 2 }
    target(40)
    // expect: the earlier value calls its original code pointer; variable reassignment does not
    // retroactively change another local's function value.
}

// Ref and out are exact call-site obligations
// ===========================================
//
// A function type records each parameter's `ref` or `out` mode. Invocation syntax must use the
// same mode at the corresponding position. The compiler must pass the original writable storage;
// it cannot create a temporary, widen a numeric type, box the value, copy it into a local, or turn
// a property getter result into an address. A `ref` argument must already be definitely assigned.
// An `out` argument becomes definitely assigned after the call's normal return edge and remains
// unassigned on an exceptional edge unless the language's call-flow contract says otherwise.
//
// Ref/out parameter types are invariant. Ordinary value-parameter conversions do not apply to
// them. A callback of `(ref short) -> ()` is not callable through `(ref int) -> ()`; nor is a
// callback accepting `int` compatible with a `ref short` location. A callback's out mode cannot
// be changed to ref or value by a conversion.

func mutate(ref value: int, out text: String) {
    value += 1
    text = "updated"
}

func exactRefOutCall() {
    val callback: (ref int, out String) -> () = mutate
    var number = 40
    var message: String
    callback(ref number, out message)
    // expect: after normal return, number is assigned and message is assigned.
}

func wrongRefOutMode() {
    val callback: (ref int, out String) -> () = mutate
    var number = 40
    var message: String
    // expect: source error; both actual passing modes must exactly match the function type.
    callback(number, ref message)
}

func wrongRefStorageType(ref number: short) {
    val callback: (ref int) -> () = func(ref value: int) { value += 1 }
    // expect: source error; no numeric conversion is allowed for a ref storage location.
    callback(ref number)
}

func wrongOutStorageType(out message: String?) {
    val callback: (out String) -> () = func(out value: String) { value = "ready" }
    // expect: source error; nullability is part of exact out storage identity.
    callback(out message)
}

// A callback's own out contract is checked on every reachable normal exit when the callback body
// is analyzed. A body that only throws has no normal exit and therefore has no normal out result.
// At the caller, the out location receives its normal-success assignment fact only if the
// indirect invocation completes normally. Capturing an out/ref parameter in a nested function
// does not perform the assignment for the enclosing function.

func completeOnEveryBranch(flag: bool, out result: int): int {
    val callback: (bool, out int) -> int = func(flag: bool, out value: int): int {
        if flag { value = 1; return 10 }
        value = 2
        20
    }
    callback(flag, out result)
}

func incompleteOutCallback(flag: bool) {
    val callback = func(flag: bool, out value: int): int {
        if flag { value = 1; return 10 }
        return 20
        // expect: source error; a normal return leaves value unassigned.
    }
}

// Vararg source declarations have a fixed indirect-call slot
// ==========================================================
//
// In a named function declaration, a vararg parameter may have call-site expansion rules. A
// function value has no such mapping metadata. When a source anonymous function declares
// `vararg values: T`, its function body sees one `[]T` parameter and its function type contains
// one value-mode `[]T` slot. A call through that function value supplies one array argument. It
// does not expand scalar values, accept a spread marker, synthesize an empty array, or apply a
// named-parameter spelling.
//
// The named declaration case and the indirect function-value case remain different even if the
// same source function is referenced in both ways. Type erasure must not erase this distinction
// during source binding: direct calls are mapped to the declaration; indirect calls are mapped to
// the resolved function type.

func collect(vararg values: int): int = values.length

func fixedArrayCallback(): int {
    val callback: ([]int) -> int = func(vararg values: int): int { values.length }
    callback([1, 2, 3])
}

func noScalarExpansion(): int {
    val callback: ([]int) -> int = func(vararg values: int): int { values.length }
    // expect: source error; the function type has one []int slot, not one int slot per element.
    callback(1, 2, 3)
}

func noSpreadExpansion(values: []int): int {
    val callback: ([]int) -> int = func(vararg items: int): int { items.length }
    // expect: source error; spread is declaration-call syntax, not part of this indirect ABI.
    callback(*values)
}

func noNamedArrayParameter(values: []int): int {
    val callback: ([]int) -> int = func(vararg items: int): int { items.length }
    // expect: source error; the type slot has no `items` name.
    callback(items = values)
}

// Function type conversions
// =========================
//
// Assignment and argument conversion uses the language's ordinary function-type compatibility
// rules. Value parameters follow the specified function variance relation; ref/out parameters
// remain invariant. Result types follow result covariance, and the checked-effect set on the
// source function must fit inside the target function's checked-effect budget. These conversions
// describe source type compatibility; they do not guarantee a native adapter exists.
//
// The source analyzer retains the exact selected function type and conversion evidence. FIR may
// admit an identity/exact-ABI value and reject a source-valid conversion when no verified adapter
// has been implemented. It must not silently cast a function pointer to a different calling
// convention or choose another overload solely because one would be easier to lower.

open class Animal {}
class Dog: Animal {}

func animalResult(): Animal = Animal()
func dogResult(): Dog = Dog()

func sourceVariance(): () {
    val acceptsAnimal: (Animal) -> Dog = func(value: Animal): Dog { Dog() }
    val acceptsDog: (Dog) -> Animal = acceptsAnimal
}

// Checked effects belong to invocation
// ====================================
//
// Constructing a function value does not execute its body and does not add the body's checked
// effects to the enclosing expression. Invoking that value contributes the function type's
// checked-effect set to the caller. A surrounding try/catch may handle those effects at the call
// site; it does not erase the effect from the function value's type. The target expression and
// argument expressions also contribute their own effects according to the normal call-evaluation
// order.

open class IOException: Exception {}

func mayFail(): int throws IOException { return 1 }

func makeDeferredCall(): () -> int throws IOException {
    val callback = func(): int throws IOException { mayFail() }
    callback
}

func handleAtInvocation(): int {
    val callback: () -> int throws IOException = makeDeferredCall()
    try { callback() }
    catch (error: IOException) { 0 }
}

func exposeAtInvocation(callback: () -> int throws IOException): int throws IOException {
    callback()
}

// Different consumers retain or handle the effect at the call, not at value construction. An
// unchecked RuntimeException does not become a checked effect merely because the function value
// is stored. A callback declared with a narrower effect contract cannot be invoked as though it
// carried a wider unchecked-only exception rule.

// Named function values and overload resolution
// =============================================
//
// A contextually typed reference to an overloaded named function selects one candidate whose
// resolved signature converts to the target function type. Selection retains the selected source
// declaration and any closed generic type arguments in source analysis. After selection, invocation
// through the value uses only the target function type's positional call contract. It does not
// use parameter names or default values from the selected declaration.
//
// An incomplete candidate is not silently removed if it could change which overload wins. The
// source analysis must either prove one unique candidate, report an ambiguity/incompatibility, or
// leave the binding deferred. FIR independently verifies the selected target and its exact
// specialized signature.

func convert(value: int): int = value
func convert(value: String): String = value

func selectedOverload(): () {
    val integerConverter: (int) -> int = convert
    val textConverter: (String) -> String = convert
    val number = integerConverter(3)
    val text = textConverter("three")
}

// FIR boundary and conformance notes
// ==================================
//
// A verified function-value call records the source target expression, ordered parameter slot
// types and modes, result type, checked effects, argument-to-slot identity, and complete source
// origins. It does not encode declaration defaults or source parameter names into the value.
//
// The current native function-pointer slice accepts only complete closed signatures that match
// the implemented ABI. The code pointer has no closure environment. A source function value that
// captures locals or a receiver, needs a variance/effect adapter, contains an unsupported generic
// target, or has a nullable/environment representation remains outside this FIR subset. Source
// validity and backend support are separate outcomes: a valid source value can be rejected by FIR
// until its environment, lifetime, rooting, and ABI have a verified representation.
//
// Required conformance properties include:
//
// - the target expression is evaluated once, before actual arguments;
// - each actual argument is evaluated once in source order;
// - no actual is dropped, duplicated, implicitly packed, or retargeted;
// - the indirect call uses the resolved function type and exact ref/out modes;
// - the normal-success continuation receives out-assignment facts;
// - checked effects are charged to the caller at invocation time;
// - AST source identity and FIR source correspondence survive lifting;
// - unsupported closures are rejected before LLVM emission rather than miscast.

object Main {
    func main(args: []String) throws IOException {
        val callback: (int) -> int = func(value: int) { value + 1 }
        println(callback(41))
    }
}
