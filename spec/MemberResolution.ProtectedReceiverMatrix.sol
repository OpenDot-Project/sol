// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative protected receiver matrix; foreign binary access metadata remains pending.
module source.spec.member_resolution.protected_receiver_matrix

// Protected receiver decision matrix
// ===================================
//
// An instance protected member is accessible only when both the lexical caller and the source
// receiver satisfy the access rule. The member's declaring owner, the lexical current type, and
// the receiver's effective static type are three different identities and must be analyzed as such.

// Access-check inputs
// -------------------
//
// - `member owner`: the type declaration that owns the selected method/property contract after
//   member lookup and constructed-base substitution.
// - `current type`: the lexical containing class/struct/object declaration of the access expression.
// - `receiver type`: the source expression's static type after only proven non-null/type narrowing.
// - `member route`: the constructed path that made the selected declaration available.
// - `operation`: read, write, call, compound update, indexer access, or a ref/out location request.
//
// Private declaration ownership is checked independently. A public child accessor cannot make a
// protected property callable from a top-level function. A valid receiver type cannot make an
// unrelated caller a derived caller.

// Derived relationship
// --------------------
//
// Starting from the lexical current type, traverse constructed direct bases and interface bounds
// under a depth budget. The relationship is established only when a reached nominal declaration
// identity equals the protected member's owner declaration. Constructed arguments remain attached
// to each path for signature specialization; they do not change nominal derivation identity.
//
// An unresolved or resource-limited path is not proof of derivation. The check remains incomplete
// and the access site is unverified. It must not treat matching simple names or module aliases as
// derived identity.
//
// An access from inside the declaring type itself does not require a derived receiver restriction.
// For access from a derived type, apply the receiver rule to the source expression. `super` is the
// explicit base-dispatch form and is permitted where the language allows a base call.

// Receiver categories
// -------------------
//
// | Source form | Receiver evidence | Protected instance access |
// |---|---|---|
// | `this.member` | Current constructed type | Allowed for owner/current derived relation |
// | `super.member` | Explicit base path | Allowed through the declared base route |
// | local `Derived` | Local's effective static type | Allowed when Derived is current type or subtype |
// | local `Base` with runtime Derived value | Static Base type only | Rejected unless flow narrows to permitted subtype |
// | nullable `Derived?` with `?.member` | Non-null Derived branch | Access checked on Derived branch |
// | nullable `Derived?` with plain `.member` | No non-null proof | Nullable diagnostic is handled first |
// | unrelated sibling `OtherDerived` | Static OtherDerived | Rejected from the current derived body |
// | type qualifier `Derived.member` | Type, no instance storage | Does not fabricate an instance receiver |
// | module qualifier | Module namespace | Not an instance receiver |
// | captured local in nested lambda | Captured local's proven effective type | Allowed only if lexical access context is preserved |
// | receiver with unresolved type | No stable receiver identity | Defer; do not assume permission |

// Receiver rule example
// ---------------------
//
//     open class Vehicle {
//         protected open func inspect(): String = "vehicle"
//     }
//
//     class Car: Vehicle {
//         func own(): String = this.inspect()
//         func base(): String = super.inspect()
//         func peer(other: Car): String = other.inspect()
//         func broad(other: Vehicle): String = other.inspect()
//     }
//
// `own` and `base` have explicit valid receiver routes. `peer` is valid because the receiver's
// static type is Car. `broad` is not permitted solely because `other` might hold a Car at runtime.
// A flow test that establishes `other is Car` can narrow the stable value within the true branch.
//
//     func outside(value: Car): String = value.inspect()
//
// A top-level function is not a derived lexical caller, even though its receiver is Car.

// Flow narrowing
// --------------
//
// A successful type test may produce a branch-specific receiver identity for a stable local. The
// protected check consumes that effective type only within the branch where the proof is valid.
// A reassignment, ref/out call, mutable capture, or other specified invalidation removes the
// narrowed type before a later access.
//
// Null narrowing and derived narrowing are separate facts. A non-null `Vehicle` is not a `Car`; a
// value narrowed to `Car?` remains nullable until a non-null check or safe access chooses the
// non-null branch. Safe access does not itself prove that the result is present outside the call.
//
// A mutable field is not a stable local unless the flow contract explicitly proves its stability.
// The compiler cannot keep a receiver type test across arbitrary calls that may replace the field.

// Generic owner substitution
// --------------------------
//
// For `Base<T>` declaring protected `read(): T`, and `Derived: Base<String>`, lookup specializes
// the callable signature to String. Accessibility still uses the source receiver type Derived.
// The owner substitution and the receiver-access proof must not be conflated.
//
// For `Derived<T>: Base<T>`, the base edge carries the exact owner T identity through the path.
// A method-level T is a separate generic parameter even when it has the same spelling. Constraint
// projection must prove the declaring owner before an inherited protected candidate is eligible.
//
// If multiple generic bounds expose protected methods, each member candidate retains its own owner
// path. A same-name member from an unrelated bound cannot borrow the permission established for a
// different bound. Conflicting constructed owner paths remain a declaration/candidate issue.

// Method and property operations
// ------------------------------
//
// A protected method call checks access against the actual receiver expression after member group
// formation. An inaccessible candidate cannot win overload resolution or contribute generic
// evidence. It does not erase a visible candidate with a distinct source declaration.
//
// A protected property read checks the getter operation. A write checks the setter/init operation.
// A compound update checks both. An accessor-specific modifier can narrow access further but cannot
// widen the property declaration's access. The getter and setter each retain their own effects.
//
// An indexer uses the source collection receiver for protected access. Index argument type, a named
// index argument, and a vararg index parameter do not change receiver permission. Ref/out requires
// an explicit addressable location contract; a getter/setter pair is not addressability.

// Generic lambda and callback calls
// ---------------------------------
//
// Candidate-local lambda inference may determine an argument or effect type, but it cannot change
// the receiver expression's static type. An inferred callback parameter T equal to Derived is not
// evidence that a `Base` call receiver is Derived.
//
// Nested lambdas retain the lexical source owner for access checking only as prescribed by closure
// capture rules. Passing a method reference or constructing a function value does not transfer
// protected permission to a top-level caller. The selected callback still performs ordinary access
// checks at its declaration body and ordinary effect checks at its invocation site.

// Static/member categories
// ------------------------
//
// Sol has no `static` modifier in this contract. Companion members and object members follow their
// declared receiver category. A companion/type-qualified function does not become a protected
// instance member call. An object singleton expression may be an ordinary receiver if its nominal
// inheritance model exposes the selected instance declaration.
//
// A nested type name, module name, import alias, and local variable shadow are resolved by source
// name analysis before protected access. Access failure must not retry another name category.

// Decision table
// --------------
//
// | Caller derives from owner | Receiver is this/super/allowed derived | Result |
// |---:|---:|---|
// | no | no | Reject access |
// | no | yes | Reject access |
// | yes | no | Reject access |
// | yes | yes | Continue with operation/accessor checks |
// | unresolved | yes | Defer or report incomplete proof |
// | yes | unresolved | Defer or report incomplete proof |
// | owner is current type | any legal receiver in owner scope | Continue with ordinary visibility |
//
// The table applies to the instance protected operation only. It does not replace private,
// internal, public, nullable, accessor, overload, or ref-like lifetime checks.

// Required invariants
// -------------------
//
// - Lexical derivation and receiver derivation are separate proof obligations.
// - The selected member's declaring owner is not substituted for the source receiver type.
// - A runtime possibility is not static-type proof.
// - A valid `super` route is explicit and does not authorize arbitrary base-typed locals.
// - Nullable handling does not grant protected permission.
// - Generic inference does not change receiver access.
// - Accessor visibility is operation-specific.
// - A failed access does not expose a hidden base fallback.
// - Budget exhaustion is incomplete, not permission.
// - Access checking never executes the target.

// Implementation boundary
// ------------------------
//
// The current body binder checks protected method calls and property receivers against the
// lexical-derived and effective-static-receiver model within a source analysis. Cross-distribution
// access metadata, every interface-default case, closure serialization, and full flow narrowing
// remain pending. The syntax examples are specification inputs, not an implementation-completeness
// claim.
