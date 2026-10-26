// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative nullable receiver contract; complete control-flow proof and safe-call lowering are pending.
module source.spec.member_resolution.nullable_flow

// Nullable receiver flow and member access
// ========================================
//
// Member lookup consumes a receiver type plus a flow fact. A nullable source type is never treated
// as non-null merely because its member name exists on the underlying declaration. Flow narrowing,
// safe access, and explicit assertion are three distinct operations with distinct result and effect
// behavior.

// 1. Lookup input
// ---------------
//
// Before resolving a member, the binder determines:
//
// - the expression's declared/static type;
// - whether that type is nullable;
// - the flow fact that is valid at the exact program point;
// - whether the receiver is a type, object, instance, module or callable value;
// - whether an addressable storage location exists for ref/out or assignment.
//
// A successful member name lookup does not establish a non-null flow fact. In particular,
// overload discovery cannot inspect a runtime value or infer that an object is non-null from a
// method that would be useful on it.
//
//     class Session { func close(): () { } }
//
//     func closePlain(session: Session?): () {
//         session.close()
//     }
//
// This expression is invalid without a dominating proof. The existence of Session.close does not
// make the nullable receiver safe.

// 2. Proven non-null receivers
// ----------------------------
//
// A flow fact narrows only the expression whose identity is proven stable. A successful type test
// or non-null comparison refines the value within its true branch:
//
//     func closeIfOpen(session: Session?): () {
//         if (session != null) {
//             session.close()
//         }
//     }
//
// Inside the body, the local read denotes Session. After the branch, the original nullable type is
// restored unless both control-flow paths independently prove the same fact.
//
//     func render(value: Object?): String {
//         if (value is String) {
//             return value.toString()
//         }
//         return "unknown"
//     }
//
// The successful type test narrows this stable local read to String. A failed test does not produce
// a successful member lookup on String.
//
// A branch join retains a fact only if every reachable incoming edge proves it. An early return,
// throw, bottom expression or exhaustive branch removes its edge from the join. Unreachable code
// does not contribute a contradictory receiver type.

// 3. Stable and mutable storage
// -----------------------------
//
// A stable local val can retain its flow fact while the local continues to refer to the same value.
// A mutable local loses a fact when an assignment, aliasing write, captured closure, or control-flow
// edge may replace its value before the member read.
//
//     func inspect(value: Session?): () {
//         if (value != null) {
//             value.close()
//             value = null
//             // value.close() is invalid here.
//         }
//     }
//
// A property getter is an operation and may return a different value on each read. The compiler
// cannot reuse a null or type-test fact across independent property evaluations unless the language
// contract identifies that property as stable storage.
//
//     class Holder(var current: Session?)
//
//     func inspect(holder: Holder): () {
//         if (holder.current != null) {
//             // A second getter call is not proven non-null.
//             holder.current.close()
//         }
//     }
//
// Capture and concurrency rules are conservative. A write by a closure or another thread that may
// occur between check and read invalidates the refinement. No synchronization is inferred from a
// source type test.

// 4. Safe access
// --------------
//
// The safe access operator evaluates its receiver exactly once. If the receiver is null, the
// selected property/function/indexer operation is skipped. If non-null, member lookup and
// invocation use the underlying receiver type.
//
//     func display(session: Session?): String? = session?.name()
//
// The result is nullable even when name returns String. The safe access does not mutate the static
// type of session after the expression.
//
//     class Profile {
//         var nickname: String? = null
//         func update(value: String): () { }
//     }
//
//     func updateIfPresent(profile: Profile?): () {
//         profile?.update(nextName())
//     }
//
// nextName is evaluated only on the non-null path. It is evaluated once, after evaluating profile.
// If profile is null, neither explicit arguments, omitted default expressions nor the target call
// are evaluated.
//
// Receiver evaluation precedes all explicit argument evaluation. Explicit arguments are evaluated
// once in source order on the non-null branch, after candidate selection has provided their
// contextual types. Omitted defaults are evaluated on that branch after explicit arguments and in
// parameter declaration order. A candidate rejected during lookup cannot cause source evaluation.

// 5. Result nullability
// ---------------------
//
// Safe access adds one nullable layer to a non-null result. If a member result is already nullable,
// normalization keeps one nullable layer while retaining both possible reasons for null.
//
//     class Catalog {
//         func find(key: String): String? = null
//     }
//
//     val catalog: Catalog? = null
//     val result: String? = catalog?.find("key")
//
// result can be null because catalog is null or because find returns null. The type system does not
// expose nested String??.
//
// A safe member returning bottom has no non-null continuation. The null receiver path can still
// produce null:
//
//     class Aborter { func stop(): ! { throw RuntimeException("stop") } }
//     func maybeStop(value: Aborter?): ()? = value?.stop()
//
// The expression either skips and yields null or does not return. It cannot manufacture a normal
// non-null value of the bottom type.
//
// For safe access returning a tuple, array, function, struct or generic result, the same nullable
// constructor applies to the complete result type. It does not rewrite elements, function
// parameters, generic arguments or declared effects.

// 6. Safe chains and grouping
// ---------------------------
//
// Each safe operator guards the operation immediately associated with that syntax segment.
// Parentheses preserve the result type and guard boundary.
//
//     class Address { val city: String }
//     class User { val address: Address? }
//
//     func city(user: User?): String? = user?.address?.city
//
// The first safe access may produce a nullable Address; the second safely reads city. The receiver
// expression for each segment is evaluated once. The normalized result is String?.
//
//     func invalid(user: User?): String? = (user?.address).city
//
// Parentheses expose a nullable intermediate value. The following ordinary .city requires flow
// proof, another safe operator, or assertion; an earlier ?. does not guard arbitrary later syntax.
//
// A chain cannot duplicate a receiver by rewriting the source expression into a test plus a second
// evaluation. Temporary storage is an implementation detail and must preserve identity, lifetime,
// exceptions and effects of one source evaluation.

// 7. Properties, indexers and storage
// ----------------------------------
//
// Safe property access invokes the getter only on the non-null branch. Its result is a value, not a
// writable location.
//
//     class Box { var count: int }
//     func readCount(box: Box?): int? = box?.count
//
// The following operations are invalid because a nullable safe result has no single stable storage
// location:
//
//     // box?.count = 1
//     // swap(ref box?.count, ref other)
//     // read(out box?.count)
//
// An ordinary property with a proven non-null receiver can still be non-addressable. Null flow
// proof does not turn a getter/setter pair into a field reference. Ref/out requires the property's
// explicit ref-return contract, if any.
//
// An indexer applies the same receiver guard and storage rules. The receiver and index arguments
// are evaluated once; index evaluation is skipped when the safe receiver is null.

// 8. Overloads, generics and expected types
// -----------------------------------------
//
// On a proven non-null or safe receiver, member candidates are collected from the underlying
// constructed type and its declared bounds. The nullable wrapper is not an extra nominal base.
//
// Generic inference uses the candidate's substituted member signature after receiver narrowing.
// A nullable receiver fact does not become a type argument unless the formal member signature has a
// corresponding nullable occurrence. A safe call lifts the completed member result after
// candidate-specific inference.
//
//     class Store<T> {
//         func get(key: String): T? = null
//     }
//
//     func read(store: Store<String>?): String? = store?.get("primary")
//
// The receiver is Store<String> on the invocation branch, and its owner substitution remains
// String. Safe access then preserves the nullable result.
//
// An expected result may check the normalized safe result. It cannot remove the null path or select
// a non-null overload solely because the enclosing expression expects a non-null type.

// 9. Assertions and fallback operators
// ------------------------------------
//
// The non-null assertion operator evaluates its operand once. A null value raises the specified
// null assertion RuntimeException; a non-null value produces the underlying type and can be used as
// an ordinary receiver.
//
//     func requiredName(value: User?): String = value!!.name
//
// The assertion does not prove that another read of a mutable variable remains non-null. Its proof
// belongs to the single evaluated expression.
//
// The Elvis operator evaluates its left operand once. Its right expression is evaluated only if the
// left result is null. If the right expression has bottom type, the non-null left path determines
// the result type.
//
//     func requireUser(value: User?): User = value ?: fail("missing")
//     func fail(message: String): ! { throw RuntimeException(message) }
//
// Elvis is a value-producing branch, not a member lookup fallback. A member missing from the
// non-null left type remains missing.

// 10. Effects and exceptions
// --------------------------
//
// Member selection itself does not execute the receiver. The safe invocation's checked effects are
// included only for the operation that executes on the non-null path, but the resulting expression
// remains subject to the surrounding checked-effect contract.
//
// If the member is a getter, its effects belong to the getter invocation. A skipped getter
// contributes no runtime throw on the null path, but static checked-effect accounting records the
// possible operation according to the effect-set contract.
//
// Null assertion failure is a RuntimeException and therefore unchecked. A user-defined conversion,
// operator, getter, setter, indexer or function can add its own declared checked effects.
//
// Effect analysis is independent from nullable result lifting. A nullable result does not erase a
// declared exception and a handled exception does not establish a non-null receiver fact.

// 11. Error precedence and diagnostics
// ------------------------------------
//
// Diagnostics identify the earliest semantic operation that cannot be justified:
//
// - an ordinary nullable member access points to the member operator/name;
// - a missing member points to the member name after receiver classification;
// - inaccessible access points to the selected declaration and call site;
// - an invalid safe storage use points to the assignment/ref/out operation;
// - a failed type test or unreachable branch is reported by control-flow analysis;
// - unresolved or cancelled flow remains unverified, never silently treated as non-null.
//
// Nullable failure does not trigger fallback lookup in a module, a type name or a base declaration
// with a different receiver. A diagnostic recovery candidate is never a successful binding.

// 12. Implementation boundary
// ---------------------------
//
// Nullable type normalization and a subset of safe-call result analysis exist in the development
// source analyzer. Full stable-variable flow proofs, invalidation, definite assignment, effect
// coverage, closure/capture concurrency rules, safe-call execution and runtime exception behavior
// remain separate implementation work. Parsing ?. or producing a nullable result type does not
// certify those behaviors.
