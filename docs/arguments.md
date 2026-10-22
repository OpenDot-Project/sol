<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Argument and default contracts

Sol has named arguments, parameter defaults and one variable-length parameter per callable.
The source contracts are [Functions.sol](../spec/Functions.sol) and [Lambdas.sol](../spec/Lambdas.sol).
The larger [argument-ordering example](../spec/examples/calls/ArgumentOrdering.sol) explains which
expressions run and in what order. These rules apply independently of how much of the backend exists.

## Parameters and named arguments

`func add(a, b: int)` declares two independent parameters. A default, vararg or constructor-property
parameter requires a single-name group. Duplicate parameter names and multiple varargs are errors.
Ref/out require matching call-site markers, writable exact storage and no parameter default.

Ordered mixing is legal: for parameters `a,b,c`, both `f(a = 1, 2, c = 3)` and `f(1, b = 2, 3)`
preserve parameter order. Fully named calls may reorder arguments: `f(c = 3, a = 1, b = 2)`.
After a named argument skips or reorders parameters, subsequent ordinary arguments must be named.
`f(b = 2, 1)` is rejected; a positional argument does not search for the next empty parameter.
Unknown names, duplicate names and filling an already occupied parameter are errors.

## Vararg

`vararg values: T` is represented inside the body as `[]T`. The one vararg may occur anywhere in
the parameter list. Positional values continue filling it; parameters declared after it are named,
apart from a final function parameter supplied through an outside lambda.

```sol
module examples.argument.contracts

func gather(head: int = 0, vararg values: int, done: bool): () {}
func defaults(): []int = [1, 2, 3]
func fallback(vararg values: int = defaults()): () {}

func example(items: []int): () {
    gather(10, 20, *items, 30, done = true)
    gather(values = items, done = true)
    gather(values = *items, done = true)
    gather(done = true)
    fallback()
    fallback(values = items)
}
```

Expanded elements and multiple `*array` spreads retain source order. A named vararg argument is a
whole array: `values = items` and `values = *items` require arrays, while `values = 1` is rejected.
It cannot also be filled with other expanded/named arguments. Ordinary named array passing keeps
the array's invariant element type; spread elements use the supported element conversion rules.
Sol arrays have one `[]T` source family, including primitive elements.

A vararg default must produce `[]T`. If no argument is supplied, its default is used; without one,
the callable receives an empty array. An explicit value, including an explicit empty array, suppresses
that default. A default expression is not implicitly appended to supplied elements.

## Defaults and evaluation

An ordinary default expression produces a value compatible with its parameter type. It is evaluated
only when the corresponding argument is omitted. It may refer to earlier parameters; its own or a
later parameter value is unavailable. Normal nested scopes may introduce their own same-named locals.

All explicit arguments are evaluated once in source order, regardless of named reordering.
Afterwards, missing defaults are evaluated once in declaration parameter order. Earlier parameters
already contain their explicit or defaulted values. Default expressions are not eagerly computed
when declaring a function and are not automatically shared between calls.

An override cannot declare replacement defaults. It inherits the original declaration's default
expressions. The statically selected call contract determines defaults; virtual dispatch selects the
body. Conflicting inherited origins for one default parameter produce an error. See the
[inheritance example](../spec/examples/calls/InheritedDefaults.sol).

## Trailing lambda

`f { 42 }` and `f() { 42 }` supply the final function parameter, even when earlier parameters use
defaults or an empty vararg. `f(head = 1) { 42 }` has the same final destination. A lambda inside
parentheses remains at its ordinary position unless explicitly named; `f({ 42 })` cannot silently
jump past a leading default. Supplying the final parameter twice remains an error.

Named lambda headers, `$0` positional references and ref/out lambda modes retain their own contracts.
There is no implicit `it` parameter. Full expected lambda typing is a separate compiler analysis.

## Current SDK evidence

The AST records `AstArgument.IsTrailingLambda`, preserving outside/inside syntax. The argument mapper
validates structure before expression typing and records source-to-parameter assignments, array-valued
named varargs, empty varargs and defaulted slots. Unsupported lambda/expression typing leaves a call
`Deferred`, with its argument mapping available for inspection.

`SourceAnalysis.Defaults` exposes `DefaultValueAnalysis`. `GetDefaults(callable)` returns original
expression owners, including inherited override defaults found through substituted nominal bases.
It checks own/later parameter reads and reports conflicting default origins. `IsComplete` describes
this lookup only; it does not prove complete override/accessibility correctness.

`CallCandidate.DefaultArguments` lists selected default expressions in declaration order, with their
original owners. `EmptyVarargParameters` distinguishes empty packing from evaluating a default.
`CallBindingJson` preserves these origins along with source argument order and trailing/array flags.
No default or explicit expression is executed by these inspection APIs.
When a selected default has supported expression typing, the binder checks its value against the
specialized parameter type (an array for vararg defaults). Unknown default expression typing leaves
the call deferred. This use-site check does not replace full declaration/body/effect validation.

Full default expression typing/effects, receiver/virtual dispatch, all lambda conversions, definite
assignment and lifetime checking still require later work. Evaluation and native execution require
verified FIR and the backend/runtime. The specification describes those intended rules now, without
claiming the implementation already executes them.
