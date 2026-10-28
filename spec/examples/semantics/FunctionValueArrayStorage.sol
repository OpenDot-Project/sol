// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: exact-ABI nullable function values now have verified FIR, AOT and ORC coverage for
// locals, parameters, results, ref/out, defaults, varargs, arrays and flow-narrowed indirect calls.
// The native pointer subset still excludes captures, adapters and checked null assertions.
module examples.semantics.function_value_array_storage

func increment(value: int): int = value + 1
func twice(value: int): int = value * 2
func <T> identity(value: T): T = value

// A typed array stores receiver-free function pointers and keeps their signature.
func indexedCallback(): int {
    val callbacks: []((int) -> int) = [increment, twice]
    callbacks[1](21)
}
// expect: Source: Bound; FIR: Ready; array element layout is scalar pointer-width/no-GC-scan; result=42.

// A generic function reference is closed from the array element's expected function type.
func genericCallback(): String {
    val callbacks: []((String) -> String) = [identity, identity]
    callbacks[1]("Sol")
}
// expect: Source: Bound(T=String for both references); FIR: Ready; generic instances are reachable from references.

// Callback arrays can be passed and indexed by another function without creating an environment.
func invokeFirst(callbacks: []((int) -> int), value: int): int = callbacks[0](value)
// expect: Source: Bound; FIR: Ready for the raw code-pointer ABI; target is an indirect call.

func callbackIteration(): int {
    val callbacks: []((int) -> int) = [increment, twice]
    var total = 0
    for val callback in callbacks {
        total += callback(20)
    }
    total
}
// expect: Source: Bound; FIR: Ready; each loop value is a function pointer copy; result=41.

// Nested managed arrays trace inner arrays but treat each inner function pointer as scalar data.
func nestedCallbackArray(): int {
    val callbacks: [][]((int) -> int) = [[increment], [twice]]
    callbacks[1][0](20)
}
// expect: Source: Bound; FIR: Ready; outer array traces rows; row elements have no GC scan shape; result=40.

// Empty arrays use the written function type because no element can supply type evidence.
func emptyCallbacks(): () {
    val callbacks: []((String) -> String) = []
    val count = callbacks.length
}
// expect: Source: Bound; FIR: Ready; length=0; element signature remains available for later reads.

// A nullable function element is one pointer-sized slot. Null has the zero pointer representation;
// function addresses remain non-GC scalar data in Yellow Tea's element descriptor.
func nullableCallbacks(): int {
    val callbacks: []((() -> int)?) = [null, func() { 42 }]
    val first = callbacks[0]
    if first == null { return 0 }
    first()
}
// expect: Source: Bound; FIR: Ready; AOT/ORC test verifies null and non-null entries in one array.

func replaceNullableCallback(): int {
    var callback: (() -> int)? = null
    if callback == null { callback = func() { 41 } }
    if callback != null { return callback() + 1 }
    0
}
// expect: Source: Bound; FIR: Ready; the mutable slot narrows only after the null comparison.

func invokeAfterNullExit(callback: (() -> int)?): int {
    if callback == null { return 0 }
    callback()
}
// expect: Source: Bound; FIR: Ready; the surviving continuation has a source-proven function type.

func nullableCallbackParameter(callback: (() -> int)?): int {
    if callback != null { return callback() }
    0
}
// expect: A nullable function is passed as one pointer value; a non-null call requires a branch proof.

// Matching source types do not manufacture a closure environment.
func capturedCallback(seed: int): []((int) -> int) {
    [{ value -> value + seed }]
}
// expect: Source: Bound; FIR: Rejected(SOL2400); closure capture requires environment ownership and GC roots.

// Requiring a nullable element type does not make the array reference itself nullable.
func nullableCallbackElements(callbacks: [](((String) -> String)?)): int {
    val callback = callbacks[0]
    if callback == null { return 0 }
    callback("Sol").length
}
// expect: Source: Bound; FIR: Ready; element stride is pointer-sized and the leaf has no GC scan shape.

// A nullable non-function object and a nullable function are different native layouts.
func nullableObjectStillOutside(values: []String?, value: String?): () = ()
// expect: Source: Bound; FIR: Rejected(SOL2400); managed nullable references need the object ABI.
