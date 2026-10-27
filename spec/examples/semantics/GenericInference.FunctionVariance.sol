// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: function-shape inference and exact-signature specialized indirect calls are implemented;
// function-value variance conversions still need explicit native adapters.
module spec.genericinference.functionvariance

open class Base {}
class Derived: Base {}

func <T> apply(value: T, callback: (T) -> T): T = callback(value)

func callbackInputAndResult(value: Derived, callback: (Base) -> Derived): Derived =
    apply(value, callback)

func <T> makeCallback(): (T) -> T = { value: T -> value }

func expectedCallbackShape(): (Derived) -> Base = makeCallback()

func <T> acceptNested(callback: ((T) -> Derived) -> int): int = 1

func nestedParameterReversesTwice(callback: ((Base) -> Derived) -> int): int =
    acceptNested(callback)

// The first call combines a lower bound from `value` and the callback's upper/lower shape bounds.
// The second call infers T from a function-valued expected result. The nested callback reverses
// twice and contributes a lower bound from the innermost parameter.
//
// Negative source cases: a narrow `(Derived) -> Derived` callback cannot satisfy a formal
// `(Base) -> Base` input; `ref Base` cannot satisfy `ref Derived` even though value parameters
// permit variance. Native FIR supports a specialized callback call when its closed function type
// exactly matches the selected target ABI; a non-identity variance conversion still needs an adapter.
