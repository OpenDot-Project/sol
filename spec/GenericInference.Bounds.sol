// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative generic bound aggregation; complete candidate-local implementation is pending.
module source.spec.generic_inference.bounds

// Bound aggregation examples
// ==========================
//
// The implementation treats every source of evidence as a constraint on one candidate-owned
// parameter identity. This file spells out the intended results for multiple inputs, expected
// types, nullable structure, base projection, and ref/out.

// One direct input
// ----------------
//
//     func <T> identity(value: T): T = value
//
//     val result = identity(42)
//
// The integer literal contributes its default type `int` absent an expected target. The selected
// candidate specializes T to int. A later assignment to `long` does not retroactively change this
// inference if the call was already fixed in a context without expected result evidence.

// Multiple inputs with one nominal join
// --------------------------------------
//
//     interface Named { func name(): String }
//     interface HasId { func id(): int }
//
//     open class Entity: Named {
//         open func name(): String = "entity"
//     }
//
//     class User: Entity, HasId {
//         override func name(): String = "user"
//         func id(): int = 1
//     }
//
//     class Service: Entity {
//         override func name(): String = "service"
//     }
//
//     func <T> common(left: T, right: T): T = left
//
//     val entity = common(User(), Service())
//
// If Entity is the unique most-specific constructed common candidate accepted by both inputs, T is
// Entity. The inference result is not selected from whichever argument appears first. If two
// unrelated interfaces are equally valid common candidates and no unique type is selected by the
// source bounds, the call is ambiguous or deferred; it does not use alphabetical interface name.

// Nominal interface projection
// -----------------------------
//
//     interface Codec<T> { func encode(value: T): String }
//
//     class TextCodec: Codec<String> {
//         override func encode(value: String): String = value
//     }
//
//     func <T> encodeWith(codec: Codec<T>, value: T): String {
//         return codec.encode(value)
//     }
//
//     val encoded = encodeWith(TextCodec(), "hello")
//
// `TextCodec` projects to `Codec<String>`, so the first argument contributes T = String. The second
// argument independently contributes the same lower bound. The exact constructed base argument
// remains String; the compiler does not strip generic argument identity from the projection.

// Multiple constructed paths
// --------------------------
//
//     interface Source<T> { func current(): T }
//     interface NumericSource: Source<int> { }
//     interface DisplaySource: Source<String> { }
//
//     class MixedSource: NumericSource, DisplaySource { }
//
//     func <T> read(source: Source<T>): T = source.current()
//
// The source declaration graph that permits two different constructed `Source` applications is
// itself a conflicting/invalid base contract. Inference preserves both projections. It must not
// choose `int` or `String` from whichever base interface was visited first. The member lookup for
// `current` and the call inference both retain enough constructed identity to diagnose the source.

// Invariant nested applications
// -----------------------------
//
//     class Box<T>(val value: T)
//
//     func <T> takeBox(box: Box<T>): T = box.value
//
//     val exact = takeBox(Box("text"))
//
// T is exactly String. If one input is Box<String> and another is Box<Object>, one T cannot satisfy
// both exact bounds because Box is invariant. A common Object supertype of String and Object does
// not make `Box<String>` convertible to `Box<Object>`.

// Nested nullable structure
// -------------------------
//
//     func <T> unwrap(value: T?): T = value!!
//
//     val fromNonNull: String = unwrap("text")
//     val fromNull: String? = unwrap(null)
//
// The non-null argument contributes String to the payload parameter. A null literal has nullable
// bottom type and does not prove an arbitrary non-null T by itself. An expected result of String?
// can provide an upper bound, but T is still resolved under the nullability and lower-bound rules;
// there is no default T = Object. A later implementation may explicitly type a null literal from an
// expected `T?` target, but it must record that as expected evidence rather than inventing a type.

// Expected result evidence
// ------------------------
//
//     func <T> emptyValue(): T? = null
//
//     val text: String? = emptyValue()
//
// The expected type constrains the return pattern. The resulting candidate is String, and the call
// result is String?. In a call to `emptyValue()` with no expected type, T has no evidence and
// inference fails. It does not pick String? because that type happens to be used by a nearby
// declaration.
//
//     open class Message { }
//     class TextMessage: Message { }
//     class AlertMessage: Message { }
//
//     func <T> create(): T = default<T>()
//
//     val message: Message = create()
//
// The expected result is Message, and the generic result pattern is T. This is an upper-bound
// solution. If a generic result has a derived declaration pattern, such as Derived<T> with expected
// Base<String>, the solver first projects Derived<T> to Base<T>, infers String, then checks that
// Derived<String> converts to Base<String>.

// Exact ref/out evidence
// ---------------------
//
//     func <T> replace(ref target: T, source: T): T {
//         target = source
//         return target
//     }
//
//     var total: int = 1
//     replace(ref total, 2)
//
// The ref location supplies exact T = int. The value argument is checked against int. A `long`
// local cannot bind through a conversion or be narrowed because the function happens to accept an
// integer literal elsewhere.
//
//     func <T> read(out target: T, prototype: T): T {
//         target = prototype
//         return target
//     }
//
//     val prototype: String = "default"
//     read(out var loaded, prototype)
//
// `out var loaded` contributes no type before selection. `prototype` infers T as String; after the
// candidate is uniquely selected, loaded is declared String and becomes definitely assigned on the
// normal continuation.

// Multiple exact bounds
// ---------------------
//
//     func <T> exchange(ref left: T, ref right: T): () { }
//
//     var count: int = 0
//     var index: long = 0
//     exchange(ref count, ref index)
//
// This call has conflicting exact identities. The compiler does not infer a common base for
// writable storage and does not create a temporary copy of either location.

// Constraints after inference
// ---------------------------
//
//     interface Identified { func identifier(): String }
//     func <T: Identified> identifier(value: T): String = value.identifier()
//
//     class User: Identified { func identifier(): String = "user" }
//     val id = identifier(User())
//
// The input infers T = User, then the constraint is validated against User's constructed base
// graph. If an input type does not implement Identified, this candidate is rejected. Constraint
// failure does not substitute the bound Identified for the input type; doing so would change the
// identity of the parameter and potentially make an invariant argument appear applicable.
//
//     func <T: std.core.Object> boxed(value: T): T = value
//
// A value-type input cannot satisfy this Object constraint by boxing. An `allows ref struct`
// anti-constraint is also checked independently from nominal bounds; it is not an inference hint.

// Distinct owner parameters
// -------------------------
//
//     class Cache<T>(val value: T) {
//         func <T> replace(next: T): T = next
//
//         func example(): () {
//             val ownerValue: T = this.value
//             val methodValue: String = this.replace("replacement")
//         }
//     }
//
// The receiver's T is fixed by `Cache<T>`. The method's T is inferred as String. Parameter spelling
// cannot make the maps overlap. An incomplete owner substitution remains visible in the method's
// specialized signature and cannot be treated as a closed native type.

// Expected type must be local to the expression
// ----------------------------------------------
//
//     func <T> first(value: T, fallback: T): T = value
//
//     val selected: std.core.Object = first(User(), Service())
//
// The call may use its local expected result in addition to its two argument bounds. It cannot use
// another statement's expected type or search future uses of the result variable to revise an
// already selected type. Dataflow does not retroactively change a generic call's type identity.

// Error cases that preserve evidence
// ----------------------------------
//
// A candidate must preserve the source of each bound so the diagnostic can identify the smallest
// conflict. These cases must not collapse into one vague “could not infer” result:
//
// - two different exact ref/out type identities;
// - exact identity incompatible with an ordinary lower bound;
// - lower bound that cannot convert to the exact expected result;
// - invariant type argument mismatch;
// - multiple projected bases with different constructed arguments;
// - expected result that cannot be reached by the specialized return type;
// - unsatisfied constraint after all evidence is solved;
// - no evidence because the parameter appears only in an empty value/literal/lambda/effect;
// - two equally specific constructed common interfaces;
// - resource-limit exhaustion during projection.
//
// A malformed/indeterminate type is different from a real conflict. It remains unverified and
// cannot be used as a successful type argument. The analysis JSON must retain candidate status and
// reason without serializing private source paths.
