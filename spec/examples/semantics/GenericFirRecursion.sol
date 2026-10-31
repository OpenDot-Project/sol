// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative graph scenarios. The current builder has exact-key reuse and bounded FIFO work;
// this file does not claim every example is currently accepted or lowered to native code.
module examples.semantics.generic_fir_recursion

// Generic FIR recursion and resource scenarios
// ============================================
//
// These scenarios specify how closed generic function instances form a graph. They distinguish
// declaration identity, ordered argument identity, call-edge origins, and temporary FIR IDs. The
// comments beginning `expect:` are semantic oracle records for a future complete graph contract.
// Parsing or adding a source fixture is not evidence that a current Sol compiler accepts it.
//
// Status vocabulary:
//
// - `KeyReuse`: the current builder's map returns one reserved function ID for an exact key.
// - `BoundedQueue`: the current builder enqueues each new key once and caps instances/body nodes.
// - `Normative`: required graph behavior, not yet guaranteed by the shipped implementation.
// - `NativeSubset`: native output still requires all current FIR and LLVM admission checks.
// - `Harness`: the case requires a controlled compiler test harness rather than a giant source file.

// Shared definitions used by the cases below
// ------------------------------------------

func <T> identity(value: T): T = value

func <T> relay(value: T): T = identity(value)

func <T> keepFirst(value: T, ignored: String): T = value

func <A, B> keepFirstTwo(value: A, ignored: B): A = value

func <A, B> keepSecond(value: A, result: B): B = result

func <T> arrayIdentity(values: []T): []T = values

func <T> nullableIdentity(value: T?): T? = value

func <T> left(value: T, more: bool): T = right(value, more)

func <U> right(value: U, more: bool): U = left(value, more)

func <T> direct(value: T, again: bool): T {
    if (again) {
        return direct(value, false)
    }
    return value
}

// 1. Equal requests share one node
// --------------------------------

// expect: repeated_exact_calls | KeyReuse | one=identity<int> | edges=2 | bodies=1
func repeatedExactCalls(): () {
    identity(1)
    identity(2)
}

// Both call expressions have the same selected declaration and the same complete closed tuple.
// Their source origins remain distinct even though both edges target one body. The body-node budget
// charges the logical instance once. Current exact-key lookup provides the key reuse behavior;
// successful native lowering is still subject to the implementation's verified subset.

// expect: repeated_nested_call | BoundedQueue | relay<int> | identity<int> | one_each
func nestedCallsShareIdentity(value: int): int {
    val first = relay(value)
    val second = relay(first)
    return second
}

// The `relay<int>` body contains one outgoing edge to `identity<int>`. A second root or another call
// through `relay<int>` reuses both nodes, not just the outer node. The selected overload, body
// substitution, and each source edge stay attached to their original declaration/call origin.

// expect: multiple_roots_share_target | KeyReuse | id=identity<int> | incoming=2
func firstRoot(value: int): int = identity(value)

func secondRoot(value: int): int = identity(value)

// A graph closure with two ordinary roots contains one `identity<int>` node and two root edges. The
// roots themselves remain distinct ordinary functions. Root order can affect current local IDs but
// cannot create duplicate semantic instance keys.

// 2. Direct exact-key self recursion
// ----------------------------------

// expect: direct_same_key | KeyReuse | nodes=direct<int> | self_edges=1 | runtime_termination=source
func directInt(value: int, again: bool): int = direct(value, again)

// The root's selected call reserves `direct<int>`. While building that body, its self-call requests
// exactly `direct<int>` again and resolves to the reserved ID. The graph has one `direct<int>` node
// and one recursive edge. Runtime termination depends on the boolean argument and ordinary runtime
// execution; graph reuse is not a termination proof. Current exact-key reuse is the mechanism under
// test, while end-to-end native acceptance remains `NativeSubset`.

// expect: direct_generic_recursion_reuses_selected_target | KeyReuse | direct<String> | one_node
func directStringRoot(value: String): String = direct(value, false)

// A different root type asks for `direct<String>`, not `direct<int>`. The use of the same declaration
// and same recursive expression does not share across the distinct closed tuple.

// 3. Mutual same-key cycles
// -------------------------

// expect: mutual_cycle_same_argument | KeyReuse | nodes=left<int>,right<int> | edges=2 | scc=2
func mutualCycleInt(value: int, more: bool): int = left(value, more)

// Starting with `left<int>`, the FIFO closure discovers `right<int>`. Its call requests the reserved
// `left<int>` key. The strongly connected component has two nodes and two cross edges. The parameter
// spellings `T` and `U` are owned by different declarations; only the call mapping relates both to
// the concrete `int` identity.

// expect: mutual_cycle_distinct_argument_components | Normative | left<int>,right<int>,left<String>,right<String>
func mutualIntAndStringRoots(first: int, second: String): () {
    left(first, false)
    left(second, false)
}

// Each tuple closes a separate two-node component. Equal source names and equal edge shapes do not
// merge the components across `int` and `String`. The fixture is normative for graph identity; the
// present body builder's body/type/native admission may reject some operations before native output.

// expect: mutual_cycle_with_repeated_origin | Normative | each_edge_keeps_call_span | target_count=2
func twoPathsIntoCycle(flag: bool, value: int): int {
    if (flag) {
        return left(value, true)
    }
    return right(value, false)
}

// Two source paths may converge onto the same node in a cycle. The graph retains each origin-bearing
// edge; it does not clone the callee body to preserve a call-site stack. A verifier can report the
// target key and the individual call origin without confusing either edge with the node identity.

// 4. Distinct closed arguments are distinct keys
// ----------------------------------------------

// expect: different_primitive_argument | KeyReuse | keys=identity<int>,identity<long> | nodes=2
func primitiveRoots(one: int, two: long): () {
    identity(one)
    identity(two)
}

// expect: nested_argument_shape | Normative | keys=identity<int>,identity<[]int>,identity<[][]int>
func nestedArrayRoots(one: int, two: []int, three: [][]int): () {
    identity(one)
    identity(two)
    identity(three)
}

// Constructed source types remain structural key components. If one of these types is outside the
// current native verifier, the compiler reports the appropriate unsupported-type diagnostic rather
// than collapsing it to Object or reusing a shallower key.

// expect: nullable_argument_distinct | Normative | keys=nullableIdentity<int>,nullableIdentity<int?>
func nullableRoots(value: int, optional: int?): () {
    nullableIdentity(value)
    nullableIdentity(optional)
}

// Nullability changes the complete closed type identity. A representation limitation may prevent
// native lowering, but it cannot turn nullable `int?` into the non-null `int` graph key.

// expect: generic_argument_order_matters | Normative | keys=keepFirstTwo<int,String>,keepFirstTwo<String,int>
func reversedClosedTuples(a: int, b: String, c: String, d: int): () {
    keepFirstTwo(a, b)
    keepFirstTwo(c, d)
}

// The ordered argument tuple follows `A, B` in the declaration. Reversing concrete types therefore
// produces a second key even if a body happens to use only its first parameter.

// expect: unused_generic_parameter_still_participates | Normative | keys=keepFirstTwo<int,String>,keepFirstTwo<int,byte>
func unusedArgumentRoots(a: int, text: String, small: byte): () {
    keepFirstTwo(a, text)
    keepFirstTwo(a, small)
}

// The second type parameter is unused by the body but remains part of the declared signature and
// constraints. Liveness or body simplification does not erase it from instance identity.

// expect: same_spelling_different_owner | Normative | identity<T@identity> != relay<T@relay>
func sameSpellingRoot(value: int): int = relay(value)

// Each declaration's T is a distinct parameter identity. The concrete edge closes `relay<T>` to
// `relay<int>` and the nested selected call independently closes `identity<T>` to `identity<int>`.

// 5. Type-growing recursion does not collapse by declaration name
// ---------------------------------------------------------------

func <T> growByArray(value: T, more: bool): () {
    if (more) {
        growByArray([value], false)
    }
}

// expect: type_growing_recursion | Normative | prefix=growByArray<int>,growByArray<[]int>,growByArray<[][]int> | terminal=SOL2403
func startTypeGrowth(value: int): () = growByArray(value, true)

// Every recursive step changes the ordered type tuple. The graph builder must continue until a real
// source/analysis or specialization budget rejects more work. It may not stop after the first
// occurrence of the declaration or use `growByArray<int>` as a generic catch-all. This chain may
// hit the current 128 structural type-depth cap before the 4,096 instance limit; the actual
// rejecting budget is named in `SOL2403`.

func <T> wrapThenCall(value: T, more: bool): T {
    if (more) {
        return wrapThenCall([value], false)
    }
    return value
}

// expect: recursive_result_type_conflict | SourceAnalysis | no_partial_instance
func growingResultRoot(value: int): int = wrapThenCall(value, true)

// This deliberately exposes an important phase boundary: if the recursive result cannot be made
// consistent with the declared result under source typing, source analysis rejects it before FIR
// graph closure. A resource graph oracle is only valid when the language-level source call itself
// has a well-defined selected substitution.

// 6. Distinct shallow chains and instance cap
// -------------------------------------------

// expect: shallow_distinct_chain | Harness | instance_budget=4096 | cap_plus_one=SOL2403
// The harness generates a source graph with 4,097 distinct, shallow generic keys reachable from
// ordinary roots. It observes the attempt to reserve the 4,097th distinct key, expects one primary
// `SOL2403`, and asserts no successful graph or native output. It does not use a 4,097-line checked-
// in source fixture, so ordinary specification review stays practical.
//
// The exact count is distinct semantic keys, not total call edges, declarations, cache hits, generic
// type arguments, or integer IDs. Exactly 4,096 valid instances do not exceed the instance cap. A
// repeated request for any one of those keys does not increment the count. A 4,097th unique key
// fails before it is added to the successful closure.

// expect: edge_fanout_does_not_charge_nodes | Harness | many_edges_one_key | instance_count=1
// The harness creates many reachable calls with the same selected target and argument tuple. It
// checks that all call origins survive, the key table contains one target, the body is accounted
// once, and duplicated edges do not consume the distinct-instance budget.

// 7. Body-node and structural type budgets are independent
// -------------------------------------------------------

// expect: body_node_budget | Harness | limit=16000000 | terminal=SOL2403 | no_truncated_success
// The harness arranges distinct specialization bodies whose aggregate accounted source AST node
// count crosses 16,000,000 without relying on a deeply nested type. It verifies that each distinct
// body is charged once, repeated edges do not repeat the charge, and a body is never partially
// accepted after the remaining budget becomes insufficient.

// expect: structural_type_depth | Harness | current_limit=128 | terminal=SOL2403
// A controlled source/type-analysis fixture supplies a closed argument whose structural nesting
// exceeds 128 levels. The type-depth check is distinct from recursion path length. The fixture can
// also contain a shallow 2,000-edge chain to demonstrate that a long graph path alone is not a
// structural type-depth overflow in the current implementation.

// expect: graph_path_depth | Normative | planned_limit=128_edges | independent_of_type_depth=true
// The complete normative resource contract can enforce at most 128 active graph edges from a root.
// The test is enabled only when the implementation advertises that separate graph-depth guard. It
// must not be reported as implemented merely because both limits happen to use the number 128.

// 8. `TypeSpecialization` does not spend the FIR instance budget
// -------------------------------------------------------------

// expect: interner_growth_without_function_instances | Harness | types=100000 | instances=0
// The type interner can construct many legal structural types without creating a generic function
// graph node. The interner's additional-type capacity is a workspace limit; it is not 100,000
// generic functions and is not deducted from the 4,096 FIR instance allowance.

// expect: function_instances_without_interner_limit | Harness | fir_instances=4096 | interner_limit=separate
// FIR body nodes can be requested after source inference has already interned each argument type.
// Crossing the instance cap reports the graph budget even if the type interner has spare capacity.
// Crossing the interner budget earlier belongs to the analysis operation that required a new type.

// expect: inference_budget_precedes_graph | Normative | source_failure=no_graph_key
// If candidate inference reaches its own depth, fixed-point, projection, or graph-node budget, no
// selected candidate exists and no generic FIR key is reserved. A later FIR budget cannot rewrite
// that analysis result into a selected instance.

// 9. Order, local IDs, and cache identity
// ---------------------------------------

func orderedFirst(value: int): int = identity(value)

func orderedSecond(value: String): String = identity(value)

// expect: same_input_same_schedule | BoundedQueue | same_key_set=true | local_ids=repeatable_for_fixed_input
// With the same source snapshot, file enumeration, options, and builder traversal, the current FIFO
// scheduler discovers the same keys in the same sequence. This is a limited repeatability claim.

// expect: reordered_files | Normative | semantic_graph_equal=true | local_ids_may_differ_currently=true
// A future canonical schedule produces normalized graph equivalence when the same files are
// supplied in a different enumeration order. The current implementation may assign different
// ordinary-function and specialization IDs because IDs follow declaration and discovery order.
// Those differences are not source semantic differences and cannot be persisted as cache identity.

// expect: cache_key_uses_canonical_identity | Normative | declaration+ordered_structural_types
// A persistent cache test uses two analysis workspaces that assign different integer IDs to equal
// semantic types. It expects one semantic cache identity only after canonical declaration/type
// identity and dependency fingerprints have been compared. A foreign integer ID alone is rejected.

// expect: same_display_signature_other_overload | Normative | keys_distinct_by_declaration
// Two overload declarations with the same printed signature but distinct canonical declarations
// remain different owners. A hash collision in the test key table also preserves distinct nodes.

// 10. Cancellation leaves no successful closure
// ----------------------------------------------

// expect: cancel_before_first_dequeue | Harness | result=Cancelled | verified_nodes=0
// The harness cancels after roots are discovered but before the first generic work item is built.
// No graph is reported as complete, no cache entry is published, and no prior output is replaced.

// expect: cancel_mid_body | Harness | result=Cancelled | no_artifact_commit=true
// The harness cancels during a large but finite body traversal. The current operation observes the
// token at an available safe point, stops accepting work, suppresses success, and releases temporary
// state. Cancellation is not mislabeled `SOL2403` or a source error.

// expect: cancel_in_cycle | Harness | result=Cancelled | recursive_component=unverified
// A cancellation during construction of a self or mutual cycle leaves every dependent node
// provisional. No reserved back-edge target is allowed to masquerade as a Built or Verified body.

// 11. Failure inside a cycle is transitive
// ----------------------------------------

// expect: invalid_cycle_member | Normative | failed_component=true | artifact=not_committed
// A test verifier rejects one body in a two-node recursive component. The other node may have a
// completed local body, but the artifact closure fails because one required edge target is invalid.
// The independent verifier must report the rejected origin/key and prevent partial publication.

// expect: missing_target_id | Normative | verifier_rejects | no_placeholder_body
// Corrupting a FIR edge so its local numeric target is absent or refers to a different key produces
// a verifier failure. The builder cannot fill the gap with an empty body, a same-named declaration,
// or a generic fallback during lowering.

// expect: orphan_generic_instance | Normative | no_root_path | verifier_rejects
// A test injects a complete-looking but unreachable generic node. The graph closure verifier
// rejects or excludes it according to the artifact policy; it is never emitted merely because its
// declaration appears in the source set.

// 12. Selection and recursion are separate
// -----------------------------------------

func <T> selected(value: T): T = value

func selected(value: String): String = value

// expect: overload_selected_before_graph | Normative | String_target=nongeneric | generic_nodes=0
func exactStringOverload(value: String): String = selected(value)

// The nongeneric overload wins under source overload rules. The generic overload candidate does
// not acquire an instance key merely because it could accept String. Graph construction consumes
// the selected target; it is not a second overload resolver.

// expect: selected_but_native_unsupported | Normative | target=selected<int> | result=source_or_fir_diagnostic
func selectedGenericWithUnsupportedBody(value: int): int = selected(value)

// A distinct fixture can make a source-selected generic body unsupported to the current native
// subset. The compiler reports that selected body failure. It cannot silently switch to a less
// preferred overload or pretend the generic declaration was not selected.

// expect: recursive_overload_chain | Normative | key_owner=selected_declaration_identity
// For an overloaded recursive call, each edge records the declaration selected at that call site.
// Same-spelled declarations do not share state. If overload resolution selects a nongeneric target,
// no generic node is created for that edge; if it selects a generic target, its full tuple is used.

// 13. Argument mapping does not change tuple identity
// --------------------------------------------------

func <A, B> mappedPair(first: A, second: B): B = second

// expect: named_argument_order | Normative | source_mapping=(second,first) | tuple=[int,String]
func namedCallKey(value: int, label: String): String = mappedPair(second = label, first = value)

// The function key is `mappedPair<int, String>` because type arguments are recorded in declaration
// order. The call edge still captures the source-to-formal mapping: the first written source
// argument supplies parameter `second`, and the next supplies `first`. Instance identity must not
// be derived from argument token order.

// expect: source_order_does_not_reverse_type_arguments | Normative | tuple=[String,int]
func sourceOrderDifferentTuple(name: String, count: int): int = mappedPair(name, count)

// This second call has a genuinely different declaration-order tuple. Whether both source programs
// are in one closure or separate roots, the key contains the ordered substitution, not a sorted set
// of types. Sorting type arguments would incorrectly merge this call with the prior example.

// 14. Root boundaries and cross-container limits
// ----------------------------------------------

object Main {
    func main(args: []String): () {
        identity(args)
    }
}

// expect: ordinary_entry_reaches_generic | Normative | root=Main.main | generic=identity<[]String>
// A valid ordinary entry is one root under the binary closure policy. Its selected direct generic
// call makes the target reachable. The example expresses graph reachability; current array and
// generic native ABI admission may still reject this concrete body.

// expect: uncalled_generic_declaration | Normative | key_count=0 | no_eager_specialization
func <T> neverCalled(value: T): T = value

// Declaring a generic function does not allocate a closed instance. A library policy can decide
// which ordinary functions are roots, but this generic declaration by itself is not one. Reflection
// names, source exports, annotations, or a cache hit cannot create a root implicitly.

// expect: generic_cross_container_reference | Normative | current=unsupported | no_foreign_id_reuse
// A consumer compilation that sees only a `.solib` signature cannot reuse an instance numeric ID
// from a producer process. A future verified generic-import format would need canonical declaration
// and type identity, ABI/version facts, semantic dependencies, and verifier evidence. The current
// implementation provides no stable cross-container generic instance identity.

// 15. Limit diagnostics identify the real budget
// ----------------------------------------------

// expect: instance_limit_message | Harness | code=SOL2403 | dimension=distinct generic instances
// At the 4,097th new key in one compilation, the primary diagnostic identifies the 4,096 instance
// limit, the requesting source call, and a bounded source-rendered instantiation trace. It does not
// print workspace-local type IDs or an absolute checkout path. Duplicate requests for existing
// keys stay valid and do not repeat the primary error.

// expect: body_limit_message | Harness | code=SOL2403 | dimension=specialized body AST nodes
// When aggregate per-instance body accounting crosses 16,000,000, the diagnostic names the body
// node dimension. The implementation cannot mislabel it as a graph recursion-depth failure simply
// because the request arose while processing a recursive call.

// expect: type_limit_message | Harness | code=SOL2403 | dimension=structural type levels
// A type argument beyond the implemented 128 structural levels reports type depth. This is not an
// active instantiation path count. The trace can contain one key if the oversized type is requested
// directly, or a bounded chain if it arose from type-growing recursion.

// expect: interner_limit_is_not_sol2403_graph_count | Harness | owning_phase=TypeSpecialization
// Exhaustion of the 100,000 additional interned type capacity occurs in the type analysis workspace.
// It is translated by the owning analysis operation and does not consume the 4,096 function
// instance allowance. Conversely, `SOL2403` for FIR instance count does not imply that the type
// interner is full.

// expect: inference_limit_has_no_instance | Normative | owning_phase=source_inference | graph_key=absent
// If generic inference exhausts its own work budget before uniquely selecting a candidate, the FIR
// builder receives no selected closed tuple. The source analysis result remains a failure/deferred
// result and no partially inferred key is reserved.

// 16. Current support ledger for this file
// ----------------------------------------

// The current builder supports the mechanism exercised by repeated exact keys: it computes the
// source declaration plus closed argument identity, reserves the key before processing its body,
// and returns the reserved local ID on duplicate requests. Its `Queue<PendingSpecialization>` is
// FIFO, distinct instances are bounded to 4,096, generic body AST nodes are accounted up to
// 16,000,000, and structural argument depth is limited to 128.
//
// This mechanism does not establish that a given source snippet parses, type-checks, can be rebound
// per instance, passes FIR verification, or lowers to LLVM. The admitted implementation is still a
// bounded generic free-function subset with limitations on source body operations and supported
// native types. Graph cycles are a graph construction property; recursive execution behavior and
// native call ABI remain separately verified concerns.
//
// The current source key stores local `ResolvedType.Id` numbers in an in-memory string. This is
// acceptable only inside the one owning builder/workspace lifetime. It does not satisfy canonical
// cache identity across process runs. The current FIFO sequence is stable for identical traversal
// order but does not promise stable IDs after source-file reordering.
//
// A future implementation-status update for any case in this file must cite the exact completed
// behavior and the independent verifier or regression evidence. Status may be promoted case by case;
// implementing one recursion scenario does not make type growth, cancellation, cross-container
// identity, a canonical queue, or an additional graph-depth counter implemented by implication.

// 17. Oracle summary
// -----------------
//
// The supported first slice demonstrates exact-key reuse, one queue entry per key, and bounded
// resource work. The complete oracle suite additionally verifies graph topology, stable semantic
// identity, cancellation atomicity, cache isolation, and every independent budget. A passing parser
// test, a successful inference result, or a FIR integer reference alone is not a passing graph test.
