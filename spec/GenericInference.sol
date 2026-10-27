// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative inference contract; the development binder implements a bounded subset.
module source.spec.generic_inference

// Generic call inference
// =====================
//
// Generic inference chooses concrete source types for declaration-owned type parameters at one
// call site. It does not infer arbitrary declarations through a project-wide call graph. Inference
// is candidate-local: an overload candidate has its own substitution, evidence, constraints, and
// applicability result. Candidate order in source files never affects the inferred types.

// 1. Type parameter identity
// --------------------------
//
// A generic parameter is identified by its owner declaration and ordinal. The spelling `T` is not
// an identity. The `T` of a containing class, the `T` of a method, and the `T` of a local lambda
// declaration are independent parameters even if all three names are identical.
//
// Compatible partial type parts refer to one canonical type-parameter sequence. A generic method
// declared on that type has a second, method-owned sequence. Substituting a constructed receiver
// only replaces owner parameters; it cannot capture, overwrite, or infer a method parameter.
//
// Every generic parameter in a candidate must receive exactly one closed type. An unused type
// parameter is not silently assigned Object, the first constraint, a default type, or a type from
// declaration order. Missing evidence rejects or defers that candidate according to whether an
// unsupported source construct could still provide evidence.

// 2. Evidence directions
// ----------------------
//
// An ordinary value argument supplies an input bound: the actual argument type must be convertible
// to the specialized formal parameter. If a formal parameter is just `T`, the actual type is a
// lower bound for T. Multiple input arguments can therefore contribute multiple lower bounds.
//
// A `ref` or `out` argument supplies exact evidence. The caller must provide writable storage of
// the exact specialized parameter type; no nullable lifting, reference conversion, boxing, numeric
// conversion, or user conversion is permitted. `out var` has no prior storage type and therefore
// supplies no inference evidence by itself. Other arguments or an expected result may determine T.
//
// An expected expression type supplies an output bound. If a generic call has result `T` and the
// surrounding expression requires `String`, T can be inferred as String, then the specialized
// result is checked against that expected type. If the call is used as an argument to another
// generic call, the nested call's expected parameter type may provide this context after the outer
// candidate has been specialized.
//
// An argument can also supply exact structural evidence inside an invariant type application. For
// `Box<T>` and an argument of type `Box<String>`, T is exactly String. A `Box<Derived>` argument
// cannot infer `Box<Base>` even if Derived converts to Base; generic nominal applications are
// invariant.
//
// Evidence from explicit type arguments is fixed before inference. Explicit type arguments bypass
// evidence collection for those parameters, but constraints and final argument conversions still
// apply. A partially explicit generic argument list is valid only where the source grammar permits
// it; omitted parameters are still solved by the normal algorithm.

// 3. Solving a parameter
// ----------------------
//
// Let `L` be the set of lower bounds, `E` the set of exact bounds, and `U` the set of expected-result
// upper bounds for one parameter.
//
// - If E has more than one distinct type identity, inference conflicts.
// - If E has one identity X, X is the only candidate. Every value in L must convert to X and X must
//   convert to every value in U.
// - Without E, candidates are constructed type identities that are reachable from the input
//   evidence by the permitted nominal base/bound graph. When L is nonempty, candidates must accept
//   every lower bound. When U is nonempty, candidates must convert to every upper bound.
// - With only U, the compiler considers the supplied expected identities and selects a unique
//   most-specific one that converts to all other upper bounds. It does not invent a subtype that is
//   absent from the source evidence.
// - The chosen candidate must be the unique minimal candidate under intrinsic conversion. If two
//   incomparable interface/base candidates are equally valid, inference is ambiguous.
// - If no candidate satisfies all bounds, inference conflicts. No numeric widening is introduced
//   by this process; generic type inference does not change the numeric variable conversion rules.
//
// Lower bounds are not combined by spelling, source order, or declaration order. Duplicate
// identities collapse. A nullable type is a distinct inference identity unless the pattern itself
// is a nullable wrapper. For `T?` matched against `String?`, the payload pattern is matched against
// the payload type. For `T` matched against `String?`, T is the nullable type `String?`.
//
// The nullable constructor normalizes nested nullable wrappers. If T is inferred as `String?`,
// substituting T into `T?` still produces one `String?` layer. The inference engine does not create
// an observable `String??` type.

// 4. Structural matching
// ----------------------
//
// Before comparing a pattern and an actual type, apply the fixed receiver and explicit-argument
// substitution simultaneously. Do not recursively rewrite replacement values as a second pass.
//
// Direct parameter occurrence:
//
//     template: T
//     actual:   String
//     result:   input bound T >= String
//
// Nullable occurrence:
//
//     template: T?
//     actual:   String?
//     result:   structural nullable match, then input/exact evidence for T
//
// Nested invariant type:
//
//     template: Map<String, T>
//     actual:   Map<String, int>
//     result:   exact evidence T = int
//
// A nominal pattern can match a constructed base projection of an input. For example, if
// `class Labelled: Named<String>`, an argument of type Labelled can provide evidence for a formal
// `Named<T>`. Each applicable projection is considered. If the same generic base is inherited as
// two different constructed identities, its nested evidence must satisfy both paths; otherwise the
// candidate conflicts.
//
// Expected-result matching runs in the opposite direction. If the generic result pattern is
// `Derived<T>` and the expected result is `Base<String>`, the compiler may project the pattern
// through `Derived<T>`'s substituted bases to `Base<T>` and infer T as String. It then separately
// verifies that the specialized result `Derived<String>` converts to the expected type. An
// incompatible projection path is discarded while other paths are checked. If multiple compatible
// paths yield different inference evidence, the expected type does not choose one arbitrarily and
// contributes no evidence for that projection; other input evidence may still solve the parameters,
// after which the final result conversion is checked. Without other evidence, inference is unresolved.
//
// Arrays, fixed arrays, tuples, nullable wrappers, and nominal generic applications are structurally
// matched by their constructor and ordered element types. Fixed-array length and function parameter
// passing modes must also match. Function effect sets are mathematical sets, not ordered lists;
// inference cannot pair them by enumeration position. A parameter occurring only in a top-level
// callback effect set can be inferred by bounded unordered matching when exactly one complete
// mapping satisfies constraints. Nested function-type sets and ambiguous mappings remain unresolved.
//
// A shape mismatch in a pattern that contains an inferable parameter rejects that candidate. It is
// not treated as successful “no evidence,” because the candidate cannot later make the structure
// compatible without changing the declaration's invariant constructor.

// 5. Generic inheritance projections
// ----------------------------------
//
// Inference projections traverse a bounded graph of constructed nominal bases and type parameter
// constraints. Every edge substitutes the current constructed type's owner arguments. Cycles are
// stopped by constructed type identity. Two paths that arrive at the same constructed identity are
// one projection; two paths with different arguments remain distinct evidence sources.
//
// A type parameter's inline constraint and every matching `where` constraint contribute bounds.
// All constraints participate; the compiler never selects the first one. A bound with an invalid
// or unresolved nominal application produces an indeterminate candidate, not an inferred success.
//
// An actual type parameter can be projected through its own nominal constraints. For `T: Reader<X>`,
// a value whose static type is T may satisfy a formal `Reader<R>` through the bound and constrain R
// from the constructed `Reader<X>` path. This does not claim that T is a heap reference or that a
// ref-like T can be boxed. Final conversions and `allows ref struct` constraints remain separate.
//
// Class-root conversion to `std.core.Object` is considered only where the intrinsic conversion
// rules permit it. Boxing a struct is not an implicit way to satisfy `T: Object`; a ref-like type is
// never boxed. A generic candidate's constraint check may therefore reject an otherwise available
// nominal common base.

// 6. Call-argument mapping and evidence
// ------------------------------------
//
// Argument mapping runs before inference. The mapping already knows each source argument's formal
// parameter, passing mode, expanded-vararg status, and whether the source supplies an array.
// Inference reads that mapping and never changes it.
//
// Positional arguments bind the next eligible parameter in declaration order. Named arguments
// bind by declared parameter name and cannot fill an occupied parameter twice. After a named
// argument skips or reorders a parameter, later positional arguments are allowed only while they
// continue the original declaration order. The detailed rules are in spec/Functions.sol.
//
// A vararg element contributes evidence against its element type. A spread array contributes
// evidence against its element type after verifying the source is an array. A named vararg array
// must match the invariant array type and contributes element evidence. Empty varargs contribute
// no type evidence; if the only occurrence of T is an empty vararg, inference needs another source
// or explicit type argument.
//
// A default parameter is not an inference source. Defaults are declarations evaluated at call time
// after explicit arguments, in parameter order. A default may refer to earlier parameters but it
// cannot cause the call site to infer a missing T from the default expression's own body.
//
// An `out var` declaration is typed after candidate selection. It does not introduce speculative
// type evidence before a unique candidate exists. If other arguments determine T, the selected
// formal parameter supplies the new local's type. A call that relies only on `out var` for T must
// use explicit type arguments or an expected-result constraint.
//
// 7. Lambdas, array literals, and literals
// ----------------------------------------
//
// An explicitly typed lambda parameter can contribute exact or directional evidence when its
// function shape is compared to a candidate function parameter. An untyped lambda parameter is
// contextually typed only after a candidate provides a sufficiently closed expected function type.
// Lambda inference cannot select an overload by first guessing a parameter type from one candidate
// and then reusing that guess for another candidate.
//
// A positional lambda `{ $0 + $1 }` obtains its arity from an expected function type. Because `$0`
// and `$1` have no written type, they provide no independent argument type evidence before that
// expected type exists. A candidate whose type variables occur only in the lambda's expected
// parameter list remains unresolved unless another bound or explicit type argument supplies them.
//
// An array literal `[a, b]` has no inferred element type until its elements produce a unique common
// type or a candidate supplies an expected array type. Every element is checked under the same
// selected element type. An empty array literal `[]` supplies no element-type evidence. It does not
// infer `[]Object` as an arbitrary fallback.
//
// Integer and floating literals first retain their source literal data. When they provide generic
// evidence without a contextual target, their language default type is `int` or `double`. After
// inference, representability is checked against each candidate's specialized type. Literal
// representability does not authorize implicit widening of an already typed numeric variable.
// `quad` literal rounding remains a separate exactness/constant-evaluation rule.

// 8. Constraint validation and candidate selection
// -------------------------------------------------
//
// A candidate's type parameters are inferred independently. After inference, the compiler validates
// all inline bounds, `where` bounds, ref-like anti-constraints, nested nominal applications, and
// `allows ref struct` rules. A failed constraint makes that candidate inapplicable; it does not
// promote a lower-ranked candidate by declaration order.
//
// Inference is not overload ranking. A constraint cannot be chosen as a preferred result type just
// because it is more specific, and a generic candidate is not preferred over an exact non-generic
// candidate with the same conversion ranks. The candidate's final specialized parameter types are
// compared using the language conversion ranking.
//
// If inference has no unique solution, the candidate is rejected for a genuine conflict or remains
// deferred when unsupported expression typing could still provide evidence. If any deferred
// candidate could be applicable and alter the winner, no unique call is reported. Diagnostic
// recovery does not count as a successful selected target.
//
// For a selected candidate, substitutions flow into parameter, return, base, property, indexer,
// checked effect, operator, and receiver types. Generic inference does not change named/default
// argument evaluation order, ref/out storage identity, nullable lifting, or dispatch mode.

// 9. Effects and callable types
// -----------------------------
//
// Checked effects are part of a function type. An ordinary generic call substitutes written effect
// types after inference. Effect parameters are not matched positionally because an effect list is
// an unordered set. Bounded top-level inference accepts a unique complete bijection after
// structural matching and constraints; ambiguous or nested function-type sets remain unresolved.
// RuntimeException and its subtypes remain unchecked and do not add a required effect.
//
// When a generic call returns a function value, the expected function type may constrain the
// generic result and function parameter/result shapes. Parameter passing modes are exact. A
// function that may throw a checked exception cannot be inferred into an expected callback whose
// effect budget excludes it.
//
// A lambda body can add effects only after its contextual function type is known. Candidate-local
// lambda analysis must retain separate flow, effects, and diagnostics for each possible target or
// defer the overload set. Reusing side effects from a rejected candidate is invalid.

// 10. Stable errors and budgets
// -----------------------------
//
// Generic inference is bounded to depth 128, at most 4096 visited constructed inheritance types
// for one projection/closure, and at most 256 distinct bounds for one parameter. The shared type
// specialization workspace separately caps its additional interned identities at 100000. These
// limits are deterministic resource protections, not language-level recursion semantics.
//
// Cancellation is observed during structural traversal, inherited projection, bound validation,
// and candidate selection. Cancellation is propagated to the caller. It is never translated to
// “missing evidence,” a candidate rejection, or a source diagnostic.
//
// The intended diagnostic categories are:
//
// - no applicable candidate: every complete candidate was rejected by mapping, conversion, or
//   constraints;
// - ambiguous candidate: two or more complete candidates have no unique best conversion ranking;
// - unresolved inference: at least one parameter has no evidence and the call cannot be completed;
// - conflicting inference: independent exact/lower/upper evidence has no common unique type;
// - analysis deferred: a supported candidate could depend on an unimplemented expression/effect
//   type or a bounded specialization operation.
//
// The diagnostic source range is the call or the smallest conflicting argument/expected-result
// expression. Candidate ordering is deterministic for inspection, but does not resolve a tie.

// 11. Required positive and negative cases
// ----------------------------------------
//
// The implementation is expected to cover, at minimum, these source shapes:
//
//     func <T> identity(value: T): T = value
//     val number: int = identity(42)
//
//     class Box<T>(val value: T)
//     func <T> unwrap(box: Box<T>): T = box.value
//     val name: String = unwrap(Box("Ada"))
//
//     interface Source<T> { func current(): T }
//     class StringSource: Source<String> { override func current(): String = "x" }
//     func <T> read(source: Source<T>): T = source.current()
//     val current: String = read(StringSource())
//
//     func <T> choose(value: T): T = value
//     val base: std.core.Object = choose("text")
//
//     func <T> update(ref value: T, next: T): T = value
//     var count: int = 1
//     update(ref count, 2)
//
//     func <T> take(values: vararg T): T = values[0]
//     val first: String = take("a", "b")
//
// Negative or deferred cases include conflicting exact ref/out evidence, incompatible invariant
// applications, duplicate paths with incompatible constructed bases, an empty-only generic
// vararg, an empty array literal with no expected element type, unrelated upper bounds, a constraint
// failure after inference, unsupported lambda-only evidence, and cancellation midway through a
// projection graph.
//
// A source diagnostic from a negative example does not mean parsing failed. The test harnesses for
// future semantic verification should assert the selected candidate status, source span, inferred
// arguments, and absence of a made-up fallback identity.
