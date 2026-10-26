// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative partial declaration identity; cross-file symbol validation is incomplete.
module source.spec.member_resolution.partial_identity

// Partial declaration identity and member lookup
// ==============================================
//
// A partial declaration is one logical type with multiple source parts. Member lookup operates on
// the merged logical declaration; it does not search one file and then fall back to another file.
// This specification applies to partial class, struct, ref struct, object and nested declarations.

// 1. Logical identity
// -------------------
//
// Every part of a partial type has the same:
//
// - distribution identity;
// - logical module identity;
// - containing declaration identity, including the complete nested owner chain;
// - source name and generic arity;
// - declaration category: class, struct, ref struct, or object;
// - compatible generic parameter names, order and constraints;
// - compatible visibility, abstract/open/sealed and other type-level modifiers.
//
// A source path, directory name, file basename, import alias, or file enumeration index does not
// participate in logical identity.
//
//     module inventory.model
//
//     partial class Item<T: std.core.Object> {
//         val id: String
//     }
//
//     partial class Item<T: std.core.Object> {
//         func key(): String = this.id
//     }
//
// Both parts contribute members to the same Item<T>. Item<String> has one constructed type
// identity. It does not have two independently constructed declarations that happen to share a
// spelling.
//
//     module inventory.model
//     partial class Item<T: std.core.Object> { }
//     partial struct Item<T: std.core.Object> { }
//
// This is a conflicting declaration category and is diagnosed even if the two files are never
// referenced by a call site.

// 2. Partial markers and source sets
// ----------------------------------
//
// If a logical type appears in more than one source declaration, every declaration must be marked
// partial. One declaration marked partial does not silently absorb a non-partial declaration.
//
//     partial class Settings { func read(): String = "a" }
//     class Settings { func write(value: String): () { } }
//
// The source set is invalid because one part omitted partial. Member lookup must not merge the two
// and report a successful member group.
//
// A compiler input explicitly determines the source set. A nearby file not supplied to the
// compilation does not implicitly add a partial part. A source generator or later compilation may
// add parts only through an explicit source-set or metadata contract.
//
// Partial declarations cannot merge across distributions. An external binary's type identity is
// closed for source merging even when it has the same module, name and generic arity.
//
// Reordering, renaming, or relocating source files while preserving declarations cannot change the
// merged API, member identity, initialization semantics, or ABI layout.

// 3. Generic parameter agreement
// ------------------------------
//
// Each part repeats the generic parameter list so the declaration can be understood in isolation.
// Every part must use the same ordinal parameter mapping and semantically equivalent constraints.
//
//     partial class Table<K: std.hash.Hashable, V> { }
//     partial class Table<K: std.hash.Hashable, V> {
//         func get(key: K): V? = null
//     }
//
// The name-to-ordinal mapping is K = 0, V = 1 in both parts. Constraints bind to parameter identity
// by ordinal, not by the spelling found in an arbitrary first file.
//
//     partial class Table<K, V> { }
//     partial class Table<V, K> { }
//
// This declaration set is incompatible even though its arity matches. The compiler cannot sort
// names to recover a guessed mapping.
//
// Constraint comparison includes inline bounds, matching where clauses, ref-like anti-constraints
// and effect/type relationships that are part of the generic declaration contract. Semantically
// equivalent declaration order within a bound set is accepted; a missing or stronger constraint
// in one part is a conflict. It is not safe to use only the first part's constraint list.

// 4. Base list and implemented contracts
// --------------------------------------
//
// The merged type has one resolved base class and one set of implemented interfaces. Base
// declarations across parts are unioned only when they resolve to the same constructed identity.
// Repeating an identical interface is a duplicate that has no second semantic effect.
//
//     interface Named { }
//     partial class Document: Named { }
//     partial class Document: Named { }
//
// Document implements Named once for type identity and lookup.
//
//     interface Reader<T> { }
//     partial class Document: Reader<String> { }
//     partial class Document: Reader<std.core.Object> { }
//
// These are not two compatible spellings of one base. If the language's inheritance rules reject
// the duplicate constructed interface, the merged declaration is invalid and member lookup cannot
// choose either projection according to file order.
//
// A class cannot acquire two distinct base classes by placing one in each part. The single-base
// restriction is checked on the merged type. An omitted base list in another part does not erase a
// base supplied elsewhere.
//
// Interface defaults, explicit implementations and conflict declarations are resolved against the
// merged interface set. Merging source parts does not make a conflicting default more specific.

// 5. Member identity across parts
// -------------------------------
//
// Members in all parts share the same owner type identity. Ordinary overload identity is checked
// after merging and after the generic owner substitution is applied. It is not scoped to a file.
//
//     partial class Formatter {
//         func format(value: String): String = value
//     }
//
//     partial class Formatter {
//         func format(value: int): String = value.toString()
//     }
//
// These are two overloads in one group. A call on one Formatter receiver considers both.
//
//     partial class Formatter { func format(value: String): String = value }
//     partial class Formatter { func format(value: String): String = "override" }
//
// The repeated signature is a duplicate declaration error. The compiler must not pick the first
// file, select the last file, or interpret the second part as an override.
//
// Return type, default expression, parameter spelling, effect set, and file order do not create
// separate overloads when the source overload identity is otherwise the same.
//
// A partial part cannot declare an override relationship against another part of the same logical
// type. Override is checked against a distinct inherited owner after the merged member table exists.
//
// Name hiding, property/method collisions, indexer identity, operator pairs, and generated members
// are validated over the merged table. A declaration in a later part cannot bypass the same
// conflict rule by appearing in another file.

// 6. Property and accessor composition
// ------------------------------------
//
// A property declaration owns its getter, optional setter, backing storage contract, and visibility
// metadata. A later partial part cannot attach an accessor to the earlier property by repeating
// only its name.
//
//     partial class Account {
//         public var displayName: String
//             get = this._displayName
//     }
//
//     partial class Account {
//         private var displayName: String
//             set(value) { this._displayName = value }
//     }
//
// This is a duplicate property declaration, not a cross-file getter/setter pair. A declaration that
// intends to supply both accessors must contain both accessors in one property declaration or use a
// language feature that names the same property identity explicitly.
//
// Backing fields are owned by the property declaration and cannot be merged by matching generated
// names. A user declaration colliding with a generated backing member is diagnosed by the same
// declaration identity rules.
//
// Primary-constructor properties, synthesized data members and delegation-generated members are
// added after source-part merge and before final member lookup. Their collision check uses the full
// merged set.

// 7. Initialization and construction
// ----------------------------------
//
// At most one source part contains primary-constructor parameters or initialization statements.
// This restriction gives the logical type one source-defined initialization sequence independent
// of input file order.
//
//     partial class Cache(capacity: int) {
//         val maximum: int = capacity
//     }
//
//     partial class Cache {
//         func full(): bool = this.maximum == capacityLimit()
//     }
//
// The second part adds members but does not add a second initialization body. The first part's
// initialized fields are initialized in the order specified by the constructor/member contract.
//
//     partial object Versions { val current: int = 1 }
//     partial object Versions { val next: int = 2 }
//
// This is invalid if both parts contain initialization. Object initialization has one deterministic
// owner; the compiler does not pick an order from filenames or module input order.
//
// Explicit secondary constructors across all parts form one constructor set. Duplicate constructor
// signatures are rejected. Delegation-cycle analysis sees the merged constructor graph.
//
// A partial part without initialization may declare methods, nested types and eligible members but
// cannot introduce a hidden constructor side effect through a property initializer.

// 8. Visibility and modifiers
// ---------------------------
//
// Type-level access and semantic modifiers are one contract. Every partial part agrees; a source
// file cannot widen a private type or make one portion open while another is sealed.
//
//     public partial class Catalog { }
//     internal partial class Catalog { }
//
// The merged declaration is invalid because the access modifiers disagree. There is no “most
// permissive wins” rule.
//
// Member visibility is attached to each member after merge and then evaluated by ordinary receiver
// lookup rules. A private member in one part is not visible merely because a caller was declared in
// a different file that belongs to the same module.
//
// Public signatures are validated against the accessibility of every type, bound and effect they
// expose after merge. A public member added by another part cannot hide an inaccessible signature
// type in the first part.

// 9. Deterministic merge diagnostics
// ----------------------------------
//
// The semantic merge uses canonical declaration identities. Diagnostics retain both relevant
// source locations for:
//
// - missing partial marker;
// - category, modifier or generic-list disagreement;
// - conflicting base construction;
// - duplicate method/property/indexer/operator/constructor identity;
// - multiple initialization owners;
// - incompatible accessor or generated-member collision.
//
// The primary location is selected by semantic source span ordering and stable path comparison;
// the secondary location identifies the conflicting declaration. Input enumeration order is not a
// semantic priority.
//
// An invalid partial set does not produce a successful partial member table. Error recovery may
// expose nonconflicting names for follow-on diagnostics, but its result remains incomplete and is
// marked as recovered in analysis APIs.

// 10. Reflection and ABI identity
// -------------------------------
//
// Reflection reports one logical type and one merged member set. It may expose part-source spans
// for tooling, but those spans do not create separate runtime type identities.
//
// Binary type identity, layout and mangling depend on the merged declaration contract, not on
// filenames, part order, source directories, or count of partial fragments. Adding a method-only
// part does not create a second runtime type. Adding a field or initializer is subject to the
// ordinary layout/initialization compatibility rules of the selected artifact.
//
// A deterministic compiler emits the same semantic member order and ABI identity for equivalent
// source sets regardless of file enumeration. Source declaration order inside the language's
// explicitly ordered initialization/member rules remains significant where stated; path order does
// not replace that rule.

// 11. Implementation boundary
// ---------------------------
//
// The source symbol layer can discover partial declarations in one explicit source analysis. The
// complete language implementation still requires stable merged metadata for binary references,
// cross-file diagnostics, generated members, reflection, layout, and incremental rebuild identity.
// Parser acceptance alone is not evidence that those checks are complete.
