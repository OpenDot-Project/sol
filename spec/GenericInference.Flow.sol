// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative flow-sensitive evidence rules; candidate-specific flow replay is incomplete.
module source.spec.generic_inference.flow

// Flow-sensitive generic evidence
// ===============================
//
// Generic inference consumes the effective type at a call site. It does not perform flow analysis
// itself, and it cannot turn a possible runtime value into a static type fact. Flow facts are scoped
// to their control-flow point and are supplied only when the expression identity is stable.

// 1. A stable smart-cast value
// ----------------------------
//
// A stable local narrowed by a dominating type test contributes the narrowed type to the call:
//
//     open class Animal { }
//     class Cat: Animal { }
//
//     func <T> echo(value: T): T = value
//
//     func inspect(value: Animal?): Animal {
//         if (value is Cat) {
//             val selected: Cat = echo(value)
//             return selected
//         }
//         return Animal()
//     }
//
// The input occurrence sees Cat inside the successful branch. Outside the branch, value retains its
// declared Animal? type. Inference must not cache the branch-local substitution for a later call.
//
// A checked non-null fact similarly supplies the non-null effective type:
//
//     func <T> same(left: T, right: T): T = left
//
//     func inspect(left: String?, right: String): String {
//         if (left != null) {
//             return same(left, right)
//         }
//         return right
//     }
//
// At the call site, both inputs are String. The null comparison itself is not an additional generic
// argument and the solver does not infer T = String? from the original declaration alone.

// 2. Mutable values and invalidation
// ----------------------------------
//
// An invalidated or unstable local uses its declared type. The solver cannot choose a more specific
// candidate by relying on a previous branch test after a write or escaping capture.
//
//     func <T> replace(left: T, right: T): T = left
//
//     func inspect(value: Animal?, other: Cat): Animal {
//         var current = value
//         if (current is Cat) {
//             current = Animal()
//             return replace(current, other)
//         }
//         return Animal()
//     }
//
// After assignment, current has its declared Animal? type. The earlier Cat fact has ended. If no
// unique common candidate accepts Animal? and Cat, the call is inapplicable or deferred.
//
// A closure capture may invalidate a fact if the closure can write the variable between the test
// and call. The compiler does not execute or speculate the closure while inferring T.
//
//     func <T> useLater(action: () -> (), value: T): T = value
//
// A callback with a visible write to the captured local invalidates a fact according to the
// language's capture/escape rules. When the flow engine cannot prove the callback cannot run before
// the argument read, it must use the declared type or mark the candidate unverified.

// 3. Receiver flow and member generic parameters
// ----------------------------------------------
//
// For a call through a narrowed generic receiver, owner parameters are substituted from the
// constructed receiver before method parameters are inferred.
//
//     open class Store<T> {
//         func <R> map(value: T, transform: (T) -> R): R {
//             return transform(value)
//         }
//     }
//
//     class TextStore: Store<String> { }
//
//     func mapIfStore(value: std.core.Object): int {
//         if (value is TextStore) {
//             return value.map("name", { item: String -> item.byteLength })
//         }
//         return 0
//     }
//
// Inside the branch, receiver projection fixes owner T to String. The argument and candidate-local
// lambda context then determine method parameter R. The owner T and method R are separate identities
// even if both declarations use the same source spelling.
//
// A null-safe receiver uses the non-null constructed owner type only on the invocation branch.
// Safe-result nullability is added after method inference and does not change the owner's generic
// substitution.

// 4. Expected result and flow joins
// ---------------------------------
//
// The local expected result is taken from the expression context after control-flow narrowing at
// that expression. A type expected after a branch join cannot flow backward and revise a call that
// was already analyzed on a branch.
//
//     func <T> create(): T? = null
//
//     func textOrNull(flag: bool): String? {
//         if (flag) {
//             val value: String? = create()
//             return value
//         }
//         return null
//     }
//
// The declared local context gives the call result String? and the return target agrees. If the
// call instead occurs in an expression whose target is only known after an unresolved overload or
// branch join, inference remains deferred until that context is available.
//
// A smart cast can narrow the actual argument while the expected result supplies an independent
// upper bound. Both are recorded; a conflict is reported rather than resolving one by declaration
// order.

// 5. Safe-call evidence
// ---------------------
//
// Safe access preserves the call's argument and expected-type rules on its non-null execution
// branch. Its outer nullable result does not provide evidence for an unrelated method parameter.
//
//     class Registry {
//         func <T> find(key: String): T? = null
//     }
//
//     func maybeFind(registry: Registry?): String? {
//         return registry?.find("primary")
//     }
//
// The expected String? result is matched against the method's T? result pattern, which can infer
// T = String. The null receiver branch does not supply a runtime value of T; inference comes from
// the local expected type. If the call has no expected target and no other T evidence, it remains
// unresolved.
//
// Explicit arguments are evaluated on the non-null branch after candidate context is determined.
// The solver may inspect their types and source syntax but never executes them while comparing
// overload candidates.

// 6. Branch-specific candidates
// -----------------------------
//
// Each overload candidate receives the same incoming flow snapshot and an isolated candidate
// context. A failed candidate cannot mutate the flow state seen by another candidate.
//
//     class Handler {
//         func <T> process(value: T): String = ""
//         func process(value: Cat): String = ""
//     }
//
//     func dispatch(handler: Handler, value: Animal?): String {
//         if (value is Cat) {
//             return handler.process(value)
//         }
//         return ""
//     }
//
// The flow snapshot at the call says Cat. Every candidate is checked from that same snapshot;
// comparing the generic candidate cannot consume or weaken the Cat fact before the concrete
// overload is examined. Final overload ranking follows the ordinary conversion and specificity
// rules.
//
// An overload's lambda body, default expression or conversion can have effects, but those effects
// are committed only for the selected candidate. Candidate exploration is not runtime execution.

// 7. Diagnostics and serialized results
// --------------------------------------
//
// Analysis output distinguishes a declared type from an effective flow type and records which
// program point supplied the refinement. This evidence remains internal/source-oriented; debug
// serialization must not disclose machine-local paths or unstable object addresses.
//
// The following outcomes are distinct:
//
// - a closed inference using a proven narrowed type;
// - a missing bound because the declared type is too broad;
// - an invalidated-flow candidate using the declared type;
// - an ambiguous common type despite a valid narrowing;
// - a deferred result because a candidate-specific lambda context is unavailable;
// - an unverified result because the flow proof exceeds its analysis budget.
//
// Flow cancellation propagates and does not publish a partial type map. Resource exhaustion is not
// interpreted as a successful smart cast. An error type cannot become generic evidence.

// 8. Implementation boundary
// ---------------------------
//
// Source analysis currently resolves a limited set of receiver and generic call expressions. Full
// smart-cast stability, mutation/capture invalidation, path-sensitive definite assignment,
// candidate-local lambda replay, safe-call execution and verified FIR remain pending. A documented
// flow example is a language contract, not a claim that the current analysis API proves it.
