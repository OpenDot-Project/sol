// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: semantic example; the accepted and rejected flow outcomes are asserted by FIR and body-analysis tests.
module examples.semantics.indirect_callback_flow

class Failure: Exception {}

// A non-throwing `out` callback assigns the caller's local after normal return.
func readAfter(callback: (out int) -> ()): int {
    var value: int
    callback(out value)
    value
}
// expect: Source: Bound; FIR: Ready; the indirect call writes the out slot on its normal continuation.

// A possibly throwing callback may not assign the location before it throws.
func readAfterCatch(callback: (out int) -> () throws Failure): int {
    var value: int
    try { callback(out value) } catch (error: Failure) {}
    value
}
// expect: Source: Rejected(SOL2240); the catch path reaches `value` without an out assignment.

// The callback's checked effects contribute to the enclosing function budget.
func invoke(callback: () -> () throws Failure) throws Exception {
    callback()
}
// expect: Source: Bound; the callback effect is covered by the declared Exception budget.

// A ref callback may change a nullable local, invalidating the earlier smart cast.
class Item { val size: int = 1 }
func inspect(item: Item?, mutate: (ref Item?) -> ()) {
    if item == null { return }
    mutate(ref item)
    item.size
}
// expect: Source: Rejected(SOL2243); ref callback mutation invalidates the nullable smart cast.
