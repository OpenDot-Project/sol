// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative receiver lookup contract; BodyAnalysis implements a bounded subset.
module source.spec.member_resolution

// Receiver-dependent member resolution
// =====================================
//
// This file defines how an expression receiver determines the set of declarations visible after
// a dot. It is a source-language rule. The internal implementation may use symbols, substitutions,
// snapshots, or other compiler-owned data, but none of those implementation identities change the
// behavior described here.
//
// The following examples in this file are specifications, not a promise that every operation can
// currently be lowered to an executable artifact. The implementation ledger lives in
// docs/body-analysis.md and docs/status.md.

// 1. The receiver is evaluated before member lookup
// ------------------------------------------------
//
// For `receiver.member`, first analyze and evaluate `receiver` exactly once. Its static type, its
// value-versus-type category, its nullable state, and its source location form the lookup input.
// The compiler must not infer the receiver from the member name or search unrelated module symbols
// merely because the receiver lookup fails.
//
// A local variable or parameter named `network` shadows a module alias `network`. In
// `network.Client`, the compiler resolves `network` as a local value if one is in lexical scope. A
// local value is never reinterpreted as a module qualifier after a missing member is observed.
// Module qualification belongs to the import/name resolver and succeeds only when the first path
// segment is not shadowed by a local declaration.
//
// The receiver is evaluated before argument expressions in a call. A call such as
// `makeClient().connect(host = nextHost())` evaluates `makeClient()` first, then each explicit
// argument in source order. Candidate discovery, generic inference, overload ranking, and
// accessibility checks do not re-evaluate those source expressions.
//
// `super` is a receiver designator rather than an ordinary local. Its lookup root is the selected
// direct base contract for the current instance. It does not mean “search all ancestors and pick a
// convenient declaration.” A base interface default that competes with another default must be
// resolved by the declaration rules below.

// 2. Receiver categories
// ----------------------
//
// An instance expression searches the nominal type of the expression after applying the current
// generic substitution. A type-qualified expression searches the companion object associated with
// that type. An object expression searches the singleton object's members. There is no `static`
// member category and no implicit conversion from a type name to an instance.
//
// A type name may qualify a companion member only. An ordinary instance member accessed through a
// type name is an error; the compiler must not fabricate a default receiver. A companion is a
// distinct object declaration and has its own generic and visibility rules. The containing type's
// generic arguments are substituted into the companion's members where the declaration binds
// those parameters.
//
// A module alias, import qualifier, and type qualifier are distinct names. `fmt.format`,
// `Formatter.defaultWidth`, and `formatter.format` may refer to a module function, a companion
// member, and an instance member respectively. Their resolution is based on the syntax and symbol
// category, not on spelling conventions.
//
// A nullable receiver `value: T?` requires a proven non-null flow fact, safe access `value?.member`,
// or the explicit non-null assertion `value!!`. A failed proof is reported at the member token.
// Safe access evaluates the receiver once, skips member arguments and invocation when the receiver
// is null, and lifts a non-null result to `R?`. A result already nullable remains one nullable
// layer. A safe access is not a writable location and cannot be passed as `ref` or `out` storage.
//
// The intrinsic `length` member on managed and fixed arrays is not a declaration lookup and is
// read-only. It has type `int`; safe access to it has type `int?`. Other collection-like types use
// ordinary declared properties or indexers. The compiler does not infer a `length` property for an
// arbitrary user type.

// 3. Constructed inheritance paths
// --------------------------------
//
// Every edge in a receiver's base graph is traversed after substituting the current constructed
// type arguments. For `class Leaf<U>: Mid<[]U>` and `class Mid<T>: Root<T>`, a `Leaf<int>` lookup
// reaches `Root<[]int>`, not the open declaration `Root<T>` and not the unconstructed declaration
// `Root`.
//
// The substitution is composed edge by edge. It is simultaneous within one edge: a replacement is
// not recursively rewritten as if it were a second source mapping. At the next inheritance edge,
// the newly constructed parent becomes the receiver and supplies the next substitution.
//
// A path records constructed type identity, not only the declared type symbol. The same declaration
// reached as `Root<int>` and `Root<String>` is two different contracts. If both paths expose the
// same member name and the invariant parameter substitutions disagree, the name is ambiguous. The
// compiler must not discard one path because its spelling or source file sorts later.
//
// A repeated path that reaches the same declaration on the same constructed owner is one lookup
// candidate. This collapses a diamond that reuses a shared base exactly once. It does not collapse
// different interface declarations that happen to have matching text, matching signatures, or
// matching source locations.
//
// The standard class root `std.core.Object` participates only where the class model supplies it.
// Value types do not acquire reference conversions or interface storage through member lookup.
// Ref-like receiver members remain instance operations on the original value; lookup does not box
// a ref struct to make a member callable.

// 4. Name groups and inherited overloads
// -------------------------------------
//
// A declaration name is classified as a callable overload group, one non-callable declaration, or
// an ambiguous mixture. Functions with the same source name but distinct signatures form an
// overload group. Overload identity is method name, method generic arity, ordered parameter types,
// and ordered passing modes. Return type, checked effects, parameter names, defaults, and generic
// constraints do not create another overload identity.
//
// An inherited callable group includes accessible declarations from the receiver and its base
// contracts. Overload resolution evaluates the whole applicable set after type substitution. The
// compiler must not choose the closest declaration before it has compared applicable signatures.
// A base overload with a different signature can remain a candidate beside a derived overload.
//
// A declaration marked `override` replaces the inherited candidate with the same generic arity,
// parameter passing modes, and substituted parameter types. Its return type, visibility, and
// checked effect contract are validated separately. After replacement, calls dispatch through the
// selected override slot while retaining the static declaration that supplies parameter names and
// default expressions.
//
// A same-signature declaration without `override` is still a declaration error. It must not make
// source order decide which implementation wins. The compiler reports the required override and
// uses the most-derived declaration only as a recovery choice for continued diagnostics.
// Recovery binding is never a successful compilation result.
//
// Two distinct inherited functions with the same applicable signature do not become one method by
// structural coincidence. If no declaration overrides both paths, a call that cannot distinguish
// them is ambiguous. A declaration that explicitly resolves the inherited contract must provide a
// valid override relationship to every matched base member.
//
// A nearer property, nested type, enum variant, indexer, or other non-callable value owns its name
// at that inheritance depth. A base declaration hidden by a nearer value is not a fallback for an
// inaccessible or ill-typed access. A more-derived callable overload group hides an inherited
// non-callable declaration with the same name; the hidden value does not reappear after the call
// fails to find an applicable method. Mixed declarations at the same effective depth are ambiguous
// and are diagnosed rather than selected by declaration order.
//
// Lookup does not expose constructors as ordinary inherited members. A constructor is selected
// from the constructed type declaration. `this(...)` and `super(...)` constructor delegation have
// their own target rules and initialization flow. The constructor name is not a property-access
// alias.

// 5. Generic receiver substitution
// -------------------------------
//
// Class/type arguments and method arguments have different owners even when both are named `T`.
// The receiver substitution is applied to inherited owner members first. Generic method inference
// then solves the method's own parameter identities. A method's `T` never overwrites its owner's
// `T` merely because the names match.
//
// Consider the following intended contract:
//
//     interface Reader<T> { func read(): T }
//     class Base<U>: Reader<[]U> { override func read(): []U = [] }
//     class Child<V>: Base<V> { }
//
// A `Child<int>` receiver exposes `read(): []int`. The body of a generic `Child<V>` exposes
// `read(): []V`. Both facts come from the same declaration path with different substitutions.
//
// Generic methods inherited through a generic base keep both substitutions. A method declared as
// `func <R> convert(value: U): R` on `Base<U>` viewed through `Child<int>` has parameter type
// `int`; its method parameter `R` remains inferable at the call site.
//
// Constrained type parameter receivers search every nominal constraint. For `T: Readable,
// T: Closeable`, members from both constraints are available. A member declared on the shared same
// constructed base is deduplicated. Distinct declarations with the same name follow the ordinary
// overload/ambiguity rules. The constraint list's source order cannot select a winner.
//
// A bound that cannot be resolved or whose specialization exceeds a resource limit is not treated
// as a successful empty set. The affected lookup is deferred/invalid, and the source location is
// retained for a diagnostic. A resolved bound with no matching member produces a missing-member
// diagnostic.

// 6. Visibility and receiver safety
// ---------------------------------
//
// `public` exposes a declaration outside its module/distribution subject to ordinary import rules.
// `internal` exposes it only inside the named distribution. One source analysis has one explicit
// distribution identity and cannot prove a cross-binary internal access from syntax alone.
// `private` limits access to the declaration's containing type; a nested declaration follows the
// innermost owner rule in the visibility contract. `protected` requires the current declaration to
// be inside the declaring type or a derived type.
//
// For a protected instance member accessed from a derived type, the receiver must be the current
// instance or have a static type compatible with the current derived type. A derived method cannot
// use a protected member through an arbitrary sibling/base instance merely because its own
// containing type derives from the owner. Companion and instance access are checked independently.
//
// Property declaration visibility and accessor visibility are separate. A public property can
// expose a public getter and private setter. A read checks `get`; an assignment checks `set`; a
// compound update checks both and preserves the declared single-evaluation order. A private getter
// does not become callable because its setter is accessible. Ref/out requires a writable storage
// location and never invokes a property accessor to synthesize an address.
//
// Accessibility is not overload ranking. An inaccessible declaration does not lose to a less
// accessible overload through a favorable conversion score. The compiler reports the inaccessible
// selected name at its source use; it does not silently bind a different member with the same name.
//
// 7. Member categories
// --------------------
//
// Properties are values with a read type and optional write contract. The getter's checked effects
// are added on read and the setter's checked effects on write. A delegated property selects its
// compile-time operator contract from the same receiver/generic substitutions; runtime string-name
// lookup is not permitted.
//
// Indexers are resolved by index argument types and the same named/default/vararg call mapping as
// other callable members. The index expression evaluates the receiver, then indices left to right.
// A compound index update computes the receiver and every index once, reads once, invokes the
// operator once, and writes once. A failed write does not roll back side effects already performed.
//
// Enum variants are members of their enum type and construct the declared payload shape. A variant
// is not a function overload in an unrelated type. Nested types are type symbols and may be used as
// type qualifiers only in syntactically valid type positions.
//
// Operator members use the companion/instance placement defined for each operator family. Their
// candidate set is formed from the operand owner and the resolved companion; ordinary property
// lookup does not invent an operator member for an intrinsic operation.
//
// 8. Failure and recovery
// -----------------------
//
// A missing member is reported once at the member name. A same-name set containing multiple
// incompatible value declarations is ambiguous. A callable overload group remains a group until
// argument mapping, inference, constraints, conversions, and ranking have been evaluated.
//
// A deferred candidate can change whether a known applicable method is the unique winner. If one
// visible candidate cannot be rejected or ranked because its signature/body is unsupported, the
// compiler must not claim a unique selected call. It records the source node as unverified and
// continues checking independent bodies.
//
// Cancellation is checked while traversing inheritance, bounds, overload candidates, and nested
// substitutions. Cancellation stops the analysis operation; it is not converted to a missing
// member or user diagnostic.
//
// Declaration and candidate iteration order is stable by source/module identity for serialized
// inspection. Stable ordering is not semantic precedence. The selected result must be identical
// when the same source set is provided in a different file enumeration order.

// 9. Resource budgets
// -------------------
//
// Inheritance/member traversal has a maximum depth of 128 and a maximum of 8192 unique constructed
// receiver types per lookup. Generic type specialization is separately bounded by its own depth
// and instance budgets. Exceeding any budget produces a bounded resource diagnostic and leaves the
// lookup incomplete. The implementation must not recurse without a cycle set or convert a limit
// into an empty candidate list.
//
// A visited key is a constructed type identity in the current TypeSpecialization workspace. A
// declaration symbol ID by itself is insufficient because two constructed applications can have
// different member signatures. Workspace-local numeric IDs are never persisted as stable API IDs.
//
// 10. Diagnostic categories
// -------------------------
//
// SOL2210 reports an unavailable instance receiver.
// SOL2211 reports an unresolved value name.
// SOL2212 reports ambiguous value/member qualification.
// SOL2214 reports inaccessible declaration or accessor.
// SOL2215 reports a missing receiver member.
// SOL2243 reports nullable access without a proof or safe operation.
// SOL2290 reports bounded member/type analysis exhaustion.
//
// Override mismatch diagnostics are emitted during declaration/body validation and retain the
// declaration span. Call-site overload ambiguity remains distinct from declaration ambiguity.
// Diagnostic order is source path ordinal, span start, then diagnostic code; ties within one node
// use the canonical candidate signature. Reaching a diagnostic count limit does not mark the
// source as complete.

// 11. Positive source shapes
// -------------------------
//
// These declarations illustrate independent lookup cases; they are not one compilation unit.

// Constructed base substitutions:
//
//     interface SequenceView<T> { func first(): T }
//     class ArrayView<T>(val values: []T): SequenceView<T> {
//         override func first(): T = values[0]
//     }
//     class ReadOnlyView<U>(val source: ArrayView<U>): SequenceView<U> {
//         override func first(): U = source.first()
//     }
//
// Generic override correspondence:
//
//     interface Mapper<T> {
//         func <R> map(value: T, convert: (T) -> R): R
//     }
//     class TextMapper: Mapper<String> {
//         override func <R> map(value: String, convert: (String) -> R): R = convert(value)
//     }
//
// A constrained receiver:
//
//     interface Readable { func read(): String }
//     interface Closeable { func close(): () }
//     func <T> inspect(value: T): String where T: Readable, T: Closeable {
//         value.close()
//         return value.read()
//     }
//
// A protected receiver check:
//
//     open class Vehicle { protected func inspect(): String = "vehicle" }
//     class Car: Vehicle {
//         func inspectFleet(items: []Vehicle): String = items[0].inspect()
//     }
//
// In the last example, the call is rejected if the protected receiver rule does not permit a
// general `Vehicle` value. `this.inspect()` and a `Car`-typed receiver inside `Car` are valid.
//
// An inaccessible setter does not make a property a location:
//
//     class Credential {
//         public var label: String
//             get = this._label
//             private set(value) { this._label = value }
//         private var _label: String
//     }
//
// `credential.label` is readable outside `Credential`; assignment outside it reports an
// accessibility error and is not an `out` argument.

// 12. Implementation status contract
// ----------------------------------
//
// The development BodyAnalysis may support only a strict subset of this document. It must expose
// Bound, Invalid, and Deferred status separately. `HasErrors == false` alone is not proof that
// receiver lookup, visibility, overload selection, generic bodies, checked effects, flow, borrow
// safety, or lowering is complete.
//
// A future implementation that closes this contract must add semantic regression coverage for:
//
// - the same generic base through one path and through a repeated diamond path;
// - two different constructed base paths with the same declaration name;
// - derived overload plus base overload with different parameter shape;
// - valid override replacement and missing/invalid override diagnostics;
// - competing interface defaults and explicit conflict resolution;
// - each receiver category and type/member shadowing;
// - every visibility level, including protected access through an invalid receiver;
// - public property/private getter, private setter, and independent checked effects;
// - constrained type parameters with one, multiple, repeated, and conflicting bounds;
// - nullable safe member/call evaluation and result lifting;
// - cancellation and each traversal budget at deterministic source spans.
//
// These cases define expected behavior even while FIR or native lowering is not yet available.
