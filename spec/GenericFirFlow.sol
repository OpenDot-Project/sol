// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative source-flow and specialized-FIR contract; current per-instance semantic rebinding is incomplete.
module source.spec.generic_fir_flow

// Generic flow through specialized FIR
// =====================================
//
// This contract defines how declaration-level generic flow, concrete FIR control flow, and
// execution behavior relate. It supplements GenericFirSpecialization.sol and does not widen its
// native admission boundary. In particular, a valid source-level flow result is not proof that a
// generic body can be rebound, verified, lowered, or executed for every possible type argument.
//
// Current implementation snapshot:
//
// - Source BodyAnalysis analyzes a generic function body with its declaration-owned type
//   parameters. It computes ordinary source diagnostics and flow facts once for that declaration.
// - For an admitted closed module-level generic free function, FIR substitutes type facts and
//   creates a concrete body whose locals, call signatures, array element types, and parameters are
//   closed. Exact instance keys are worklist-interned; nested generic calls receive composed closed
//   arguments.
// - FirVerifier builds a control-flow graph for each specialized body and proves slots after type
//   substitution. The proof is independent of the source BodyAnalysis result and does not trust a
//   serialized `verified` flag.
// - The current pipeline does not rerun the complete source binder/semantic analyzer on each
//   concrete instance. A generic body operation, member, or nongeneric call whose validity depends
//   on an open type parameter is rejected by the bounded body-surface gate (SOL2400), even if a
//   particular hypothetical substitution could make it valid.
//
// The rules below distinguish required language behavior from currently admitted lowering. The
// normative examples specify outcomes for future conformance; individual comments identify cases
// that the current FIR/native slice can or cannot accept. No operation may be silently erased to
// turn an unsupported body into a verified one.

// 1. Terms and analysis layers
// ----------------------------
//
// A declaration parameter is one type parameter owned by one generic declaration. Its identity is
// the declaration identity plus its ordinal, not its spelling. A closed substitution maps every
// declaration parameter to one concrete, supported source type. A specialized body is the generic
// body represented with one such substitution. Two instances of one declaration have separate
// local-slot identities and CFGs even when their machine representation happens to be equal.
//
// Source flow is the source analyzer's reachability, definite-assignment, mutability, nullability,
// return, ref/out, and effect analysis of the source body in its lexical declaration context.
// Source flow sees an open type parameter as a type identity with its declared constraints. It does
// not specialize that parameter to a concrete runtime value.
//
// FIR flow is the verifier's proof over a particular closed FIR body and its typed slots. It sees
// substituted types and lowered operations, and must validate every reachable CFG edge. It cannot
// repair a failed source call binding or invent a type-dependent source operation which was never
// bound.
//
// Runtime behavior is the result of executing emitted machine code for a verified instance. It
// includes concrete values, branches taken, loop iteration counts, exceptions, ref/out writes, and
// array contents. Static flow proves safety over paths; it does not predict which path a particular
// execution will take.
//
// These layers are related by evidence, not equivalence:
//
// 1. Source analysis selects declarations, maps arguments, solves type arguments, and checks the
//    open declaration body under source rules.
// 2. FIR substitutes only a selected, closed instance and preserves the source body structure.
// 3. The independent FIR verifier checks closed types, call edges, slots, CFG paths, and operation
//    forms against the source-origin data and specialization map.
// 4. The backend may consume only the verified graph and emits behavior for its concrete target.
//
// A successful result at one layer never substitutes for the next layer's proof. In particular,
// “the generic body is flow-safe” does not imply “the closed operation has a native lowering.”

// 2. Flow-state model
// -------------------
//
// A flow state is associated with a program point and one path class. It records at least:
//
// - each local and parameter slot's declared, inferred, or substituted type;
// - whether a readable value has been definitely assigned on every incoming normal path;
// - whether a `val` may still receive its single permitted assignment;
// - whether a `var` value can be read and what refinements remain valid;
// - nullability and other value refinements tied to a particular storage identity;
// - reachability and successor kind (fallthrough, return, break, continue, or throw);
// - ref/out writes that have definitely completed on the path;
// - active loop target, lexical owner, cleanup, and effect context when applicable.
//
// A join is computed over all reachable incoming edges of the same continuation kind. A normal
// join does not include return, break, continue, or exceptional edges that leave the current
// region. An unreachable edge contributes no facts. If no normal predecessor reaches a point, the
// point is unreachable and a later expression cannot make it reachable by producing a value.
//
// Definite assignment is a must property: a slot is definitely assigned at a join only if every
// reachable predecessor assigns it. For a newly declared `val`, single-assignment permission
// follows the language's path-sensitive write-once rules; two mutually exclusive writes may each
// be valid while a second write on one path is rejected. A `var` is writable after declaration,
// but a read still requires definite assignment.
//
// Facts about different declaration-owned generic parameters are never merged by matching their
// source spellings. A slot type may mention one or more owner-scoped parameters; the type identity
// and substitution provenance remain attached to that slot through joins and FIR projection.

// 3. Generic values and pass-through locals
// -----------------------------------------
//
// A generic parameter is a statically typed value parameter. Passing a value of type `T` to a
// parameter of type `T`, returning it as `T`, or copying it into a local declared as `T` does not
// erase its type or make it dynamically typed. The local is a distinct storage slot with a type
// expression containing the same declaration-owned parameter.
//
// Assignment transfer for a pass-through local is ordinary assignment:
//
// - `val copy: T = value` creates an assigned immutable slot of type `T`;
// - `var copy: T = value` creates an assigned mutable slot of type `T`;
// - `var copy: T` creates an unassigned mutable slot of type `T`;
// - `copy = value` marks the slot assigned only on the normal edge where the assignment completes;
// - a later read is legal only where every reachable normal incoming edge has assigned the slot.
//
// An initializer that is a direct parameter read, local read, selected generic call, array element,
// or other already bound expression carries the type identity established by source binding. FIR
// substitutes that expression type and slot type together. A verifier mismatch between the
// substituted expression and target slot is an invalid FIR graph, not a conversion inferred during
// verification.
//
// The local's name has no effect on specialization identity. A local named `T` does not shadow the
// type parameter in type syntax; value and type lookup follow their own lexical namespaces. A local
// can be assigned a different expression only when ordinary type compatibility and mutability
// rules permit it. Substitution must not turn a previously rejected source assignment into an
// accepted one.
//
// Open generic operations are a separate question. `val copy: T = value` requires no operation on
// the representation of T and is the intended pass-through case. `copy + value`, a member access
// such as `copy.length`, a conversion, or a nongeneric call taking `int` may require facts that do
// not exist for an open T. These require source-level constraints or future per-instance rebinding;
// an eventual concrete type must not retroactively legalize the generic declaration in the current
// implementation.

// 4. Assignment joins inside generic declarations
// -----------------------------------------------
//
// Substituting a type does not alter control-flow topology. The same source branches produce the
// same abstract predecessor edges in a specialized clone unless a future language rule explicitly
// defines type-directed compile-time control flow. There is no such rule in this contract.
//
// For an `if` with a reachable then and else edge, a variable is assigned after the expression only
// if both normal branches assign it. If a branch returns or throws, that terminal edge does not join
// the post-`if` normal state. If there is no `else`, the condition-false edge is a predecessor and
// retains the pre-branch assignment state.
//
// An expression-valued branch must satisfy the result type and definite-result requirements on
// every normal value-producing arm. A terminal arm contributes no value. The generic result type
// `T` does not allow an arm to omit a result merely because the eventual instance could be `()` or
// another representation; type substitution is applied only after source binding chose a valid body.
//
// Branch refinements are joined using the ordinary source-flow rules. A test of a `T` value only
// proves facts allowed by the type system and operators used in the test. It cannot assume that T
// is a reference, array, nullable type, or numeric type unless the constraint and language rule
// establish that fact.
//
// Side effects in one branch invalidate mutable storage facts only along paths that execute those
// effects. Joining a path that wrote a `var` with one that did not retains only facts common to both
// paths. The FIR verifier recomputes assignment and required slot initialization from CFG edges; it
// does not accept source `val`/`var` syntax as a proof token.

// 5. Definite assignment and normal completion
// --------------------------------------------
//
// A function body must satisfy the source return contract before it can become a generic
// declaration with a successful body analysis. Every reachable normal exit from a non-unit
// function yields a value compatible with the declared result. Every reachable normal exit from a
// function with `out` parameters assigns each such parameter. An unreachable tail is not a
// substitute for a missing return or out write on a reachable path.
//
// A generic return `T` follows the same rule as a concrete result. A direct `return value` is
// accepted when source binding proves `value` has the required type. An expression-bodied function
// has the same value requirement as a block ending in an expression. A block may use explicit
// `return`; each reachable return expression is checked independently before the function's
// normal-exit proof is concluded.
//
// Local initialization state is not inferred from the fact that a generic call normally returns.
// If a call can throw before normal completion, its exceptional edge carries the state that existed
// at the throw point. An out argument becomes assigned at the caller only when the callee returns
// normally under the checked call contract. A thrown edge cannot borrow an out write that the
// caller is entitled to observe only after successful completion.
//
// A return from inside a loop exits the function and is checked as a function exit, not joined with
// the loop's exhaustion or break edge. Returns do not make a later loop iteration or post-loop
// statement reachable. A return type containing `T` remains tied to the current declaration owner
// until the instance substitution closes it.

// 6. Loop flow and back edges
// ---------------------------
//
// A loop is analyzed as a control-flow graph with an entry, condition or iterator acquisition,
// body, back edge, and distinct exits. The source proof must consider zero iterations unless the
// source semantics and a sound constant-flow proof establish that the loop body is entered. An
// unknown array expression, generic call result, or generic collection is not assumed non-empty.
//
// `while` analysis includes the condition-false edge when reachable. A write made only in the body
// is not definitely assigned after a potentially zero-trip loop. A write before `continue` reaches
// the loop back edge; a `continue` before that write does not. A break exits the nearest matching
// loop and contributes its current state to that loop's normal post-loop join.
//
// `for val item in values` first evaluates the iterable expression once according to source
// evaluation rules. The body may execute zero or more times for a dynamically sized array or
// general iterable. The item slot is assigned at each body entry with the element type selected by
// source binding. A body assignment to another local does not prove that local assigned after the
// loop unless every post-loop predecessor, including zero-trip/exhaustion, carries the write.
//
// `break` and `continue` target the nearest legal enclosing loop unless a labeled control-transfer
// rule selects another permitted target. They cannot be retargeted by specialization. An inner
// loop's break exits only that inner loop; it is not an outer-loop break. An inner-loop body write
// does not prove that either the inner body or the outer body was entered.
//
// Loop fixed-point analysis must converge over finite abstract states. A generic substitution may
// change slot representations, but it may not disable a reachable branch, discard a back edge, or
// reset a flow fact to force convergence. Resource exhaustion is a diagnostic/defer outcome, not
// permission to claim definite assignment.

// 7. Loop exits, break, and continue
// ----------------------------------
//
// The normal post-loop state joins all reachable exits belonging to that loop:
//
// - condition false or iterator exhaustion;
// - a normal `break` targeting the loop;
// - any loop-specific normal termination edge defined by the source construct.
//
// Returns and throws leave the function or current exception region, so they do not join the
// post-loop normal state. A `continue` transfers to the condition/next-iteration edge and likewise
// does not directly join the state after the loop. The state at that transfer still participates in
// the loop back-edge fixed point.
//
// A `break` arm that assigns `result` and exits can contribute an assigned state, but if an
// exhaustion edge can reach the statement after the loop without assigning `result`, the join is
// unassigned. Conversely, when `result` was assigned before loop entry, a conditional body write
// preserves definite assignment even if the loop executes zero times. A mutable value may still
// lose a refinement when the body can reassign it.
//
// The same rules apply when the loop's element type is generic. `T` controls the item slot's type;
// it does not change the zero-trip edge. A generic helper returning `[]T` does not establish that
// its result is non-empty. An exact array literal may provide shape information only where source
// flow explicitly models that literal; FIR cannot infer mandatory iteration from its concrete
// machine layout alone.

// 8. Ref parameters
// -----------------
//
// A `ref` formal and actual must have exactly the same source type after ordinary generic
// inference and substitution. No widening, narrowing, nullable adaptation, boxing, conversion, or
// representation-only equivalence is allowed for the location binding. For a generic formal `ref
// value: T`, inference uses the exact type of an eligible ref actual to close T; the caller's slot
// identity is retained in the call binding.
//
// The ref actual must be an assignable location and must be definitely assigned before the call,
// because the callee may read it at entry. Ref is not a value copy. A normal return may have written
// the location; the caller invalidates stale refinements according to the ordinary aliasing rules.
//
// In a generic callee, reading a ref T or assigning another T is type-transparent. Applying an
// operator or member through the ref is subject to the open-type limitation above. The source body
// may use a capability only when a declared constraint and source binding justify that operation.
//
// For a specialized FIR call, the verifier confirms the concrete formal type, actual slot type,
// access mode, origin and target parameter index. Matching only their erased native type is
// insufficient. The verifier also checks that the specialized body does not read an unassigned
// slot and that the call's write effects reach the right caller location.

// 9. Out parameters and out-var locals
// ------------------------------------
//
// An `out` formal begins unassigned inside the callee. Reading it before a write on every reachable
// path is a source error. Every reachable normal return must assign it; a throwing edge need not
// satisfy the normal-return obligation unless cleanup semantics explicitly transfer a pending
// normal return after assignment. A function with an `out T` formal is checked for every path at
// the generic declaration level under ordinary rules.
//
// At a call site, an existing `out` actual must be a writable location with the exact type selected
// for the formal. It need not be initialized before the call. On normal call completion the caller
// may read it; on an exceptional completion it remains not definitely assigned by this call. If the
// call is short-circuited or guarded, assignment is conditional on the branch where invocation
// occurs.
//
// `out var name` introduces no candidate-independent type evidence and no local during speculative
// overload probing. Once one applicable candidate is uniquely selected, the new local has the
// selected formal's closed type and is considered assigned only after the call's normal edge. The
// local's scope begins at the language-defined call-expression scope; it cannot be read in an
// argument that is evaluated before the call completes.
//
// In `func <T> produce(out value: T)`, an `out var result` alone does not determine T. Other
// arguments, explicit type arguments, or permitted expected-result evidence must first select a
// unique closed T. The new local cannot be used to guess a generic argument from a candidate's
// unclosed formal. If candidates produce different out types and selection remains ambiguous, no
// out-var local is committed.
//
// `out var` is not initialized to a default value. No runtime zeroing or placeholder value may be
// used to make a read legal before the normal call-return proof.

// 10. Exact ref/out joins through nested calls
// --------------------------------------------
//
// Each nested call has its own selected declaration, argument map, parameter passing mode, and
// generic substitution. The caller's type parameter and the callee's same-spelled parameter remain
// distinct identities until the selected call maps them. Substitution composes the callee type
// argument through the closed caller map before a concrete FIR edge is built.
//
// For a call `inner<T>(ref source, out target)`, source flow validates `source` as assigned at
// entry and `target` as writable. If the closed callee completes normally, the target's concrete
// caller slot becomes assigned. If the callee call returns a value, that return expression's type
// is checked independently from its ref/out effects. A wrapper cannot drop the out write merely
// because it returns a pass-through local.
//
// Every call edge records the selected target identity, ordered type arguments, argument-to-formal
// map, source evaluation order, and value/ref/out modes. A specialized call verifier rejects a
// target whose parameter mode or concrete slot type differs, even if another overload with the same
// name would have accepted the call.
//
// A nested `out var` is introduced by the inner selected call, then its assigned state flows through
// the enclosing expression according to normal completion. If an outer call or later expression
// fails, its diagnostics do not retroactively make the inner local uninitialized on paths where the
// inner invocation completed normally. Conversely, no state is propagated past an expression edge
// that is unreachable or exceptional.

// 11. Generic return flow
// -----------------------
//
// A generic return expression has a source-bound type, usually a declaration-owned type parameter,
// which must match the declared result under the ordinary type rules. Specialization closes the
// result type and every return value type consistently. A return edge remains a return edge after
// substitution; the resulting machine return convention is selected only after the native ABI
// verifier accepts the closed type.
//
// For a wrapper `func <T> same(value: T): T { val saved: T = value; return saved }`, source flow
// proves that `saved` is assigned before the return. Its specialized FIR slot and return type are
// concrete. The verifier separately checks that the return loads the expected slot and that every
// normal exit returns the declared result.
//
// A return from both branches satisfies normal-return completeness when each reachable arm returns
// a compatible value. A branch with one return and one fallthrough requires the remaining path to
// return a value or reach a final expression as allowed by the function syntax. A loop that may
// execute zero times cannot prove a non-unit result solely by returning from the loop body.
//
// An exception is not a return value, even when a closed type is represented by the same LLVM
// register class. Checked effects and exception edges remain explicit verifier concerns.

// 12. Arrays and generic element flow
// -----------------------------------
//
// An array type `[]T` carries T as its element type. A generic parameter may be passed through an
// array slot when the source type system and current ABI admit that array shape. The type
// substitution of `T` must be applied to the array element type, array parameter, result, local
// slots, index operations, and nested call signatures consistently.
//
// For `values[index]`, the index expression is evaluated and checked under ordinary source rules.
// The resulting element expression has type T. An assignment from that expression to `val item: T`
// or `var item: T` uses ordinary assignment and definite-assignment flow. The bounds check and any
// runtime failure edge remain attached to the indexing operation; specialization may not remove it
// merely because the current array value appears to be a literal.
//
// A literal `[]` needs a target element type or other legal inference evidence. An empty literal
// does not independently infer T for a generic call. A non-empty literal's elements provide
// ordinary inference evidence only after source call selection and type inference rules are
// applied. An array's concrete runtime contents do not participate in compile-time type identity.
//
// Iteration binds one item slot for each body entry, with the source-bound element type. For a
// dynamic array, zero-trip and exhaustion edges remain possible. A loop assignment based on an
// element cannot establish post-loop definite assignment without accounting for those edges.
// Nested arrays, nullable elements, views, spans, and other collection types need their own ABI,
// ownership, lifetime, and iterator proofs; they are not admitted merely because `T` is closed.
//
// The initial native slice may admit one array level over supported primitive/String elements.
// Native admission of `[]T` in one instance does not imply support for `[][]T`, arbitrary generic
// arrays, heap storage of ref-like values, or a generic collection interface.

// 13. Branches with generic calls
// -------------------------------
//
// A condition is source-bound before FIR specialization. If it calls a generic function, that call
// has a selected declaration and closed type arguments in the specialized caller. The condition's
// Boolean result controls ordinary CFG edges; the verifier checks the call result type and the true
// and false successors.
//
// Calls in separate arms retain separate source origins even when they intern to the same function
// instance. Sharing the callee body cannot hoist either call across the condition, merge their
// side-effect order, or cause one arm's out assignment to appear on the other arm.
//
// A write through `out` in only the true arm is not definitely assigned after the branch unless the
// false arm is unreachable or also assigns the same location. A return from the false arm removes
// that normal predecessor, but a throw has an exceptional continuation rather than a normal write.
// Mutable refinements are joined independently of assignment bits.
//
// A generic predicate cannot inspect an unconstrained T in a way that changes source binding. A
// predicate such as `isNull(value)` is legal only if its declaration and constraints define the
// operation. FIR does not choose a different body or flow graph because the closed substitution is
// a pointer, integer, or array.

// 14. Calls from one generic body to another
// ------------------------------------------
//
// A selected generic-to-generic call creates an edge between declaration-owned substitutions. The
// inner generic argument tuple is inferred in inner declaration order using mapped source
// arguments, explicit syntax, constraints, and permitted expected-result evidence. At FIR build
// time, an outer substitution is composed into that tuple so no open caller parameter remains in a
// closed instance key.
//
// The caller body and callee body are analyzed independently. The inner call's normal return type
// substitutes into the caller expression, which may initialize a local, condition, return, array
// element, or outer call argument. The caller flow state changes only from explicit dataflow edges
// and ref/out effects; being generic does not share local assignment state between function
// instances.
//
// Exact recursive and mutual-recursive calls may reuse reserved instance keys. Their CFG proofs are
// finite per function body and retain recursive call edges. A type-growing call that would create
// an unbounded family must be stopped by specialization budgets before verified FIR is published.
// Budget exhaustion is not a flow proof and cannot truncate the instance graph to a smaller
// successful program.
//
// An inner function whose type arguments are unresolved, open after composition, or unsupported for
// its target causes the enclosing specialization to fail or remain deferred. A caller must not
// retain a successful return or out-assignment fact that depended on the missing callee proof.

// 15. Operations and members that depend on open T
// -------------------------------------------------
//
// Pass-through assignments do not require an operation on T. Most other syntax does. Examples
// include arithmetic, equality, ordering, indexing T itself, member lookup, property access,
// casts, interpolation requiring a formatting method, and calls to nongeneric functions expecting
// one concrete type. Each operation must be justified while T is open by the declared constraints
// and source-analysis rules, or be rebound and revalidated under every concrete substitution.
//
// The current generic FIR slice does not perform complete per-instance source semantic rebinding.
// It therefore rejects open-type-dependent operators, members, and nongeneric calls at the body
// surface gate even where a particular eventual instance might have worked. This conservative
// rejection is deliberate: it prevents an unverified source overload/member selection from being
// smuggled into a concrete FIR body by substitution alone.
//
// A future implementation may support more operations only after it records the source operation
// and candidate set, rebinds the concrete instance with full owner-aware semantics, validates
// conversions/effects/flow, and independently proves the resulting FIR. A backend intrinsic or
// machine representation match alone is not evidence that a Sol operator is valid.

// 16. FIR CFG and slot proof
// --------------------------
//
// Every specialized body contains closed typed slots for parameters and locals. The verifier
// checks source slot identity, type substitution, initializer/write/read relationships, and exact
// references from instructions. No source slot can disappear when it carries an assignment,
// lifetime, effect, debug origin, or required call argument.
//
// The verifier constructs basic blocks and terminators from the specialized body. It validates
// that all branch targets belong to the function, terminators are structurally sound, and reachable
// flow obeys the function result and out-parameter obligations. It then runs definite-assignment
// analysis over normal CFG edges. Reads before definite assignment are rejected; unreachable
// instructions do not provide evidence to reachable successors.
//
// `return`, `break`, `continue`, and `throw` have distinct targets/effects. A break target must be
// the exit of its owning loop; continue must reach its loop's iteration edge. A return must target
// the function result contract. Exception edges retain their pre-completion state. Malformed
// control-flow cannot be normalized into fallthrough.
//
// The verifier validates closed `ref`/`out` modes against parameter and actual slot IDs. A `ref`
// read at function entry requires assignment. An `out` read is permitted only after a write on all
// incoming paths. Every normal return satisfies out completeness. At a call edge, caller
// assignment after `out` is recorded only for the normal successor.
//
// The CFG proof is concrete per generated instance but is not a re-execution of source overload
// selection or source member binding. Both source evidence and FIR structure must pass their own
// checks; neither proof subsumes the other.

// 17. Source/FIR/runtime correspondence
// -------------------------------------
//
// For each admitted source operation, an origin chain links source span and declaration identity to
// bound body fact, specialized expression/slot, FIR instruction, and backend instruction/debug
// location where available. This chain allows diagnostics and validation to explain which closed
// instance a flow fact describes. Numeric IDs remain compilation-local.
//
// The correspondence must preserve:
//
// - branch and loop edges, including zero-trip, exhaustion, break, continue, return, and throw;
// - source argument evaluation order and selected parameter mapping;
// - exact location identity and passing mode for ref/out;
// - local initializers, assignments, and reads used by DA proof;
// - generic parameter owner and every composed type substitution;
// - call result type, normal/exceptional continuation, and effect obligations;
// - array element type, bounds-check operation, iteration item slot, and iterator exit;
// - source spans for the declaration, call site, and body expression where available.
//
// Runtime behavior is then constrained by this verified graph. A runtime test may confirm that a
// selected concrete program computes expected values, but it cannot establish DA safety for all CFG
// paths, all call origins, or all specializations. A verifier proof may establish structural safety,
// but it does not demonstrate that LLVM code generation and runtime ABI behave correctly. The
// language and compiler require both kinds of correctness evidence before claiming an executable
// feature complete.

// 18. Diagnostic and recovery requirements
// ----------------------------------------
//
// A source-flow failure is reported at the source expression or declaration that violates the
// language rule, using the existing flow diagnostic family. A FIR flow failure is an internal
// verifier rejection with the generic instance and source origin attached where known; it must not
// be reported as a user type error if the source program was already accepted.
//
// `SOL2400` reports a generic body/native shape that lies outside the current supported subset.
// `SOL2402` reports a specialization verifier rejection. `SOL2403` reports a bounded resource
// failure. None is an implicit successful fallback to a generic call, interpreter, erased ABI, or
// empty function.
//
// A diagnostic must distinguish at least:
//
// - source definite-assignment/return/out failure;
// - unresolved or ambiguous generic type inference;
// - unsupported open-type-dependent body operation;
// - unsupported closed ABI or array shape;
// - FIR slot or CFG verification rejection;
// - specialization graph or body-size resource exhaustion;
// - backend or runtime failure after successful verification.
//
// Recovery may continue parsing and analyzing unrelated declarations, but a rejected instance and
// all dependent callers remain ineligible for successful FIR. Diagnostic cascade suppression may
// hide derivative messages; it cannot alter the stored rejection state.

// 19. Required positive and negative guarantees
// ----------------------------------------------
//
// A conforming implementation must preserve these positive cases when their closed ABI shapes are
// admitted:
//
// - passing T through parameters, initialized locals, explicit returns, and direct generic calls;
// - assignment joins where every reachable normal predecessor supplies a value;
// - branch and loop flow with correct zero-trip and break/continue edges;
// - ref/out exactness after inference and substitution;
// - out-var creation only after unique candidate selection and assignment on normal return;
// - nested generic calls whose argument tuples close under caller substitution;
// - arrays whose element type and item slot agree under the closed substitution;
// - per-instance CFG verification independent of the source generic declaration's flow result.
//
// It must reject or defer:
//
// - reads of a local, ref, or out slot not assigned on every reachable incoming path;
// - a non-unit generic function with a reachable normal exit lacking a result;
// - a generic out formal not assigned on every reachable normal return;
// - a post-loop read justified only by a body write when zero iterations are possible;
// - ref/out type mismatch hidden by equal native representation or implicit conversion;
// - out-var inference that depends only on the candidate's unselected open formal;
// - call or loop edges removed to make a slot appear initialized;
// - operations on unconstrained open T that lack source capability evidence or full instance rebinding;
// - an instance whose substitution or call edge remains open after closure.
//
// These rules do not guarantee that every syntactically valid generic body is natively compilable.

// 20. Conformance matrix and status labels
// ----------------------------------------
//
// Generic flow fixtures use `// expect:` annotations as normative expected outcomes. Suggested
// outcome vocabulary:
//
// - `SourceBound`: source parsing, binding, inference, and declaration-level flow succeed;
// - `SourceRejected`: source rules fail before a usable generic call binding is produced;
// - `NativeAdmitted`: the current closed subset builds and verifies FIR for the described shape;
// - `NativeRejected`: source is bound but current FIR/native subset rejects the closed shape;
// - `VerifierRejected`: malformed or inconsistent FIR does not receive verified status;
// - `PlannedRebind`: correct per-instance body rebinding is required, but is not implemented.
//
// A fixture may describe a valid language program and still have outcome NativeRejected or
// PlannedRebind. The annotation names the current implementation boundary; it does not weaken the
// normative source rule. Changes in implementation status require a new source/API/backend and
// conformance review rather than reinterpreting an old fixture.
//
// The focused examples are in `examples/semantics/GenericFirFlow.sol`. The maintainer explanation
// and compact proof checklist are in `docs/generic-fir-specialization/Flow.md`. These documents
// complement the specialization, nullability, control-flow, generic inference, array iteration,
// and ref/out contracts. They do not replace those contracts or alter the Sol language version.

// 21. Completion criteria for per-instance flow
// ----------------------------------------------
//
// This flow feature is complete only after all points below hold:
//
// 1. Every selected closed instance receives a fresh, owner-aware semantic context whose type
//    parameters are replaced simultaneously, without capture or spelling-based substitution.
// 2. The source binder re-resolves every operator, member, indexer, conversion, callable candidate,
//    and overload whose applicability could vary with the concrete substitution.
// 3. Body analysis recomputes DA, mutability, nullability, return completeness, ref/out writes,
//    loops, branches, effects, borrow/lifetime and exceptional flow for that instance.
// 4. Every nested generic call receives its independently inferred, ordered, fully closed type
//    argument tuple after composed caller substitution; unresolved edges remain failures.
// 5. Array and iteration transfer functions preserve zero-trip, bounds, exceptions, element
//    identity, and assignment joins for each admitted shape.
// 6. FIR records enough source origins and substitution provenance for an independent verifier to
//    recompute slot types, CFG successors, call modes, and normal-exit obligations.
// 7. The verifier rejects corrupt/missing writes, stale slot IDs, malformed branch targets,
//    mismatched substitutions, forged flow summaries, and unresolved operations.
// 8. AOT and ORC execute admitted scalar and array cases with correct returns, branches, loops,
//    ref/out effects, failures, and source-to-machine debug origins.
// 9. Resource limits, cancellation, diagnostics, serialization, and cache invalidation cannot
//    publish a partial instance or reuse a proof for a different closed argument tuple.
// 10. Public status notes describe the actual tested boundary without treating source flow, FIR
//     verification, or runtime validation as substitutes for one another.
//
// Until this list is met, the implementation status at the start of this file remains controlling:
// it describes an existing specialized CFG/slot proof over substituted bodies, not complete
// per-instance semantic rebinding of every generic body.
