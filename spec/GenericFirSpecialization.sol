// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative full contract; a bounded generic-free-function FIR/native slice is implemented.
module source.spec.generic_fir_specialization

// Bounded module-level generic-function specialization
// =====================================================
//
// This contract defines how a uniquely selected module-level generic function may become a closed
// function instance, pass independent FIR verification, and enter the Windows x86-64 native
// pipeline. It defines an admission boundary, not a promise that every Sol declaration can be
// specialized or emitted. Unsupported constructs remain source diagnostics; they never become
// placeholder FIR, empty bodies, unresolved LLVM values, or fallback machine code.
//
// Current implementation status:
//
// - Source analysis retains each selected call's generic type arguments in declaration order,
//   including expected-result and explicit-argument evidence already admitted by body binding.
// - FIR builds a closed monomorphic body for directly called module-level generic free functions,
//   composes an outer specialization into nested generic call arguments, and interns instances by
//   source declaration identity plus ordered type IDs. Exact recursive keys are reused; creation is
//   bounded to 4096 functions per compilation and 128 nested type levels.
// - Pure defaults on module-level free functions lower as call-plan values: explicit arguments are
//   evaluated once in source order, defaults follow in parameter order, and earlier formal reads use
//   captured call values. Effectful or unsupported default expressions remain outside the native slice.
// - The FIR verifier independently checks the source declaration, closed type arguments and
//   constraints, substituted signature/effects, selected call binding, exact target instance, and
//   argument origins. `FirJson` records function and call type arguments in schema 7.
// - Verified instances use the existing LLVM 23.1.3 C++ API path for AOT and ORC. This is code
//   generation from constructed LLVM objects, not LLVM text input.
// - This first slice substitutes the already-bound body facts; it does not rerun full source
//   overload/body analysis for each concrete instance. Generic owner/member specialization,
//   function values, unsupported native types and operations, and cross-container generic exports
//   remain outside the implemented subset. The full rebinding, cache, ABI and artifact guarantees
//   below are normative requirements and are not implied by this slice.
//
// A passing inference result or a `Bound` call binding alone is not a specialization proof. The
// examples mark implemented, rejected, and planned outcomes separately; normative `expect` cases
// do not claim that every illustrated program is currently emitted by Solc.

// 1. Terms and scope
// ------------------
//
// A generic declaration is a module-level `func` that declares one or more type parameters. An
// instantiation is one such declaration paired with one complete, ordered tuple of closed type
// arguments. A specialized body is the declaration body interpreted and verified with that tuple.
// A specialization graph is the finite transitive closure of selected generic calls reachable from
// an artifact's root functions. An ABI type is a closed source type with a defined native value or
// storage representation for the selected artifact and target.
//
// The initial contract covers only direct calls to module-level generic free functions in one
// explicit source compilation. Imported declarations may participate only after their source or a
// separately defined verified specialization artifact has been loaded and its identity/ABI checked.
// The present source SDK can bind direct imported names in supported analysis cases; that fact does
// not establish cross-container generic body consumption.
//
// The contract does not authorize specialization of generic class, struct, ref struct, interface,
// enum, object, constructor, property, accessor, operator, local function, or member declarations.
// Those require separate layout, dispatch, initialization, reflection, and ABI contracts. A
// module-level free function may mention a constructed nominal type in its signature only when the
// complete type is already valid and the eventual FIR/native verifier supports its representation.
//
// This contract covers direct statically selected calls. A generic function is not itself a
// first-class value. A function reference, delegate, closure, virtual dispatch, reflection lookup,
// or runtime type argument cannot request an instance dynamically. A caller with an open type
// parameter cannot create a native instance from that open parameter under this initial boundary.
//
// Type inference and generic body specialization are separate services. Inference determines one
// candidate's closed substitution from explicit type arguments and permitted evidence. The
// specialization service consumes only a uniquely selected candidate after the final call mapping,
// constraints, conversions, expected result, and applicable source-analysis obligations have been
// checked. It cannot redo overload selection with machine-code availability as a hidden preference.
//
// `bin` AOT and ORC are the initial native consumers of closed generic instances. Library export
// wrappers remain governed by the separate artifact contract. A public generic declaration is not
// a C ABI export. A library may use a closed generic instance internally only if the containing
// artifact can prove and package every required instance under its own artifact contract. The
// current `.solib` metadata does not expose a generic declaration as a stable cross-container
// callable ABI. Same-compilation source calls may use a closed instance inside an artifact, but
// that does not authorize another compilation to request it through a generic `.solib` export.

// 2. Normative pipeline
// ---------------------
//
// The implementation must preserve this observable order for every direct generic call:
//
// 1. Resolve lexical and module qualification, including shadowing, aliases, accessibility, and
//    the complete visible overload group.
// 2. Map positional, named, spread, ref, out, vararg, trailing-lambda, and omitted-default inputs
//    against each candidate's declaration without evaluating source expressions more than once.
// 3. Build candidate-local inference evidence from only the rules defined by the generic-inference
//    contracts. Apply explicit arguments and already-constructed owner arguments as fixed values.
// 4. Solve a complete substitution or retain a precise missing, conflict, ambiguity, deferred, or
//    resource result. Do not use partial substitutions as if they were selected.
// 5. Substitute the candidate signature simultaneously. Check every nested nominal constraint,
//    anti-constraint, exact ref/out requirement, conversion, and checked-effect obligation that is
//    required by the source-analysis contract.
// 6. Compare complete candidates with the ordinary overload-ranking rules. If a deferred candidate
//    could change the winner, do not select the currently best-known candidate.
// 7. After exactly one applicable candidate wins, create or reuse its canonical instance key.
// 8. Check whether the selected output kind, target, closed signature, body constructs, and all
//    transitive calls are within the verified-FIR/native admission contract.
// 9. Substitute the body, bind and verify that concrete body, then add its selected generic calls
//    to the specialization worklist.
// 10. Close and verify the full reachable instance graph before LLVM lowering or artifact commit.
//
// The source call's argument evaluation order is fixed by the call-binding contract and is not
// changed by specialization. Named argument reordering changes the formal-to-actual mapping, not
// the source evaluation order. Defaults are evaluated only after a winner is selected, in the
// defined parameter order, under the selected closed substitution.
//
// A failed body must not be made successful by dropping a call edge, effect, parameter, local,
// return, or control-flow branch. A failed instance cannot be replaced by a same-name overload or
// by an unspecialized generic declaration. The parent caller remains unverifiable if it depends on
// the failed instance.
//
// Specialization is a compiler-internal lowering operation. It adds no source-visible keyword,
// command-line switch, implicit module, entry selection rule, or runtime reflection facility.

// 3. Declaration and source identity
// ----------------------------------
//
// A generic parameter is identified by its canonical declaration owner and zero-based ordinal.
// Parameter spelling is diagnostic text only. Two declarations that each spell a parameter `T`
// own distinct identities. Compatible partial declarations, if ever admitted by this scope, must
// use the declaration-analysis canonical owner; this contract does not merge unrelated declarations
// because they have equal names or equal source paths.
//
// The generic function declaration identity is the canonical symbol identity produced by source
// declaration analysis. A callable name, source filename, line number, display signature, or
// overload ordinal alone is insufficient. File moves and input enumeration changes do not change
// the logical declaration identity when module and declaration ownership are unchanged.
//
// A function overload remains a distinct declaration even when its name and generic parameter
// names match another overload. The selected target is recorded before instance creation. No
// specialization key is constructed for a rejected, ambiguous, or unresolved candidate.
//
// Source `module` identity is the declared logical module, not an inferred folder path. The normal
// lowercase-directory/PascalCase-file convention is not an identity rule. Relative source layout,
// absolute checkout path, current directory, and machine username must not enter semantic identity.
//
// A specialized instance retains a source-origin link to its one generic declaration. Its body
// nodes retain the original node origin; the instance key and instantiation stack are additional
// context. A cloned node is not treated as a new user declaration and cannot acquire independent
// accessibility, overload, annotation, or partial-type identity.

// 4. Closed type substitution
// ---------------------------
//
// Let P be the finite ordered list of type-parameter identities declared by one selected generic
// function, and A the equally sized ordered list of selected concrete type identities. The
// substitution S maps each P[i] to A[i]. Explicit type arguments occupy declaration order. Inferred
// arguments occupy the same positions after the candidate solver has proved a unique solution.
//
// Substitution is simultaneous, not recursively textual. If S contains T -> U and U -> int, an
// original occurrence of T becomes U; an original occurrence of U becomes int. Replacements are
// not rewritten by a second substitution pass. This rule prevents owner/method capture and agrees
// with `spec/Generics.sol` and the inference validation contract.
//
// The substitution traverses every type-bearing position in a function signature and body:
//
// - value, ref, and out parameter types, including each source-to-formal mapping;
// - explicit result type, inferred expression result, local slot, and pattern binding types;
// - nominal generic arguments and each substituted base/interface projection;
// - dynamic and fixed arrays, fixed-array lengths, tuple elements, and nullable wrappers;
// - function parameters in order, each passing mode, result, and set-valued effect member;
// - constants, casts, type tests, operator selections, intrinsic selections, and call results;
// - declared, inferred, caught, thrown, and propagated checked-effect type identities;
// - flow facts, storage locations, capture candidates, borrow origins, and verifier metadata;
// - all nested types attached to source origins, diagnostics, and generated FIR nodes.
//
// A missing substitution for a function-owned parameter is an open instance and is not eligible
// for native code generation. A substitution value that contains any parameter identity is also
// open unless that parameter is itself replaced through a previously constructed owner map before
// the instance boundary. A source error type, bottom placeholder used as a recovery artifact, or
// unresolved nominal type cannot count as a closed concrete argument.
//
// Substitution preserves nullability, tuple arity and order, fixed-array length, function passing
// modes, and the mathematical set identity of effects. It applies the existing nullable
// normalization: specializing `T?` with `int?` yields one nullable layer, not `int??`. It does not
// widen numeric types, infer a common type by spelling, box a struct to satisfy Object, or erase a
// ref-like lifetime requirement.
//
// Every constructed nominal type nested in a signature or body must be revalidated after
// substitution. It is not enough that the outer function parameter was valid before substitution.
// A substituted nested type that violates any declared invariant, base/interface constraint,
// storage rule, or ref-like anti-constraint invalidates that candidate or instance.
//
// Effect sets are unordered identities. Substitution replaces each member while preserving set
// semantics and removing duplicate identities only where the language type model defines a set.
// It must not pair generic effect parameters by the implementation's hash/list enumeration order.
// If the source call's checked effects cannot be determined under the selected closed instance,
// native admission fails closed.
//
// The type interner's workspace ownership remains mandatory. All produced closed identities must
// belong to the current analysis workspace. Numeric IDs from another workspace, a serialized
// inspection file, or a previous process are never valid substitutes for canonical type identity.

// 5. Instance key
// ---------------
//
// Instance equality is structural and exact. Two instance requests are equal if and only if they
// identify the same canonical generic declaration and the same ordered tuple of closed canonical
// type identities under the same semantic compilation environment. Source parameter names and the
// order in which calls discover the instances do not affect equality.
//
// A per-compilation semantic key contains at least:
//
// - canonical module identity and canonical function declaration identity;
// - declaration generic arity and ordered closed argument type identities;
// - logical source compilation identity needed to resolve private/imported dependencies;
// - language edition and semantic feature set that can alter type or body meaning;
// - target architecture and platform ABI family when layout/calling convention affects legality;
// - Sol runtime descriptor/ABI version for any generated runtime interaction;
// - the compiler's semantic identity for persistent caches, but not for in-memory equality.
//
// For a single build, compiler version, target triple and runtime ABI are compilation-wide facts;
// they may be stored once in an enclosing cache namespace. An implementation may omit them from a
// local dictionary key only when the dictionary cannot outlive or cross that namespace. It must
// never reuse a native-code entry across a different target, runtime ABI, feature set, or semantic
// compiler revision.
//
// Inference evidence, argument spelling, source argument order after mapping, expected-type path,
// diagnostic state, call-site span, and overload enumeration position are not part of instance
// identity after they have produced the selected declaration and closed type tuple. They remain
// provenance for diagnostics and call-site mapping.
//
// Parameter types, return type, effects, constraints, body syntax, and attributes are not repeated
// as independent identity fields when they are derivable from an immutable canonical declaration
// plus the source snapshot. A cache content key must nevertheless include the digest of every
// semantic input that can change those values, including transitive source and compiler-owned
// declarations that the body binds against.
//
// Display names are not cache keys. Concatenating `T.Name`, `ResolvedType.Id`, or textual signature
// display strings is insufficient. A key encoder uses tagged, length-delimited canonical
// components so that `A<BC>` cannot collide with `AB<C>`, and distinguishes signed/unsigned,
// nullable/non-null, tuple/function, array/fixed-array, and effect-set identities.
//
// A stable persistent key is a digest over the canonical encoded key and all content inputs. The
// digest algorithm and cache envelope version must be explicit. A digest hit is only a lookup
// hint: the compiler verifies that the cache record's complete semantic key and content digests
// match before reusing code or verified FIR. A hash collision cannot authorize reuse.
//
// No on-disk specialization cache schema or cross-version cache compatibility is implied by this
// source contract. Until such a schema is versioned, specialization caches are compilation-local
// and discarded after the build. FIR JSON schema 7 records ordered function and call type arguments;
// `.solib` schema 2 is not silently
// extended by writing unversioned generic records into them.

// 6. Candidate selection and call identity
// ----------------------------------------
//
// The selected generic target is determined using the generic inference and overload contracts.
// In particular, all potentially better deferred candidates remain blockers. The compiler does
// not prefer a nongeneric target merely because a generic body has not yet been implemented, and
// it does not prefer a generic target because a speculative specialization appears cacheable.
//
// Explicit generic arguments must have exact arity and declaration order. They fix the named
// function's parameters but do not waive bounds, conversions, passing-mode checks, body safety, or
// native ABI admission. A type-argument arity or bound error rejects that candidate with its
// source origin; it cannot truncate the list or fall back to inferred arguments.
//
// Inference uses only evidence allowed by the language contracts: mapped value/ref/out arguments,
// expected results where the enclosing binder supplies one, and explicitly supported literal,
// array, lambda, projection, or effect evidence. Omitted defaults are never inference inputs.
// Empty varargs and empty array literals do not manufacture an arbitrary `Object` type.
//
// A generic target is specialized only after the final argument mapping is immutable. The
// specialization rewrites each source call's formal argument index, passing mode, and substituted
// type consistently. Named argument order does not change evaluation order. A ref/out call requires
// exact eligible storage and cannot be converted or boxed into a different address type.
//
// User-defined conversions, open generic function values, dynamic dispatch through generic
// signatures, and unresolved contextual lambda analysis remain deferred or rejected according to
// their source contracts. Backend reachability is not evidence that an expression type checks.
//
// Generic and nongeneric overloads compete under the same source ranking rules. If two candidates
// are equally best, the call is ambiguous before specialization. If one candidate wins but its
// body is unsupported for FIR/native, report that boundary at the selected candidate/call; do not
// rerun ranking and silently select a worse candidate. This keeps source semantics independent of
// target-specific code availability.
//
// One candidate's partial substitution, lambda body result, array element join, constraint result,
// cache hit, or body-analysis facts cannot leak into another candidate. Candidate-owned state is
// discarded or retained for diagnostics according to the candidate transaction contract.

// 7. Constraints and closed-body validity
// ---------------------------------------
//
// After substitution, validate every inline and `where` constraint on the generic declaration.
// Every bound applies; declaration order does not choose a preferred bound. Constructed
// inheritance/interface edges are projected with the complete owner substitution and compared by
// canonical constructed identity. Missing metadata or a bound that cannot be proven is
// indeterminate, not satisfied.
//
// `allows ref struct` is an anti-constraint. It permits an otherwise admissible ref-like argument;
// it does not prove that the body may box, heap-store, capture, return across a forbidden lifetime,
// place the value in a managed array, or pass it to an Object-requiring operation. The specialized
// body is checked for each such use. The initial native ABI subset below excludes ref-like types.
//
// `T: std.core.Object` requires a reference-type satisfaction under the declared constraint
// rules. Boxing a primitive or ordinary struct is not synthesized to make the bound succeed.
// Constrained interface dispatch may operate on an admitted concrete value without creating a
// boxed interface object only when a separate verified lowering rule proves that representation.
// No such general constrained-dispatch lowering is claimed by this contract.
//
// Every operation in the body is checked against the substituted operand and result types. The
// compiler cannot prove `T + T`, member access on T, indexer use, conversion, equality, or a
// constraint-provided operation merely because one call site happens to pass a type that supports
// it. It must either resolve the operation through a declared constraint with a verified lowering
// or reject the body. One call site cannot specialize a declaration under assumptions absent from
// its declared bounds.
//
// A function body that uses only concrete substitutions may still fail native admission because
// of unsupported source operations, storage, control flow, effects, or transitive callees. Closed
// types are necessary and are not sufficient for body verification.
//
// Type-dependent constant expressions are reevaluated under the closed substitution using checked
// source arithmetic and the existing literal domain. The compiler does not inherit a previously
// folded value if its type, overflow behavior, or selected overload can differ after substitution.
// Non-constant runtime expressions retain source evaluation count and order.

// 8. Body specialization and rechecking
// -------------------------------------
//
// The body is associated with one immutable source declaration snapshot. Specialization creates a
// compiler-owned instance representation; it does not rewrite the source AST or mutate shared
// `BodyAnalysis`, lexical scopes, candidate tables, or another instance's nodes.
//
// Before a body enters FIR, the implementation must:
//
// - instantiate every source signature and local storage type with the complete simultaneous map;
// - bind each identifier, overload, intrinsic, operator, accessor, and call under the closed types;
// - recheck definite assignment, reachability, return completeness, and every out parameter;
// - recheck nullability, smart-cast validity, mutation invalidation, and every type test/cast;
// - recheck checked-effect propagation and handler/cleanup control-flow obligations;
// - recheck ref/out locations, borrow lifetimes, ref-like escape and capture restrictions;
// - recheck type-dependent overload selection and conversions, including constraints and defaults;
// - reject any unresolved or candidate-local recovery node that survives the selected body;
// - preserve all reachable source branches and the defined argument/default evaluation order;
// - account for every direct generic call as an edge to a closed instance key or a defined intrinsic.
//
// The body proof is per instance. A source-level check under an abstract T cannot be reused as a
// proof for an operation whose validity depends on the concrete type unless the source analysis
// explicitly proves that operation through T's declared constraints. A check performed for
// `T = int` is not reused for `T = String`.
//
// This initial contract accepts only code where body specialization produces the same observable
// Sol evaluation semantics as the source declaration. It does not permit constant specialization
// to remove observable calls, reorder side effects, skip default evaluation, or bypass checked
// effects. Ordinary dead-code elimination is a later semantics-preserving backend optimization.
//
// Runtime generic type tests, reified parameters, `typeOf<T>()`, generic reflection, and layout
// inspection are unsupported. Generic specialization is compile-time only; no type-argument token
// or reified descriptor is passed to the SolRT ABI by this contract.
//
// A body may be analyzed after its key is reserved to close recursion, but it cannot be published
// as verified until all required edges, parameter/result types, and the full reachable graph are
// closed and validated. A reserved incomplete node is an internal worklist state, never a cache hit
// or successful FIR function.

// 9. Specialization graph and recursion
// -------------------------------------
//
// Graph nodes are exact instance keys. A direct call from instance I to generic declaration G
// first resolves the source call candidate and its complete argument substitution; it then yields
// target key K(G, arguments). A call to the same declaration with the same tuple reuses the existing
// graph node. A call with a different tuple is a different node even if its machine ABI happens to
// be identical.
//
// Ordinary source recursion with a stable type tuple is legal if the source function and native
// backend support recursion. On encountering an already reserved exact key, the worklist records a
// back edge and does not recursively copy the body without bound.
//
// Mutual recursion follows the same rule. For example, `even<T>` calling `odd<T>` and `odd<T>`
// calling `even<T>` creates two nodes for each distinct T tuple and a finite strongly connected
// component. Each member is independently verified before the component is eligible for lowering.
//
// Type-growing recursion creates distinct keys and must terminate through the specialization depth
// or total-instance budget. For example, a body that calls itself with `[]T` produces
// `f<int>`, `f<[]int>`, `f<[][]int>`, and so on. A visited declaration name is not a sufficient
// cycle check and cannot turn this expanding chain into one instance.
//
// Type arguments must be finite acyclic identities. Source equation cycles are handled by the
// inference/occurs-check contract before instance creation. Corrupt recursive metadata, a self-
// referential interner node, or an invalid substituted graph produces a compiler diagnostic or
// internal invariant failure; it is never accepted as a regular recursive type by this contract.
//
// Every edge stores its source call origin and caller instance key. If a nested instance fails,
// diagnostics can report both the failing declaration/body location and the deterministic chain of
// call sites that required it. Repeated identical edges may be coalesced for graph storage but
// provenance must retain enough information to explain each reachable root path under the
// diagnostic cap.
//
// Graph traversal order is canonicalized for output and diagnostics. It cannot decide overloads,
// satisfy a constraint, resolve a cycle, or choose one body when multiple candidates remain.

// 10. Resource bounds and cancellation
// -------------------------------------
//
// Planned hard defaults are: 128 active instantiation edges, 100,000 distinct instances,
// 16,000,000 specialized body nodes, and 4,096 structural type nodes per signature/body root.
// These are separate from the TypeSpecialization interner and inference budgets. Counting,
// cancellation, failure atomicity, and resource diagnostics are normative in
// [GenericFirSpecializationLimits.sol](GenericFirSpecializationLimits.sol).

// 11. Native ABI admission
// ------------------------
//
// ABI admission is evaluated after substitution and before native lowering. The target is Windows
// x86-64 under the current LLVM 23.1.3 backend. A successful semantic substitution does not make an
// unsupported source type ABI-compatible.
//
// For the current verified-FIR bootstrap, the closed scalar source types recognized by the verifier
// are `bool`, `sbyte`, `byte`, `short`, `int`, `uint`, `long`, `ulong`, `single`, and `double`.
// `byte` is unsigned. `ubyte` is not a Sol built-in. `half` and `quad` are not in this admitted
// native subset. This is the current verifier boundary, not a claim that every operation or
// external ABI supports every listed scalar in every position.
//
// The bootstrap verifier also admits `std.core.String` as a managed reference and a one-level
// dynamic array whose element is one of those admitted scalars or `std.core.String`. It does not
// admit nested arrays, arbitrary class/struct values, arbitrary nominal references, nullable
// wrappers, fixed arrays, tuples as stored values, function values, or ref-like types as native
// slots. All types and operations must pass the exact verifier rules for the selected node shape.
//
// The empty tuple `()` is the source unit result supported by entry and ordinary function return
// lowering. It is not a storable parameter/local type, a native pointer, or an exported C value.
// The bottom type `!` cannot be used as a native slot or instance argument. A bottom-returning
// operation is admitted only when a separate verified control-flow lowering rule supports it; the
// current generic specialization contract does not add that rule.
//
// The executable entry remains exactly one `Main` object method `main(args: []String): ()`. Generic
// specialization does not provide an alternate entry, an entry type argument, or a new `--entry`
// selector. Generic functions can be reached only through ordinary statically selected calls from
// that entry graph or another defined artifact root.
//
// Scalar `ref` and `out` ABI locations are admitted only where the current verifier and lowerer
// already prove exact source storage, passing mode, and scalar representation. A generic ref/out
// argument substitutes to an exact admitted scalar type; it is not converted, boxed, copied into a
// temporary, or widened to a pointer with unrelated provenance. String/array/ref-like byref
// specializations remain unsupported until their lifetime and GC-root ABI is separately verified.
//
// The generic instance's internal Sol calling convention is distinct from the C export convention.
// A native `bin` or ORC program may use an internal closed String or dynamic-array instance if the
// verified FIR and runtime-root rules admit every use. The `cdylib`/`staticlib` C wrapper contract
// remains narrower: generic exported functions, String/Object/array references, Boolean
// representation ambiguity, half/quad, checked exception escape, and unsupported receivers are
// not made exportable by specialization.
//
// `solib` external module consumption and generic instance import remain outside this contract.
// A container must not claim a generic declaration can be linked by an external consumer until a
// separately versioned export, constraint, dependency, ABI, and cache contract exists.
//
// The native runtime layout version is provisional ABI 0. A specialized instance that interacts
// with String, arrays, roots, barriers, or any SolRT symbol must use the exact runtime contract
// checked by the compilation and guest. Specialization does not stabilize ABI 0 or make a mismatch
// safe. The compiler and the separate ORC guest retain their process-isolation boundary; no CLR
// object or managed type identity is passed to guest code.

// 12. Effects, exceptions, and cleanup
// ------------------------------------
//
// The closed function signature retains the substituted unordered set of declared effect types.
// Each effect must remain a workspace-owned subtype of `std.core.Throwable`, subject to the
// checked/unchecked hierarchy contract. `RuntimeException` descendants remain unchecked; other
// escaping `Throwable` descendants require source handling or a declared caller effect.
//
// Source-level checked-effect proof is rerun for the concrete body and every direct call. A generic
// body cannot use the fact that one observed call site happens not to throw as a replacement for its
// declared abstract contract. Conversely, a checked effect that becomes impossible only after
// concrete specialization may be removed from the instance's inferred escape set only if the
// source analysis formally proves it and preserves declaration-signature rules.
//
// The current native subset does not lower source `catch`, exceptional CFG edges, `finally`,
// `defer` cleanup, general throwing operations, or Windows source-level exception propagation.
// A generic instance that requires any of these operations is rejected at the FIR/native gate,
// even if source analysis has already modeled its effects. Native C++ exception carriers in SolRT
// do not implement the Sol source exception semantics.
//
// `print`, `println`, `error`, and `errorln` keep their compiler-owned adapter identity and ordered
// argument behavior. A generic call that specializes to a supported adapter input is still checked
// against the trusted runtime declaration and exact adapter signature; matching a name is not
// enough to select an intrinsic.

// 13. Verified FIR requirements
// -----------------------------
//
// A generic instance enters LLVM lowering only as part of a `VerifiedFirProgram` minted after the
// complete source graph has closed and the independent verifier has accepted every function. The
// builder's unchecked FIR is not a code-generation capability.
//
// Each emitted specialized function has a unique compilation-local FIR function identity. Every
// call edge targets exactly one function identity in that same verified program or an exact
// compiler-owned runtime adapter. A call cannot retain a generic declaration ID when the call
// requires a closed instance.
//
// Verification checks that:
//
// - no function-owned type parameter remains in parameters, result, effects, slots, expressions,
//   calls, flow facts, constants, intrinsic metadata, or nested type applications;
// - every instance key has the declaration's exact arity, owner identity, and closed type arguments;
// - each argument type equals its selected parameter type after the verified source conversion or
//   exact ref/out rule; named-call parameter mapping and source origins agree;
// - every body node is anchored to the original source declaration and its specialized closed type;
// - every nested nominal type and every generic constraint has a completed valid proof;
// - all call targets belong to the closed graph and no unresolved, deferred, or error target exists;
// - return types, out assignment, reachability, definite assignment, and checked effects are valid;
// - runtime adapters are compiler-owned and their exact signatures match the emitted call;
// - the function's body operations, slots, layouts, roots, and ABI types are supported by the target;
// - every reachable generic instance is present once per key and every reachable nongeneric target
//   is present once per canonical function identity;
// - the selected entry and library-root rules remain unchanged by instance expansion.
//
// The verifier must not accept an instance because the builder previously reported success. It
// checks the concrete specialized records independently, including all edges and source anchors.
// If verifier inputs are mutated after verification, the old capability cannot be reused as proof
// for the modified program. A changed program requires a fresh verification pass.
//
// `FirJson` is an inspection representation, not a source of trusted verified capability. If
// instance identities or origins become externally visible in Fir JSON, the schema version must
// increase and its deterministic ordering and omission rules must be documented. Deserializing
// Fir JSON cannot construct a `VerifiedFirProgram`.

// 14. LLVM lowering and artifacts
// -------------------------------
//
// LLVM lowering consumes only verified closed FIR. It constructs LLVM operations through the pinned
// LLVM 23.1.3 C++ API bridge. It does not emit LLVM assembly text and ask LLVM to parse that text as
// the compilation path. LLVM text remains printer output for inspection.
//
// All instance functions are declared in the native module before call bodies are emitted so that
// direct and mutual recursion resolve to exact native symbols. Linkage and visibility follow the
// existing artifact rules. Internal specialization names are collision-free, deterministic within
// the artifact, and independent of filesystem paths, random IDs, or worklist arrival order.
//
// Optimization may merge machine functions only after the compiler has maintained distinct source
// instance identities and equivalent verified semantics. Code folding cannot merge mismatched
// source origins, generic substitutions, effect metadata, or debug mappings in the inspection data.
//
// AOT object emission, embedded LLD linking, SolRT dynamic/static linkage, and ORC bitcode transfer
// retain their existing transactions and validation. A failure before successful artifact commit
// leaves any previous destination untouched. ORC continues to execute in the separate native
// `sol-exec-host.exe` process. Generic specialization cannot introduce an in-process guest call.
//
// `--artifact-type=bin|cdylib|staticlib|solib`, `--static-solrt-lib`, and `-C llvm-compiler=aot|orc`
// keep their independent meanings. This contract defines no additional CLI option. If selected
// instance closure exceeds a target capability, Solc diagnoses the selected source and emits no
// misleading partial artifact.

// 15. Caching and reuse
// --------------------
//
// Within one compilation, a completed verified instance may be reused by every call with the same
// exact instance key. A pending instance may be referenced to close recursion but cannot be read as
// a completed body. A failed instance result is retained only as a failed result for that exact
// compilation context; it is never reused as verified code.
//
// The implementation may separately cache source binding, specialized FIR, LLVM modules, object
// files, or linked artifacts. Each layer has its own key and validation boundary. A verified-FIR
// cache hit must still be bound to the exact source snapshot, semantic options, declaration
// identity, substitution, runtime/target contract, and verifier version. An object cache hit must
// additionally match target features, LLVM version, codegen options, and all linked runtime inputs.
//
// Diagnostics, source locations, and provenance are not discarded on a machine-code cache hit.
// The cache record either retains origin maps from the exact content inputs or rebuilds them from
// the current source snapshot. A machine object alone cannot answer a new source diagnostic.
//
// Invalidation is transitive. A change to a generic body, one of its selected callees, a constraint
// declaration, an interface projection, a trusted intrinsic declaration, runtime ABI, compiler
// semantics, or relevant codegen option invalidates the affected dependent instance and artifact.
//
// Cache lookup cannot change overload resolution, diagnostics' source ownership, candidate
// applicability, resource accounting, selected instance keys, output bytes, or artifact metadata.
// Cold and warm builds with equal inputs produce equal observable outputs and equivalent maps.
//
// Concurrent requests for the same instance coordinate one canonical creation. They cannot build
// two instances with different IDs and let task scheduling choose which one a call targets. Cache
// synchronization must be cancellation-safe and must not expose partially initialized FIR or LLVM
// state to another compiler task.

// 16. Determinism and reproducibility
// -----------------------------------
//
// Given the same source bytes, explicit source set, compiler/LLVM/runtime identities, target,
// options, budgets, and environment-independent inputs, the specialization phase produces the
// same set of instance keys, selected targets, diagnostics, verified FIR ordering, source-origin
// maps, and artifact metadata.
//
// Input file ordering, declaration hash-table order, candidate enumeration order, generic
// parameter spelling, worker scheduling, cache hit/miss, and dictionary/set iteration cannot alter
// semantic results. The implementation sorts presentation and serialization by canonical
// declaration identity and canonical encoded type tuple after selection; sorting does not break a
// semantic tie.
//
// If stable LLVM/object bytes are not yet guaranteed by LLVM or linker behavior, the compiler still
// guarantees deterministic semantic instance graphs and manifest metadata. It must not advertise
// byte reproducibility beyond the measured artifact layer.
//
// Internal function ordinals are assigned from a stable canonical ordering after graph closure, or
// are hidden from observable output and proven not to affect reproducibility. Worklist insertion
// order is not an ABI or stable symbol contract.
//
// Source paths are represented only through the existing source-origin map and configured path
// remapping. Absolute machine-local source paths, username, checkout location, and temporary
// directory names do not enter instance keys, mangled symbols, deterministic cache content, or
// public artifacts.

// 17. Diagnostics and failure classification
// --------------------------------------------
//
// Diagnostics preserve the phase that owns the failure. They distinguish source candidate
// rejection, unresolved/deferred analysis, selected-body semantic failure, FIR verification
// failure, ABI admission failure, resource exhaustion, cancellation, and backend failure. One
// category must not be rewritten as another merely to simplify recovery.
//
// Existing source-analysis outcomes retain their current meanings: unresolved direct function,
// no applicable candidate, ambiguous candidate, and bounded specialization failure use their
// documented call-analysis diagnostics. Existing `SOL2134` identifies bounded specialization
// failure in generic/call analysis; it is distinct from the native FIR instance budget.
//
// Existing `SOL2400` identifies a construct outside the verified native subset. A generic source
// body that is otherwise well-typed but not admitted to current FIR/native support is reported at
// the body construct that blocks emission. It is not reported as a syntax error or as successful
// generic inference.
//
// Existing bounded analysis exhaustion remains in its designated resource family (including
// `SOL2290` where applicable). FIR specialization resource exhaustion uses `SOL2403`; `SOL2402`
// remains reserved for independent FIR verification rejection, and `SOL2400` denotes an unsupported
// native construct. These phase-owned outcomes are not interchangeable.
//
// A diagnostic for a failed specialized operation includes the source body span, selected
// declaration, concrete type arguments in declaration order, and a bounded call-chain note from a
// root. A failed constraint names the constraint declaration and the concrete argument that failed.
// A resource diagnostic includes limit and observed work. A target-incompatible type names the
// closed type and the artifact/ABI position that rejected it.
//
// If a body contains multiple unsupported constructs, diagnostics are stable under input order and
// candidate hash ordering. The primary diagnostic points to the earliest blocking source origin
// under the defined source ordering; secondary notes are capped but the failure cannot be hidden.
//
// A rejected generic candidate does not produce a body-specialization diagnostic before it becomes
// the selected candidate, unless the message is strictly a candidate note needed to explain why no
// overload applies. A losing candidate's unsupported body cannot invalidate a different unique
// source winner.
//
// Cancellation is not a source diagnostic. Internal verifier inconsistency is not downgraded to
// unsupported source syntax. A missing runtime library is not a generic constraint failure.
//
// Failure is transactional. No executable, library, `.solib`, or ORC payload is reported successful
// if one reachable required instance failed. Staging files may be cleaned after failure but may not
// overwrite an existing successful output.

// 18. Origin mapping and inspection
// ----------------------------------
//
// Each specialized FIR node maps to the original source node that authored it. Synthetic prologue,
// runtime adapter, and lowering nodes carry an explicit synthetic-origin kind plus the source call
// or declaration that required them. They do not masquerade as user-authored source expressions.
//
// A node's primary origin is stable across different instance keys when it represents the same
// source node. Instance metadata separately records the canonical generic declaration and ordered
// concrete type arguments. A user can therefore distinguish “the expression in the body” from
// “the call that instantiated this body.”
//
// For a diagnostic caused inside an instance, the body span is primary and the instantiation call
// chain is attached as notes in outermost-to-innermost or another explicitly documented stable
// order. For an argument-to-parameter mismatch at a call site, the call argument is primary and the
// callee declaration/instance is a note. For an ABI rejection of a result, the result-producing
// body expression is primary when known; otherwise the function declaration is primary.
//
// Byte spans refer to the immutable UTF-8 source snapshot already used by CST/AST diagnostics.
// Repeated specialized instances may point to the same source span; the instance key is additional
// diagnostic context and not a fabricated source file.
//
// Inspection output serializes canonical type descriptions or compilation-local references only
// under a versioned schema. It does not serialize raw object references, unstable hash codes,
// foreign workspace IDs, or trusted “verified” flags that a consumer could forge.
//
// Debug information must map machine instructions back to the original function body and source
// call chain while preserving instance identity in the available symbol/debug metadata. Debug
// mapping does not change the runtime ABI and cannot expose a private filesystem path by default.


// See [GenericFirSpecializationBoundaries.sol](GenericFirSpecializationBoundaries.sol) for the remaining contract.
