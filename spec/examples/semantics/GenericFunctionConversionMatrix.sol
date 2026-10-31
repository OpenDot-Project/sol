// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: source-level generic function-value inference is verified for this matrix; only exact
// function signatures currently enter native FIR, because variance adapters need an explicit ABI.
module examples.semantics.generic_function_conversion_matrix

open class Base {}
class Derived: Base {}

func <T> identity(value: T): T = value

func <T> edit(ref slot: T, replacement: T): () {
    slot = replacement
}

func <T> initialize(out slot: T, replacement: T): () {
    slot = replacement
}

func <E: Throwable> read(): String throws E, IOException = "value"

func covariantResultAndContravariantInput(): () {
    val callback: (Derived) -> Base = identity
}
// expect: Source: Bound; T=Derived from the value parameter, and Derived -> Base is a result conversion.
// expect: FIR: Rejected(SOL2400) until the non-identity function adapter has a verified native representation.

func refStorageAndValueInputAreSolvedSeparately(): () {
    val callback: (ref Derived, Base) -> () = edit
}
// expect: Source: Rejected; ref fixes T to Derived, then the value parameter would need to accept Base.
// A Base value cannot be passed to the source function's Derived value parameter.

func exactRefAndValueConversion(): () {
    val callback: (ref Derived, Derived) -> () = edit
}
// expect: Source: Bound; ref/out parameter storage is invariant, while the value parameter uses ordinary compatibility.

func outRefCannotChangeItsMode(): () {
    val callback: (ref Derived, Derived) -> () = initialize
}
// expect: Source: Rejected(SOL2206); `out` is not interchangeable with `ref`.

func narrowEffectsFitABroadBudget(): () {
    val callback: () -> String throws Exception = read
}
// expect: Source: Deferred; current generic function-value inference does not close E while matching a
// multi-member effect set. No target is selected until the effect-set solver proves one substitution.

func missingEffectBudgetIsRejected(): () {
    val callback: () -> String = read
}
// expect: Source: Deferred; the same open effect-set candidate remains unresolved, and checked effects
// are never erased to manufacture a pure function value.
