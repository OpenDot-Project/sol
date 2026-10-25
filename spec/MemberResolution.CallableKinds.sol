// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative operation-specific member contract; execution lowering remains incomplete.
module source.spec.member_resolution.callable_kinds

// Member categories by operation
// ==============================
//
// A receiver's name group is consumed differently by a read, call, assignment, construction,
// operator, index operation, or delegated property. The common lookup path supplies declarations;
// each operation validates the exact category and its own evaluation/effect contract.

// PROPERTY READ
// =============
// A property read selects one property declaration and its getter. It returns the getter's
// specialized value type and adds the getter's checked effects. If the property is delegated,
// compile-time operator binding identifies the stored delegate and getter protocol; runtime string
// lookup by `getValue` is forbidden.
//
// A method overload group is not a property value merely because the source omits parentheses. A
// future method-group-to-function conversion must have an explicit target function type and
// preserve generic parameters, receiver dispatch, ref/out modes, and checked effects. If that
// conversion is unavailable, the expression remains deferred.

// PROPERTY WRITE
// ==============
// A property write selects the setter, checks its visibility and parameter type, and evaluates the
// receiver once. A simple assignment does not invoke the getter unless the language statement
// explicitly reads the old value. A compound assignment invokes getter, operator, setter in order.
//
// Property writability is not raw storage. `out property` and `ref property` are invalid even when
// both getter and setter are public. A ref-returning member is a separate source contract and must
// prove location identity and lifetime; it cannot be inferred from accessor syntax.

// INDEXER
// =======
// An indexer candidate group is selected from the receiver's indexer declarations using the same
// argument mapper and generic inference as callable methods. It keeps named/default/vararg rules
// only where the indexer declaration grammar permits those forms. The receiver is evaluated first;
// index expressions are evaluated left to right exactly once.
//
// For a compound update `target[index] += value`, evaluate `target` and `index` once; load the
// current element once; select the binary operator; compute once; invoke the setter once. If a getter
// or operator has a checked effect, that effect is added at that operation. If the setter throws,
// prior side effects remain and no rollback is promised.

// CONSTRUCTOR
// ===========
// `Type(args)` resolves a type symbol, constructs its closed generic type, then selects one
// constructor on that type. Constructors are not inherited ordinary members. Generic type
// inference may use constructor arguments to infer the type's parameters when the type application
// is implicit. Explicit type arguments fix those parameters before constructor overload selection.
//
// The constructor call adds constructor checked effects. It also creates initialization obligations
// for required fields/properties. A constructor overload is not usable until its primary/base
// delegation and field initialization contracts are validated. `this(args)` and `super(args)` have
// their own rules and cannot search arbitrary members named `this` or `super`.
//
// A type-level qualifier alone cannot provide an instance receiver for an instance method. An
// object declaration is a singleton value; its constructor restrictions do not turn it into a
// class instance constructor call.

// OPERATOR
// ========
// The operator declaration is found on the declared owner/companion determined by the operator
// family. The member set is closed by the operator contract: owner operand, arity, passing modes,
// result type, required comparison/truth pairs, and checked variant selection. Ordinary receiver
// lookup cannot search every module function and reinterpret an unrelated name as an operator.
//
// A checked operator may be preferred when checked arithmetic is active and an applicable checked
// overload exists. Otherwise the ordinary operator remains eligible. User operators do not change
// source precedence or make `&&`, `||`, assignment, `?.`, `?:`, `===`, or `!==` directly overloadable.
// Compound assignments read and write the same location once.
//
// Nullable lifting first identifies the non-null receiver/operand contract and then propagates the
// nullable result according to the operator family. The operator body is not called with a null
// payload. Runtime null gating must be explicit in the lowering.

// DELEGATION
// ==========
// `by` is not general member syntax. The compiler resolves provideDelegate/getValue/setValue
// protocol members from the property owner, descriptor metadata, and constructed generic types.
// The selected declaration identity and effects are stored in the source analysis result. The
// runtime must not reflect over method-name strings to choose a protocol.
//
// Protocol methods are instance methods. Their explicit receiver and SolProperty<T> descriptor
// types are specialized with the property owner and value type. Getter effects are added on read;
// setter effects are added on write; provideDelegate effects occur during initialization. Two
// applicable providers are an overload ambiguity, not source-order selection.

// FUNCTION VALUE INVOCATION
// =========================
// A local function value has one already-constructed function type. It uses its ordered parameter
// types, exact ref/out modes, result, and checked effect set. It is not a member overload group and
// does not acquire generic type arguments from an identifier spelling. Named/default/vararg
// invocation rules follow the function value type only when those details are represented there.
//
// Calling a function value evaluates its receiver/local value before arguments. Ref/out requires
// exact writable storage. The effect set is added to the caller. If the function type is nullable,
// a non-null proof or safe invocation is required.

// SAFE ACCESS
// ===========
// Safe access wraps the consuming operation in a conditional receiver branch. It applies to a
// property read, callable member, or index operation only where the syntax explicitly supports it.
// The receiver is evaluated once. Arguments, default expressions, and index expressions are
// skipped on the null branch. A result R becomes R?; a result already R? remains R?. A setter
// operation through a null receiver does not fabricate a writable location.

// Diagnostics are operation-specific. A method call with no applicable overload is not a missing
// member if a method group exists. A property write with no setter is not a missing getter. A
// constructor constraint failure is not an inherited member ambiguity. Candidate records preserve
// the declaration category so downstream diagnostics can name the actual failed operation.

// Cross-operation invariants
// --------------------------
//
// - One receiver expression is evaluated at most once per source operation.
// - Name lookup does not execute getters, defaults, constructors, operators, or delegate providers.
// - Accessor effects are included only for accessors invoked by the operation.
// - Generic owner substitution happens before method/indexer/constructor inference.
// - Method inference never treats return type as overload identity.
// - Call argument evaluation happens only for the selected candidate and remains source ordered.
// - Ref/out does not bind through properties, safe access, or temporary conversions.
// - Every unsupported operation leaves an explicit proof gap; no empty success is synthesized.

// These conditions apply whether the selected target later lowers to a direct call, virtual call,
// interface dispatch, closure invocation, operator sequence, or native bridge. Dispatch lowering
// is not yet implied by the presence of a selected source declaration.
