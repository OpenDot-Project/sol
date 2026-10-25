// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative array/vararg source mapping; complete trailing-call lowering remains pending.
module source.spec.generic_inference.array_vararg_mapping

// Array literal and vararg argument mapping
// =========================================
//
// Array inference depends on how an argument maps to a formal parameter. The same source array
// expression can be one array-valued argument or a literal source for several vararg elements only
// where the call grammar explicitly says so. Candidate inference must not guess the mapping that
// produces the most convenient type.

// Formal parameter categories
// ---------------------------
//
// A fixed parameter has one argument slot. A vararg parameter has an element formal and may also
// accept the language-defined named-array or spread form. A default parameter consumes a source
// argument only when one is supplied; its default expression is not generic evidence.
//
// The source mapping records argument index, formal parameter index, source name, spread state,
// array-argument state, and effective vararg element index. It is immutable after mapping. Each
// candidate receives its own mapping result because overload parameter names/defaults/varargs may
// differ.
//
// Named argument binding uses the declared parameter name exactly. A misspelled name rejects the
// candidate even if another parameter has a compatible type. A positional argument after a named
// argument follows the language's named/positional ordering rule; type inference cannot repair an
// invalid argument order.

// Positional vararg element
// -------------------------
//
// A positional expression mapped to the vararg element slot is checked against the element type.
// It contributes ordinary input evidence, except ref/out modes which require exact storage
// identity. A source array expression in this position is still one expression. It is not silently
// flattened into its elements.
//
// Example declaration:
//
//     func <T> collect(prefix: String, vararg values: T): []T
//
// `collect("x", 1, 2)` maps two positional elements to T and can infer one common T. `collect("x",
// [1, 2])` maps one array literal expression to an element formal T. It is not equivalent to two
// integer arguments unless the call syntax has an explicit spread marker.

// Named array-valued vararg
// -------------------------
//
// A named vararg array argument denotes one array value accepted by the vararg parameter's array
// contract. Its source type must match the substituted array element type invariantly. An array
// variable contributes exact element identity; its elements are never inspected at compile time.
//
// An array literal supplied in the named-array form is checked as one array expression. If it is
// non-empty, it may contribute a single exact common element type only when the standalone common
// element rule yields one unique type. It cannot contribute separate unrelated candidate types to
// different elements of the same array.
//
// An empty named array literal contributes no element evidence. T must be closed by another source
// before the literal can be checked. It does not mean “zero vararg values” unless the grammar maps
// that syntactic form to zero elements explicitly.

// Spread array
// ------------
//
// A spread argument must have an array type. Its array element identity is checked against the
// vararg element formal after receiver/explicit/generic substitution. The spread's runtime length
// does not affect generic inference. Type checking does not enumerate its runtime elements.
//
// A spread of a nullable array requires the ordinary nullable check or a safe/correctly narrowed
// expression before the array element type can be read. A nullable element type remains nullable;
// spreading an array does not strip element nullability.
//
// A fixed array spread preserves its element type and any source length facts relevant to overload
// or lifetime rules. It does not become an arbitrary managed array if the target requires fixed
// storage. The current standard source contract does not imply that every fixed-array form is
// spreadable; a language-level mapping rule must explicitly permit it.

// Empty and default cases
// -----------------------
//
// An omitted vararg contributes zero arguments and zero type evidence. A supplied empty array
// literal contributes one array-valued argument and zero element evidence. They can have different
// evaluation, named-argument, overload-ranking, and default interactions and must not be conflated.
//
// An omitted optional parameter contributes no inference fact. The default expression is bound at
// the call site after candidate selection with the completed substitution. The candidate solver
// cannot evaluate a default to discover T, and a default lambda is not replayed as a source argument.
//
// A defaulted vararg is permitted only if the declaration grammar defines it. If permitted, the
// omission uses that default exactly once after explicit arguments. It still contributes no
// speculative evidence.

// Source-to-formal map table
// --------------------------
//
// | Source form | Mapping | Inference contribution |
// |---|---|---|
// | `f(1, 2)` to `vararg T` | two element slots | two input facts for T |
// | `f(values = array)` | one named array slot | invariant element identity |
// | `f(...array)` | one spread slot | invariant element identity |
// | `f(values = [1, 2])` | one named array literal | one unique common element type |
// | `f(...[1, 2])` | one spread literal | array shape and one element type |
// | `f()` | no vararg source | no evidence |
// | `f(values = [])` | one empty array source | no element evidence |
// | `f(prefix = "p", 1)` | grammar-dependent order | reject if source ordering rule forbids |
// | `f(other = 1)` with no `other` | no formal mapping | reject candidate |
// | `f(ref value)` to value vararg | mode mismatch | reject candidate |
// | `f(ref value)` to `vararg ref T` | one storage slot | exact evidence and storage check |
//
// Inference cannot mutate this table after receiving new evidence. Candidate-specific parameter
// names mean the same named source argument may map differently for sibling candidates, but each
// individual candidate's map is fixed before solving.

// Lambda vararg
// -------------
//
// A lambda mapped to a vararg element is contextually typed using the element function type. A
// named array of function values follows the array-valued rule instead. A spread array uses its
// existing element function identity. The compiler does not infer from every lambda in a runtime
// array because those values are not available during static analysis.
//
// Each positional lambda element is analyzed in a private candidate transaction. A lambda that
// closes T can affect later inference rounds for other mapped lambdas, but all lambdas in one round
// share the same entry substitution. Candidate order cannot change the result.

// Overload ranking
// ----------------
//
// Mapping validity precedes type inference. After substitution, ordinary element conversions are
// ranked against the selected parameter type. A named-array or spread form uses its array
// conversion rank and cannot be ranked as multiple independent element conversions.
//
// Vararg use normally ranks below a candidate with the same fixed formals and no vararg expansion,
// according to the call-ranking contract. The mere fact that one mapping supplies more individual
// element evidence does not make it more specific.
//
// An incomplete candidate that may outrank a complete candidate prevents selection. A candidate
// cannot be marked inapplicable just because its array/lambda evidence solver has not yet reached a
// unique fixed point.

// Candidate error ownership
// -------------------------
//
// Mapping errors belong to the candidate whose signature defines the invalid map. Element type,
// spread type, fixed length, nullable array, ref/out storage, and common-type errors likewise reject
// only that candidate. The call-level resolver combines candidate outcomes after every complete
// mapped candidate has been considered.
//
// If one overload accepts a named array and another interprets the same syntax as a positional
// element, both mappings are evaluated independently under their written signatures. The resolver
// cannot change a failed candidate's map to borrow the sibling's successful interpretation.

// Diagnostic examples
// -------------------
//
// | Error | Primary location | Candidate result |
// |---|---|---|
// | unknown named parameter | argument name | rejected |
// | too many fixed arguments | excess argument | rejected |
// | spread source is not an array | spread expression | rejected |
// | array element type is incompatible | element span | rejected |
// | named array element identity differs | array argument | rejected |
// | empty array leaves T open | call/literal | deferred |
// | ref/out mode mismatch | argument | rejected |
// | malformed argument ordering | source argument sequence | rejected |
// | cancellation during candidate map | analysis operation | canceled |
// | path/common-type cap exceeded | array/call span | resource-limited |
//
// A parser error in an argument list is represented by syntax recovery and remains a separate
// parser diagnostic. The call mapper does not reinterpret missing nodes as omitted defaults.

// Required invariants
// -------------------
//
// - Mapping happens before generic solving and stays immutable for the candidate.
// - Positional elements, named arrays, spread arrays, and omitted varargs remain distinct forms.
// - Runtime array contents are never inspected to infer a source generic parameter.
// - Empty array and omitted vararg supply no element evidence.
// - Named/spread array element types remain invariant where specified.
// - Default expressions are not speculative inference evidence.
// - A lambda vararg uses the element function type unless syntax maps an array value.
// - Candidate mapping failures do not corrupt sibling mappings.
// - Incomplete candidates retain their potential overload rank.

// Current implementation boundary
// -------------------------------
//
// The source call binder preserves argument mapping for supported named, positional, vararg,
// spread, and array-literal forms. Full language-wide verification of default evaluation, fixed
// arrays, ref-like spread lifetimes, and every trailing-lambda/vararg combination remains pending.
