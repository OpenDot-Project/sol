// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative access and receiver contracts; several advanced checks remain unimplemented.
module source.spec.member_resolution.visibility

// Visibility is evaluated after the receiver and declaration identity are known. It does not
// participate in overload ranking and never creates an implicit receiver.

// PUBLIC
// ======
// public is available to any source that can name the containing module through its explicit
// imports. The declaration must still be a valid member of the receiver category. Public does not
// export a private parameter type, internal nested type, or inaccessible checked effect from a
// public signature; signature visibility validation is a declaration-level rule.

//     module access.library
//
//     public class Catalog {
//         public func find(key: String): String? = null
//         private func normalize(key: String): String = key
//     }
//
// A caller may invoke `catalog.find`. It may not invoke `catalog.normalize` even though the
// containing class is public. Member visibility is not inherited from the owner declaration.

// INTERNAL
// ========
// `internal` means visible within the same named distribution. A SourceAnalysis that contains a
// single DistributionName can validate within-source internal access. A compiler that reads
// external metadata must compare the distribution identity exactly. Module identity alone is not
// enough: two distributions may each contain a module with the same logical name.
//
// A normal import, module alias, selective import, public import, or re-export does not widen
// internal access. Re-export exports public declarations only. A public signature cannot make an
// internal member callable from outside its distribution.

// PRIVATE
// =======
// A private member is visible only in the containing type. Nested declarations follow their direct
// lexical owner: code inside the containing type can access the private member through a permitted
// receiver; code inside an unrelated nested type does not gain access merely because both appear in
// the same file. The exact nested-owner closure is based on the declaration tree, never source path.
//
// A private declaration in a base type is not an inherited fallback. A private declaration in the
// current type remains a name declaration even when the caller is outside the type; diagnostics
// report access failure at the use rather than silently searching for a same-spelling base member.

// PROTECTED
// =========
// A protected member is accessible inside its declaring type and inside a derived type. The
// receiver restriction prevents a derived body from using protected access through an unrelated
// base-typed sibling object. In a derived instance body, `this.member` is valid. A receiver whose
// static type is the current derived type is valid. A general base-typed receiver is not valid only
// because its runtime value could happen to be derived.
//
//     open class Device {
//         protected val serial: String
//     }
//
//     class Router: Device {
//         func ownSerial(): String = this.serial
//
//         func otherSerial(other: Router): String = other.serial
//
//         func baseSerial(other: Device): String = other.serial
//     }
//
// `ownSerial` and `otherSerial` are permitted. `baseSerial` is rejected at `serial`. This rule
// concerns the receiver's static type and does not perform a runtime downcast. Protected companion
// access follows the companion's own inheritance/type rules; it does not grant ordinary instance
// access through a type name.

// ACCESSOR VISIBILITY
// ===================
// A property declaration, getter, setter, and initializer accessor have independent effective
// access. If a property declares no accessor modifier, the accessor inherits the property's
// effective access. An explicit accessor may reduce but cannot widen its property's exposure.
//
//     class Account {
//         public var displayName: String
//             get = this._displayName
//             private set(value) { this._displayName = value }
//
//         private var _displayName: String
//     }
//
// External code can read `account.displayName`; it cannot assign it. The private setter does not
// erase the readable property declaration and does not make assignment resolve to a same-named
// base property. Inside Account, assignment invokes the setter.
//
// A read resolves the getter and adds only the getter's checked effects. A write resolves the setter
// and adds only setter effects. A compound update requires both contracts and applies effects in
// getter, operator, setter order. If the setter fails, the prior getter/operator side effects are
// not rolled back.
//
// Ref/out needs stable writable storage. A property remains an accessor operation even when both
// accessors are public. No hidden temporary is passed as a managed address. An indexer behaves the
// same way unless its declaration explicitly exposes a source-level ref return under the ref-return
// contract.

// EXPRESSION CATEGORIES
// =====================
// The compiler distinguishes a type value, singleton object value, instance value, module
// qualifier, property value, method group, and callable value. The same spelling can denote
// different categories at different syntax positions, but a failed lookup cannot convert one
// category into another as fallback.
//
//     object Versions {
//         const val current: int = 1
//     }
//
//     class Formatter {
//         companion object {
//             func defaultWidth(): int = 80
//         }
//
//         func format(): String = "formatted"
//     }
//
//     func use(formatter: Formatter): int {
//         val width = Formatter.defaultWidth()
//         val rendered = formatter.format()
//         return width
//     }
//
// `Versions.current`, `Formatter.defaultWidth`, and `formatter.format` have different declaration
// owners and receiver categories. `Formatter.format()` is not accepted as a shorthand for a
// constructed Formatter receiver.

// NULLABLE RECEIVERS
// ==================
// A receiver of type `T?` is not a valid ordinary member receiver until a flow proof narrows it to
// T. `?.` keeps the nullable receiver evaluation single and conditionally evaluates the remaining
// operation. `!!` converts the expression to non-null T or raises the null assertion runtime error.
//
//     class Session {
//         func close(): () { }
//         func name(): String = "session"
//     }
//
//     func closeIfPresent(session: Session?): () {
//         session?.close()
//     }
//
//     func nullableName(session: Session?): String? = session?.name()
//
//     func requiredName(session: Session?): String = session!!.name()
//
// A safe call skips explicit arguments if the receiver is null. It also skips defaults and the
// target invocation. Receiver and arguments are each evaluated no more than once. The result
// category is not writable storage; `session?.field = value` and `out session?.field` are invalid.

// NAME HIDING AND DIAGNOSTICS
// ===========================
// If a nearest non-callable member owns a name, a farther base declaration does not reappear after
// access or conversion failure. A more-derived function group hides inherited non-callable values
// of the same name. At the same effective depth, a property/type and method group with one spelling
// is ambiguous. Exact declaration order, file order, import order, and alphabetic symbol order do
// not determine the semantic result.
//
// Accessibility diagnostics identify the inaccessible selected declaration. Missing-member
// diagnostics identify the member token. Nullable-receiver diagnostics identify the operation that
// requires a non-null proof. An unsupported access rule remains Deferred and appears in the
// unverified source locations; it cannot be converted into “member is accessible.”

// BOUNDARY CASES THAT REQUIRE SEMANTIC REGRESSION
// ===============================================
// The tests for a complete access checker must vary one rule at a time:
//
// - public member on public, internal, and private containing types;
// - internal access within one distribution and from a separate distribution;
// - private access through this, another same-type receiver, and an unrelated nested type;
// - protected access through this, derived-typed receiver, base-typed receiver, sibling instance,
//   object singleton, and companion type;
// - public property/private getter, public getter/private setter, and protected setter;
// - reads, writes, compound updates, increment/decrement, ref/out and delegated properties;
// - ordinary, nullable-narrowed, safe, and asserted receiver expressions;
// - same name on local, import alias, type, object, companion, property, and callable value;
// - inaccessible nearest declaration with a public same-name base declaration;
// - an access failure whose member has a checked getter or setter effect.
//
// This contract is intentionally more specific than the current BodyAnalysis implementation. The
// implementation must expose any unproved access as incomplete rather than claim the contract is
// fully verified.
