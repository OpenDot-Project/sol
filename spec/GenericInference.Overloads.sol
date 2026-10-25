// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative candidate-local inference/overload order; full lambda candidate isolation is pending.
module source.spec.generic_inference.overloads

// Candidate-local inference and overload resolution
// ================================================
//
// Each overload candidate receives a private source argument mapping, a private substitution, a
// private inference-bound set, a private constraint result, and candidate-specific diagnostics.
// One candidate never publishes a type guess to another candidate.

// Phase A: gather declarations
// ----------------------------
// Resolve lexical shadowing, module/member receiver category, visible declaration set, and
// inherited constructed paths. Do not evaluate a candidate body or choose a declaration by source
// order. If a potentially applicable declaration cannot be inspected, record that uncertainty.

// Phase B: map arguments
// ----------------------
// Validate named/positional order, duplicate assignment, vararg destination, spread/array shape,
// trailing lambda destination, explicit type argument arity, and ref/out markers. Mapping errors
// reject one candidate when they depend on its parameter list. A syntax-level invalid argument is
// reported independently of candidate selection.

// Phase C: gather and solve evidence
// ---------------------------------
// Apply fixed receiver substitutions and explicit type arguments. Collect ordinary input bounds,
// exact ref/out bounds, and expected result bounds. Gather evidence from every mapped argument
// before selecting a candidate. Do not use an omitted default or an untyped lambda parameter as a
// fabricated concrete type.
//
//     interface Keyed<T> { func key(): T }
//     class StringKey: Keyed<String> { override func key(): String = "k" }
//
//     func <T> choose(value: Keyed<T>): T = value.key()
//     func choose(value: std.core.Object): std.core.Object = value
//
//     val selected: String = choose(StringKey())
//
// The generic candidate can infer T = String through the base projection. Its specialized
// conversion vector competes with the non-generic Object overload. The generic candidate does not
// win merely because it is generic or because its constraint is narrower; the normal conversion
// ranks decide.

// Phase D: validate constraints and final signatures
// --------------------------------------------------
// Reject a candidate that fails any nominal bounds, allows-ref-struct constraint, nested type
// application requirement, required byref lifetime, or checked callable shape. A failed constraint
// does not erase the candidate's evidence; it makes the candidate inapplicable with a reason.
// Indeterminate constraints remain deferred when they could change the winner.

// Phase E: compare applicable candidates
// --------------------------------------
// Compare conversion rank vectors per source argument. The candidate with lower conversion rank on
// every compared argument and a strict improvement on at least one argument is better. If vectors
// are incomparable, neither candidate is better. After conversion vectors tie, prefer ordinary
// arity over vararg expansion, fewer defaults, then an exact non-generic candidate over a generic
// candidate. No declaration order tie-break exists.

// A deferred candidate can be a blocker. If it could accept the call and its conversion vector
// might beat the known applicable set, the binder cannot claim a unique selected result. The result
// is Deferred with the candidate reason. A rejected candidate with a proven conversion/constraint
// failure is not a blocker.

// Overload with expected result
// -----------------------------
//
//     func <T> load(key: String): T
//     func load(key: String): std.core.Object
//
//     val exact: String = load("primary")
//
// The expected String can infer T for the generic candidate. The specialized return type and
// conversion compatibility are checked before overload ranking. If the expected type is missing,
// the generic candidate may be deferred rather than assigned Object. A result context cannot make
// an otherwise non-applicable parameter signature valid.

// Literal candidates
// ------------------
//
//     func parse(value: int): String
//     func parse(value: long): String
//     func parse<T: NumberLike>(value: T): String
//
//     val parsed = parse(1)
//
// The literal retains its exact source magnitude and default `int` preference. Every candidate is
// tested for representability. A typed variable of type int does not acquire literal narrowing or
// a widening conversion. A generic T parameter may infer int from the literal's default type and
// then pass its constraints; it cannot infer a new T from every overload independently and reuse
// the first bound.

// Candidate isolation for lambdas
// -------------------------------
//
//     func select(block: (String) -> int): int
//     func select(block: (std.core.Object) -> int): int
//
//     val count = select { value -> value.byteLength }
//
// The untyped `value` receives a separate expected type per candidate. If the body is invalid for
// Object but valid for String, that rejection belongs only to the Object candidate. If both remain
// valid and equal-ranked, the call remains ambiguous. The compiler cannot cache one candidate's
// lambda result as a global source expression type before selection.

// Defaults and side effects
// -------------------------
//
// Defaults are mapped and type-checked for each candidate, but their runtime expressions are not
// executed during inference or overload comparison. After a unique candidate is selected, explicit
// source arguments evaluate once in source order. Omitted defaults evaluate next, in declaration
// parameter order, each under the closed substitution. A rejected candidate contributes no runtime
// side effect and cannot mutate flow state.

// Ref/out candidate ranking
// -------------------------
//
// Ref/out markers and exact storage types filter candidates before ordinary conversions. A
// candidate requiring a reference conversion, boxing, literal retyping, or user conversion for a
// ref/out parameter is rejected. An `out var` is created only after a unique candidate is chosen.
// If two candidates would assign it different types and cannot be ranked before that point, the
// call remains ambiguous/deferred; the compiler does not create two hypothetical locals.

// Inherited generic method candidates
// -----------------------------------
//
//     open class Base<T> {
//         open func <R> map(value: T): R
//     }
//
//     class Derived: Base<String> {
//         override func <R> map(value: String): R
//     }
//
//     val value: int = Derived().map<String>("x")
//
// Owner substitution makes the inherited parameter String. Method inference or explicit method
// arguments then bind the independent R. Override suppression applies to matching signatures only;
// unrelated base overloads remain candidates. A member method's owner map is never inferred from
// the name of a call argument.

// Ambiguity versus missing evidence
// ---------------------------------
//
// Missing evidence means a required type parameter has no lower, exact, or upper constraint.
// Conflicting evidence means constraints exist but do not share one valid candidate. Overload
// ambiguity means two complete applicable candidates tie. Deferred means an unimplemented analysis
// could still change applicability or ranking. A no-applicable result means all candidates have
// proven rejection reasons.
//
// These states have different downstream behavior. A compiler may show several candidate
// signatures for ambiguity, but it must not expose partial substitutions as selected types. A
// diagnostic count limit cannot silently change the selection. An analysis API can retain all
// reasons while the CLI chooses a concise primary message.
