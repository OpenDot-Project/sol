// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: semantic examples; rows identify source and FIR behavior, while runtime qualification is recorded separately.
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

// Matching source types do not manufacture a closure environment.
func capturedCallback(seed: int): []((int) -> int) {
    [{ value -> value + seed }]
}
// expect: Source: Bound; FIR: Rejected(SOL2400); closure capture requires environment ownership and GC roots.

// Nullable function elements are still outside the admitted layout.
func nullableCallbackElements(callbacks: [](((String) -> String)?)): int = callbacks.length
// expect: Source: Bound; FIR: Rejected(SOL2400, nullable function-value element layout).
