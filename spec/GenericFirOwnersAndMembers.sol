// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: source member binding is broader than native generic FIR; owner/member specialization is normative future work.
module source.spec.generic_fir_owners_and_members

// Generic nominal owners and member specialization
// =================================================
//
// This contract defines identity and specialization boundaries for generic classes, structs,
// ref structs, partial declarations, methods, constructors, properties, indexers, operators,
// delegation, and inherited members. It complements the source generic-call and member-resolution
// contracts. It does not claim that these constructs can currently produce native code.
//
// The active implementation can bind generic member calls in source analysis, substitute
// constructed receivers for lookup, collect candidate-local inference evidence, and check nominal
// constraints. Verified FIR accepts directly called module-level generic free functions and a
// bounded receiver-free subset of generic methods on stateless, non-generic module-level objects.
// Generic methods on stateful, nested, companion, inherited, generic, or virtual owners remain
// outside FIR, as do computed receivers, constructors, properties, accessors, indexers, and
// operators. Full owner/member specialization still requires broader per-instance semantic
// rebinding, layouts, dispatch, runtime metadata, and ABI rules.

// 1. Generic parameter identity belongs to a declaration
// ------------------------------------------------------
//
// A generic type parameter identity is the tuple (canonical owner identity, ordinal). The source
// spelling is presentation only. `T` in a class and `T` in a method are distinct parameters even
// when their names, constraints, and source locations are similar. Renaming a parameter in a
// compatible partial part does not change its ordinal identity.
//
//     class Pair<T, U>(val first: T, val second: U) {
//         func <T> replace(value: T): Pair<T, U> = Pair(value, second)
//     }
//
// The inner method `T` shadows the owner's spelling but does not replace the outer parameter.
// The return type above therefore needs a disambiguated written spelling if both identities are
// intended in one scope. Implementations must not solve that ambiguity by silently binding every
// spelling to the nearest type parameter. The grammar and name-resolution contract determine which
// spelling is selected; declaration ownership remains distinct after parsing.
//
// Internally, a parameter record retains:
//
// - the canonical source declaration identity of its owner;
// - its ordinal in that owner's parameter list;
// - its written name for diagnostics and reflection policy;
// - its normalized declared constraints and anti-constraints;
// - the source span that introduces its name and each constraint;
// - whether it belongs to a nominal declaration, callable, or another explicitly supported owner.
//
// Equality, substitution, overload identity, and specialization keys use the owner and ordinal,
// never the string `T`. Two unrelated declarations with an identical parameter list have unrelated
// identities. Parameter owner identity must survive CST, PSI, AST, binding, FIR metadata, source
// maps, and diagnostics.

// 2. Composed substitution has two independent maps
// ------------------------------------------------
//
// A call to a method declared on `Box<T>` has an owner substitution and a method substitution.
// The owner map is derived from the constructed receiver; the method map is derived by explicit
// method arguments or inference after argument mapping. For a selected body, both maps must be
// applied simultaneously to the original resolved type graph.
//
//     class Box<T>(val value: T) {
//         func <U> pair(other: U): Pair<T, U> = Pair(value, other)
//     }
//
//     val box: Box<String> = Box("left")
//     val pair: Pair<String, int> = box.pair(4)
//
// The receiver contributes `T = String`; the call contributes `U = int`. The body may not mutate
// one substitution map while walking a type and accidentally rewrite a replacement inserted by
// the other map. Substitution applies to parameter types, return types, checked effects, property
// types, index parameter types, operator operands/results, constructor parameters, and nested
// generic applications.
//
// Owner and method constraints are checked against their respective maps. An owner constraint is
// checked when the nominal application is formed. A method constraint is checked after the method
// candidate has a complete argument mapping and a unique substitution. A member call must not
// treat the receiver's type parameters as method parameters or use a method parameter to satisfy an
// unrelated owner constraint.
//
// If a receiver is itself an open generic parameter, member lookup can use the parameter's declared
// bounds to form source candidates. That candidate information is not a closed native instance.
// It remains open until an enclosing generic function is specialized and the body is rebound for
// the concrete receiver type.

// 3. Partial declarations share one logical owner
// -----------------------------------------------
//
// Compatible partial class, struct, ref struct, and object declarations form one nominal owner.
// All parts share one canonical identity and one generic parameter owner. A part cannot create an
// additional generic type merely by repeating the name and arity.
//
//     partial class Registry<K: std.hash.Hashable, V> {
//         func get(key: K): V? { ... }
//     }
//
//     partial class Registry<K: std.hash.Hashable, V> {
//         var count: int
//     }
//
// Every part agrees on module, source name, kind, generic arity, parameter order, compatible
// constraints, visibility, inheritance contract, and modifiers that apply to the type as a whole.
// The merge creates one stable owner before member lookup. Filename order, directory order, parser
// order, and partial-part count do not determine generic parameter identity or method ranking.
//
// A non-partial declaration cannot be absorbed into a partial owner. A class and struct with the
// same name do not merge. Distinct modules contain distinct owners even if they use the same simple
// name. A type imported through aliases retains the declaring module identity.
//
// Member sets are assembled after the owner is validated. Duplicate member identities across parts
// are diagnostics. Repeating a property name in another part does not attach a setter to a getter
// unless the source contract explicitly defines that form. Constructors, initialization statements,
// and primary-constructor parameters obey the single-owner initialization rules.
//
// The source analysis implementation has an initial partial-declaration merge and diagnostic path.
// It does not thereby prove that every cross-file generic constraint, generated member, initializer,
// inherited override, or layout agrees. The normative full merge rules remain the contract for
// later validation.

// 4. Partial generic owner invariants
// ----------------------------------
//
// A partial type's repeated generic parameter list is checked position by position. The names may
// follow the language's compatible-part rule, but each ordinal has one canonical owner identity.
// Parameter order is semantically significant.
//
//     partial class Table<K, V> { }
//     partial class Table<V, K> { }
//
// These declarations are not compatible: the second position has different meaning. A part cannot
// add a new type parameter or remove one. Each repeated constraint must normalize to the same
// contract. A constraint declared in one part and omitted in another is accepted only if the
// language's partial merge rule explicitly permits non-repeated constraints; the resulting owner
// has one merged constraint set and a deterministic diagnostic origin for each bound.
//
// Conflicting base applications are invalid even if they would produce equivalent runtime layouts.
// A repeated interface base is deduplicated by canonical constructed type identity. A constructed
// base reached through multiple routes remains one logical base only when declaration identity and
// all type arguments match. Invariant generic arguments are never merged by choosing a convenient
// common supertype.
//
// Partial declarations are analyzed as one source owner, but diagnostics retain the source part and
// exact type parameter/constraint origin that introduced a conflict. Recovering one valid-looking
// part after another part invalidates the owner would risk inconsistent IDs and is forbidden.

// 5. Nested nominal owners
// ------------------------
//
// A nested type has its own declaration identity and type parameters. Any capture or explicit
// forwarding of containing type parameters must be represented by owner-qualified type references;
// it must not be inferred from same-spelled names. The nested type's declaration contract decides
// whether outer parameters are in scope and whether its constructed identity includes them.
//
//     class Outer<T> {
//         class Inner<U> {
//             func combine(left: T, right: U): Pair<T, U> = Pair(left, right)
//         }
//     }
//
// If this nested form captures `Outer<T>`, the constructed identity is conceptually
// `Outer<String>.Inner<int>`. The runtime cannot erase the outer argument from a nested object's
// type token if member layout, field types, constraints, or reflection can observe it. If the
// language later specifies non-capturing nested declarations, their type identity remains
// independent and source examples must use that declared rule.
//
// Companion objects are separate singleton declarations rather than implicit static members. They
// do not capture the enclosing class's generic parameters unless the source declaration contract
// explicitly provides an owner-bound companion view. An unqualified type parameter name inside a
// companion cannot refer to an arbitrary instance of the enclosing generic class.
//
// The first FIR specialization slice rejects generic owner bodies and does not emit nested generic
// layouts or companion runtime descriptors. Source member resolution may still find a candidate;
// native rejection must preserve the distinction.

// 6. Generic class and class-member example
// -----------------------------------------
//
//     interface HasKey<K> { func key(): K }
//
//     open class Entry<K: std.hash.Hashable, V>(val id: K) {
//         open var value: V
//         open func read(): V = value
//         open func <R: HasKey<K>> project(keyed: R): Pair<K, V> = Pair(id, value)
//     }
//
//     class NamedEntry<V>(id: String, value: V): Entry<String, V>(id) {
//         override func read(): V = value
//     }
//
// The generic method's `R` identity belongs to `project`; `K` and `V` belong to `Entry`. A selected
// call through `NamedEntry<int>` composes inherited owner substitution (`K = String`, `V = int`)
// with the method substitution for `R`. Override comparison first projects the base declaration to
// the derived owner, then checks the specialized signature and effects under the same owner map.
//
// A derived class does not acquire a fresh identity for an inherited base parameter. If the base is
// reached through more than one inheritance path, constructed owner projections are deduplicated
// using canonical type identity. Two inconsistent constructions of the same base are ambiguous or
// invalid according to the member-resolution contract; the compiler must not select the first path.

// 7. Generic structs and ref structs
// ----------------------------------
//
// Struct and ref struct owners use the same declaration-owner and ordinal rules as classes, but
// their value/reference and lifetime behavior is not interchangeable. The category participates in
// layout, pass mode, mutability, boxing restrictions, and generic constraint checks.
//
//     struct PairValue<T, U>(val first: T, val second: U)
//
//     ref struct Window<T> {
//         val data: ReadOnlySpan<T>
//         func first(): T = data[0]
//     }
//
// A generic value layout is computed for each closed argument tuple unless a separately proven
// uniform layout rule applies. Field offsets, alignment, drop/cleanup obligations, boxing behavior,
// and calling convention may depend on the arguments. A source constraint alone does not prove
// that a ref-like type is storable in a heap object or that its values can be captured.
//
// `where T: allows ref struct` permits an argument category in source type checking. It does not
// grant permission to store T in a class field, return it beyond its region, capture it in a heap
// closure, box it, put it in a managed array, or pass it to an API that does not preserve the
// lifetime. Each operation requires a separate owner-aware escape proof.
//
// A ref struct's members can still be bound in source analysis. Ref/out argument exactness and
// lifetime checks operate on source locations, not on emitted generic FIR slots. A native
// specialization must prove receiver and argument lifetimes through every call and every temporary.

// 8. Generic methods on generic owners
// ------------------------------------
//
// A method's callable identity includes the declaring owner identity, method declaration identity,
// method generic arity, ordered parameter types after owner substitution, and passing modes. The
// method's generic parameter owner and ordinal remain separate from the owner's parameters.
//
//     class Mapper<T>(val value: T) {
//         func <U> map(convert: (T) -> U): U = convert(value)
//     }
//
// A `Mapper<String>` receiver specializes the method input to `(String) -> U`; it does not decide U
// until the call's arguments, expected result, explicit arguments, and constraints have been
// processed. Inference is candidate-local. A losing method candidate cannot leak its partial owner
// or method substitution into another candidate.
//
// A generic method selected on `Box<T>` must not be represented as if it were a module function
// whose only type parameters were the method parameters. Doing so erases the receiver identity and
// can merge calls on different owner instances. A correct future generic instance key includes the
// canonical method declaration plus every owning type argument, followed by every method argument
// in declaration order, and any explicitly reified enclosing owner arguments required by the ABI.
//
// Calls from a generic owner body can remain open. They become concrete only after the owner
// substitution is known and the method call is rebound. No open owner parameter may remain in a
// verified native signature or hidden in a type argument, effect, function value, or runtime token.

// 9. Constructors and construction identity
// ------------------------------------------
//
// Construction first resolves the nominal type identity and a closed or inferable type-argument
// list. Constructor candidates are selected within that owner; constructors are not inherited
// ordinary members. Owner arguments are substituted before constructor method arguments, if the
// constructor form supports its own generic parameters, are inferred.
//
//     class Buffer<T>(val size: int, val initial: T) {
//         init { }
//         constructor(initial: T): this(1, initial) { }
//     }
//
//     val bytes: Buffer<byte> = Buffer(4, 0)
//
// `T` is inferred from the constructor parameters and the expected constructed type under the
// language's inference rules. Explicit `Buffer<T>(...)` syntax fixes owner arguments before
// overload selection. A constructor's parameter names, defaults, varargs, ref/out modes, checked
// effects, delegation target, and required-field obligations remain attached to the selected
// constructor declaration.
//
// Primary-constructor property initialization, field initializers, base construction, and secondary
// constructor delegation participate in definite assignment and exceptional flow. A constructor
// candidate is not callable merely because its parameter types match; its source owner must satisfy
// all initialization and delegation obligations.
//
// `object` is a singleton, not a class with an implicit per-call instance constructor. Object
// initialization has one canonical identity and must not be multiplied by generic call-site
// specialization. Partial object parts merge into that singleton under the partial-owner rules.

// 10. Generic properties and accessors
// ------------------------------------
//
// A property has a canonical declaration identity and a read/write contract. Its declared type is
// substituted with the owner arguments. Getter and setter are distinct operations for accessibility,
// effects, flow, and assignment analysis. An immutable property has no writable setter location.
//
//     class Slot<T>(initial: T) {
//         var value: T = initial
//             get() = field
//             set(next) { field = next }
//     }
//
//     val slot: Slot<String> = Slot("value")
//     val current: String = slot.value
//
// A getter read uses the substituted property result and getter effects. A setter write uses the
// substituted value parameter and setter effects. Compound assignment stabilizes the receiver,
// invokes the getter, resolves the operator, and invokes the setter according to the source
// evaluation-order contract. It must not call either accessor just to infer a generic type.
//
// A property whose type contains an owner or method parameter is not equivalent to an ordinary
// source field after substitution unless its backing storage has a valid concrete layout. For a
// reference owner, object field layout and GC descriptor rules apply. For a value owner, closed
// layout and copy semantics apply. For a ref struct, escape and region rules apply.
//
// Delegated properties resolve the delegate protocol at compile time using the substituted owner,
// delegate, property, and receiver types. Runtime string-name lookup is not the protocol. The
// synthesized read/write operation retains the source property origin and the selected operator
// origin so diagnostics and verifier checks can reconstruct the binding.

// 11. Indexers and indexed assignment
// -----------------------------------
//
// An indexer is a call-like member with an owner receiver and ordered index arguments. Candidate
// mapping, generic inference, defaults or varargs where permitted, passing modes, access checks,
// overload ranking, and checked effects follow the callable contracts. The indexer owner arguments
// are applied before indexer-specific method arguments.
//
//     class Matrix<T>(val rows: int, val columns: int) {
//         operator fun get(row: int, column: int): T = ...
//         operator fun set(row: int, column: int, value: T): () { ... }
//     }
//
// Read access resolves one getter and returns its closed result type. Write access resolves the
// setter for the assigned closed value. Compound indexed update evaluates the receiver and each
// index expression exactly once in source order, reads the current value once, applies the selected
// operation, then writes once. If a getter, operator, or setter fails, the reported effect belongs
// to that exact operation.
//
// Indexer ref/out use is not implied by the getter result. A getter returns a value unless the
// language explicitly defines a location-producing accessor. An index expression may be passed by
// ref/out only when binding proves a stable writable storage location and the backend preserves the
// address through any required receiver/index temporaries.

// 12. Operators and generic owner lookup
// ---------------------------------------
//
// A user-defined operator is selected from the operand owner and the operator family’s declared
// companion/instance locations. It is not found by searching every module-level function whose name
// resembles the token. Operator candidate identity includes the owner after generic substitution,
// method generic arity, parameter passing modes, and the operator family contract.
//
//     class Magnitude<T: std.order.Ordering>(val value: T) {
//         operator fun plus(other: Magnitude<T>): Magnitude<T> = ...
//     }
//
// Whether the above user operator is legal depends on the operator family’s operand-owner rule. If
// admitted, a call on `Magnitude<int>` must use that closed owner and exact selected operand types.
// The constraint `Ordering` does not itself provide a machine implementation of `plus`.
//
// Checked arithmetic selection, intrinsic fallback, nullable lifting, assignment location, and
// evaluation order are separate rules. A user operator cannot silently replace a checked intrinsic
// or change overflow semantics unless the source contract grants that behavior. Operator body
// specialization is subject to the same per-instance rechecking and ABI constraints as any method.

// 13. Inheritance, projections, and override matching
// ---------------------------------------------------
//
// A generic base edge is a substitution from one owner to a constructed base type. Traversal carries
// the complete constructed type at every edge. It never compares only simple names or generic
// arities.
//
//     open class Base<T> { open func read(): T = ... }
//     open class Mid<U>: Base<[]U>()
//     class Leaf: Mid<String>()
//
// A `Leaf` receiver sees `read(): []String`. In an open `Mid<U>` body, the same member remains
// `read(): []U` until U is closed and the body is rebound. A second path to `Base<[]String>` is
// deduplicated by declaration identity plus constructed arguments. A path to `Base<String>` is a
// distinct constructed owner and cannot be merged with it.
//
// Override matching projects the base signature into the derived owner before comparing the method
// name, generic arity, parameter types, and passing modes. Return covariance, visibility, and
// checked-effect narrowing are validated separately. A same-spelled method whose owner or generic
// parameter identity differs is not an override.
//
// Member hiding and overload merging operate by route and owner identity. An inaccessible candidate
// cannot win and cannot contribute inference evidence. A nearer non-callable member can own a name
// and prevent fallback to a hidden base overload. Rejected/deferred candidates remain distinguishable
// from an empty candidate set.

// 14. Dispatch and runtime identity
// ---------------------------------
//
// Source binding selects a declaration and records whether the call is statically bound, virtual,
// interface-dispatched, companion-qualified, or otherwise indirect. A generic method call adds the
// owner and method substitutions to that binding. The runtime target slot, if dispatch is required,
// must correspond to the canonical override contract after owner substitution.
//
// Type erasure is permitted only if all substituted operations have the same verified calling
// convention, object layout, GC descriptor, exception/effect behavior, and runtime type requirements.
// Otherwise closed specialization or a reified descriptor must preserve the distinctions. The
// compiler cannot select a method at runtime from an unvalidated display name or generic argument
// count.
//
// Virtual generic methods require a policy for generic arity and argument identity across overrides.
// A derived method cannot change the number or constraints of method parameters in a way that makes
// a base-typed call dispatch to a signature that was not validated at the static call site. Generic
// interface methods require equivalent interface-slot identity and implementation checks.
//
// The current native FIR subset does not lower generic owner dispatch, generic virtual methods,
// interface maps, or generic runtime descriptors. Source call binding and a correct overload choice
// do not prove dispatch code generation.

// 15. Visibility and generic evidence
// -----------------------------------
//
// Visibility is checked before a member can provide inference evidence or win overload selection.
// Declaration visibility and accessor visibility are independent. Protected access validates both
// the lexical derived owner and the static receiver category; a generic base projection does not
// bypass either check.
//
// Generic argument evidence can come from accessible parameter/result shapes, constructed receiver
// projections, explicit type arguments, expected result types, and contextual lambda/array facts
// permitted by the inference contract. It cannot come from invoking a getter, constructor, operator,
// default expression, or delegate provider during speculative analysis. Each candidate's evidence is
// isolated and committed only for the uniquely selected candidate.
//
// If the accessible candidate set is incomplete because lookup exceeded a resource limit, analysis
// cannot publish a partial winner. If an inaccessible overload would otherwise have matched, it
// must not be reported as the selected target and then rejected later by FIR.

// 16. Body rebinding is required before full member specialization
// ---------------------------------------------------------------
//
// Substituting type annotations into one already-bound body does not prove the body for a closed
// member instance. The concrete owner can change overload applicability, conversion ranking,
// property/accessor selection, dispatch, operator choice, checked effects, nullability, flow, definite
// assignment, borrowing, escape, and field layout.
//
//     class Runner<T>(val value: T) {
//         func run(): String = value.describe()
//     }
//
// The abstract body analysis may bind `describe` through a type parameter bound. A concrete
// `Runner<String>` and `Runner<Custom>` can select different implementations or have different
// effects. A correct generic instance pipeline must re-run the relevant semantic checks against the
// closed owner and method arguments before constructing verified FIR.
//
// Per-instance rebinding must preserve declaration and source-expression identity, but it must not
// reuse an abstract selected member when the concrete substitution changes the candidate set. Any
// cached body result is keyed by canonical owner/method identities, ordered arguments, source
// version, target and ABI contract, and analysis options that affect semantics.

// 17. Ref/out and storage identity
// --------------------------------
//
// `ref` and `out` parameters require exact source storage type identity after all owner and method
// substitutions. Numeric widening, nullable unwrapping, covariance, or boxing cannot make a
// mismatched location legal. A ref struct owner can impose additional lifetime restrictions.
//
// The verifier must prove that a lowered address belongs to the selected parameter and that its
// storage remains alive across the call. A member getter, operator result, temporary receiver,
// nullable safe-call result, or synthesized copy is not silently a writable location.
//
// For an `out` argument, assignment is required on every normal return from the selected call. If
// the generic body has conditional exits or checked exceptions, the proof uses the closed instance
// control-flow graph and the declared effects. A source binder's generic method selection alone does
// not prove a native address or post-call assignment.

// 18. Effects compose through members
// -----------------------------------
//
// Declared checked effects are substituted along with parameter and result types. The selected
// constructor contributes construction effects. A getter read, setter write, indexer operation,
// operator, delegated accessor, and virtual call contribute the effects of the exact selected
// member operation.
//
// A method override may narrow checked effects only under the exception hierarchy and compatibility
// rules. A generic effect parameter remains owned by its declaration. Two type parameters with the
// same spelling do not denote the same thrown type. Nullable wrapping or source null checks do not
// erase a checked effect.
//
// Source-body effects are computed from reachable operations and exceptional edges. Full native
// generic member support must rerun this analysis after substitution. Merely replacing E with a
// concrete Throwable subtype in the function signature is insufficient if the body selected a
// different getter, operator, or override.

// 19. Runtime layout and GC obligations
// -------------------------------------
//
// Every closed generic owner layout states its field order, size, alignment, reference slots,
// nullable slots, value-copy semantics, and type descriptor identity. The instance's descriptor must
// identify the exact constructed owner and every nested type that can contain references.
//
// A generic field may contain a managed reference, value, array, nullable value, function object,
// or ref-like representation. The collector's precise root map must trace only valid reference
// fields and must not interpret scalar payload bits as pointers. A nested `Pair<T, U>` layout can
// have different root positions when T and U differ; it cannot use the primitive layout as if all
// arguments were pointer-sized references.
//
// Stack maps, spill slots, object descriptors, array descriptors, handle tables, weak references,
// write barriers, and concurrent marking must agree on each concrete layout. A type argument that
// is nullable still describes a possible null reference/value; a null check refines a source value
// but does not mutate the static layout descriptor.
//
// Yellow Tea is a separate guest runtime concern. A host process collector and a guest process
// collector do not share object pointers or root ownership. Generic descriptor handles must never
// cross the process boundary as raw addresses. This contract does not broaden the runtime or claim a
// new collector implementation.

// 20. Artifact and export boundary
// -------------------------------
//
// Internal closed generic instances may be emitted as private implementation functions for a
// supported native executable. A generic source declaration is not itself a stable C ABI export.
// `cdylib`, `staticlib`, and `.solib` exports need an explicit closed signature, symbol naming rule,
// dependency identity, target/runtime ABI version, and import-validation contract.
//
// Open generic parameters cannot appear in a machine ABI parameter, return type, global, function
// pointer, exception record, or runtime callback. Exporting one requires a separately specified
// dictionary/reified ABI or a closed wrapper; neither is implied by native free-function
// specialization.
//
// AOT and ORC use the same verified source/FIR identity and LLVM 23.1.3 C++ API contract. ORC's
// process-isolated guest runtime does not relax generic owner, GC, or export checks. Default dynamic
// SolRT and explicit `--static-solrt-lib` link modes likewise do not change source generic identity.

// 21. Current implementation boundary
// ------------------------------------
//
// Current source analysis can provide facts for generic member lookup, constructed-base projection,
// candidate-local argument mapping, explicit/expected/input evidence, nominal constraints, and
// source effects. Those facts are useful to analysis clients and do not certify a native body.
//
// The current native FIR specialization slice accepts closed module-level generic free functions
// and directly called methods on stateless, non-generic, module-level objects when their argument
// types and body operations fit the verified native subset. Object methods are emitted without a
// receiver slot. Their call must use a direct singleton qualifier or an unqualified same-owner
// reference; a local or computed receiver is rejected. The object must have no bases, initialization,
// constructors, properties, or non-function members. Methods must have bodies, value returns, no
// operator/virtual dispatch, annotations, or this/super use. Omitted defaults follow the pure,
// non-vararg call-plan rules and may capture only earlier value arguments. Partial
// object parts are admitted only when the canonical owner satisfies the same restrictions.
//
// FIR records ordered method type arguments, emits one function instance per selected source
// declaration/type tuple, applies substitution to bound signatures and body expression types,
// limits expansion, verifies call identity/effects/origins/receiver syntax, and only then allows the
// existing backend to consume the verified graph.
//
// It does not emit a generic class/struct/ref-struct layout, a generic constructor, stateful or
// nested/companion owner method, generic property/indexer/operator, open owner body, virtual generic
// dispatch slot, generic closure, generic exported symbol, or runtime reified type descriptor.
// Unsupported owner/member native requests must receive an explicit diagnostic. They must not
// create an empty body, replace a selected member with a free function, or fall back to a different
// overload.

// 22. Required order for future implementation
// ---------------------------------------------
//
// The following milestones preserve phase ownership:
//
// 1. Validate partial-owner identity and merge every source part before binding members.
// 2. Resolve accessible receiver members and generic projections with bounded deterministic search.
// 3. Map source arguments and solve owner and method substitutions in isolated candidates.
// 4. Select one source member; retain canonical declaration identity, substitutions, origins, modes,
//    defaults, and effects.
// 5. Rebind and recheck the body under the complete closed owner/method substitution.
// 6. Validate override/dispatch slots, initialization, field layout, ref-like lifetime and escape.
// 7. Construct concrete FIR and independently verify every source binding and runtime invariant.
// 8. Generate exact GC descriptors, stack maps and runtime type metadata for the concrete layout.
// 9. Validate AOT/ORC, SolRT linkage, and each artifact/export boundary separately.
// 10. Publish status only for the concrete target/artifact/runtime combinations that passed.
//
// Skipping one stage cannot be compensated by an earlier inference result or a later LLVM module
// verifier. The first generic free-function slice remains intentionally narrower.

// 23. Normative error ownership
// -----------------------------
//
// Source name/visibility errors belong to member lookup and source binding. Constraint mismatch
// belongs to generic candidate analysis. Ambiguous projection belongs to constructed-base lookup.
// Unsupported concrete source shapes belong to native admission (`SOL2400`). A malformed or
// internally inconsistent FIR graph belongs to independent verification (`SOL2402`). Expansion
// beyond a bounded compiler budget belongs to specialization accounting (`SOL2403`).
//
// A diagnostics consumer must be able to distinguish source invalidity, source-deferred analysis,
// native unsupported status, verifier invariant failure, cancellation, and resource exhaustion. A
// failed owner/member specialization is never reported as a generic inference success merely because
// the binder found one source candidate.

// 24. Completion criteria
// -----------------------
//
// Generic owner/member FIR is complete only when all applicable items below are independently
// qualified:
//
// - owner-qualified type parameter IDs survive partial merge, rename, nested scopes, import aliases,
//   and compilation-unit changes;
// - owner and method substitutions compose without capture, mutation, foreign-workspace leakage, or
//   ambiguity;
// - constructors, properties, indexers, operators, delegation, and virtual/interface calls retain
//   exact source target identities and evaluation order;
// - per-instance body binding recomputes overloads, access, effects, flow, assignment, nullability,
//   constraints, borrow and escape facts;
// - value/reference/ref-like layouts use exact calling conventions, GC maps, barriers and descriptors;
// - generic instances have deterministic bounded keys, recursion handling, source origins, caches,
//   diagnostics, and cancellation behavior;
// - AOT, ORC, dynamic/static SolRT, and each library artifact boundary have separate qualification;
// - source examples and negative fixtures cover all owner/member shapes without being mislabeled as
//   passing tests until their oracles run.
//
// Passing free-function `identity<T>`, binding a generic property, or verifying a constructed owner
// type does not establish complete generic member specialization. The full declaration identity,
// concrete body, layout, dispatch, runtime metadata, and artifact boundary must all agree.
