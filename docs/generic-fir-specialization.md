<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Generic FIR specialization

This page is the entry point for the current generic-function specialization slice. It connects the
source-language contract to binding, FIR construction, independent verification, JSON inspection,
native admission and known limits. The detailed pages intentionally distinguish normative behavior
from behavior that the current implementation can execute.

## Current implementation boundary

Source analysis can bind generic calls, solve the currently supported inference evidence, select one
overload, and retain the selected type arguments in declaration order. The FIR builder can turn a
directly called module-level generic free function, or an eligible method on a stateless top-level
`object`, into a closed, monomorphic function instance. Object methods use direct singleton
qualification or an unqualified call from the same object; the native function ABI has no receiver
slot. Eligible omitted defaults use the same pure, non-vararg call-plan rules as generic free
functions. An outer specialization is applied to nested call arguments and the resulting instances enter
a bounded work queue. The verifier then checks that each instance agrees with its source declaration,
type arguments, constraints, selected call bindings, substituted expressions, parameter types,
return type, effects and exact target identity.

Verified instances can continue through the existing Windows x86-64 native pipeline when their
concrete signatures and bodies fit the admitted native subset. LLVM IR is built with the LLVM 23.1.3
C++ API through the existing C++/CLI bridge. It is not produced by generating or parsing LLVM text.
This statement describes the available path; it does not mean every generic source program can be
lowered.

The current slice substitutes already-bound body facts. It does not rerun complete overload
resolution, body typing, flow analysis, effect inference, escape analysis, borrowing, or definite
assignment independently for every concrete instance. That gap is explicit: substitution is not
per-instance semantic rebinding. Any source construct whose correctness depends on that missing
proof is rejected at the native FIR boundary, remains deferred in source analysis, or is described
as a future requirement in the specification.

## Contract and implementation map

| Concern | Detailed reference | Current status |
|---|---|---|
| End-to-end specialization and admission | [Normative specialization contract](../spec/GenericFirSpecialization.sol) | Closed free functions and the stateless top-level object-method subset are implemented. |
| Source examples and expected outcomes | [Specialization case matrix](../spec/GenericFirSpecializationCases.sol) | Examples annotate source binding separately from FIR/native admission. |
| Limits, termination and budgets | [Resource limits](../spec/GenericFirSpecializationLimits.sol), [Instantiation graph](../spec/GenericFirInstantiationGraph.sol) | The builder enforces instance, body-node and nested-type bounds; the full cache contract is normative. |
| Cache identity and determinism | [Cache and determinism](generic-fir-specialization/CacheAndDeterminism.md) | Reuse is compilation-local; there is no persistent or parallel generic FIR cache. |
| Reusable examples | [Source-specification index](../spec/README.md) | Example files record expected outcomes; they are not evidence of a passed test run. |
| Builder and native lowering | [Specialization lowering](generic-fir-specialization-lowering.md) | Closed instances are built before code generation and validated before lowering. |
| Independent FIR verification | [FIR verifier contract](fir-generic-verification.md) | The verifier checks source identity, specialized metadata, expression origins, calls and reachability. |
| Inference, argument mapping and overload choice | [Generic inference](generic-inference.md), [call mapping](generic-fir-specialization/ArgumentMapping.md), [overload selection](generic-fir-specialization/OverloadSelection.md) | Analysis precedes specialization and supplies the selected closed call facts. |
| Nested type substitution | [Nested substitution](generic-fir-specialization/NestedSubstitution.md) | Supported resolved shapes are substituted structurally; unsupported body operations remain gated. |
| Type constraints | [Constraint contract](generic-fir-specialization/Constraints.md) | Source constraints are checked against resolved type identities; native representation remains narrower. |
| Effects and checked exceptions | [Effect contract](generic-fir-specialization/Effects.md) | The selected generic declaration's substituted effects are compared with the verified target. |
| Nullability and bottom type | [Nullability contract](generic-fir-specialization/Nullability.md) | Source typing requirements are documented; nullable native representations remain outside the slice. |
| Primitive and array ABI | [Primitive ABI](generic-fir-specialization/PrimitiveAbi.md) | Admission is intentionally finite and target-specific. |
| Recursion and termination | [Recursion and limits](generic-fir-specialization/RecursionAndLimits.md) | Exact keys are reused; distinct keys consume bounded resources. |
| JSON and diagnostics | [JSON and diagnostic contract](generic-fir-specialization/JsonAndDiagnostics.md) | Documents schema 5 metadata, source identity, origins, and phase-specific diagnostics. |
| Artifact and runtime boundaries | [Artifact contract](generic-fir-specialization/Artifacts.md), [native artifacts](library-artifacts.md) | The generic slice does not create C ABI or external `.solib` generic exports. |
| Generic owners and members | [Owner/member contract](generic-fir-specialization/OwnersAndMembers.md), [member resolution](member-resolution.md) | Only methods on stateless, non-generic top-level objects enter generic FIR; other members remain outside it. |
| Storage, arrays and GC roots | [Storage and root contract](generic-fir-specialization/StorageAndRoots.md), [Yellow Tea ABI](yellow-tea/compiler-runtime-abi.md) | Generic collections, spans, nullable layouts and full per-instance root maps remain outside the slice. |
| Function values, lambdas and closures | [Function-value contract](generic-fir-specialization/FunctionValues.md) | Source analysis covers some contextual values; generic indirect FIR and closure lowering remain unsupported. |
| Runtime and collectors | [Yellow Tea](yellow-tea/index.md), [runtime ABI](yellow-tea/compiler-runtime-abi.md) | This specialization slice does not broaden runtime layouts or GC root rules. |

## Pipeline and invariants

The specialization pipeline starts after ordinary source binding has selected a target. Each phase
owns a different proof obligation:

1. **Candidate analysis** builds candidate-local argument mappings and type substitutions. A failed
   candidate cannot contribute partial type evidence to another candidate.
2. **Selection** identifies one source declaration and its resolved type arguments. Declaration
   spelling alone is insufficient to identify a function or an instance.
3. **Instance-key construction** combines the selected source declaration identity with the ordered
   closed type-argument identities. Display names and formatted source text do not form cache keys.
4. **Work-queue expansion** assigns one FIR function identity to each admitted key. Repeated exact
   keys refer to the existing instance, including recursive calls. Distinct keys consume the
   compilation's bounded specialization budget.
5. **Body construction** substitutes resolved types in signatures, locals, expression metadata and
   effects. It keeps the origin of each source expression available to the verifier.
6. **Verification** rechecks the constructed graph against immutable source-analysis facts. A call
   must point to the selected instance and carry the same closed type arguments and concrete
   parameter/result/effect types.
7. **Native lowering** accepts only verified FIR. The LLVM bridge receives structured operations and
   resolved types; unsupported source constructs do not turn into placeholder instructions.

An implementation must preserve all of these invariants together. In particular, a concrete
signature does not prove that a generic body is valid for that signature. A selected source overload
does not prove that its target function was instantiated correctly. A successful verifier pass does
not prove that the artifact can be linked against every SolRT mode or exported to another Sol
container. These are separate stages and are covered by separate contracts.

## Instance identity

The source declaration is identified by its source-analysis workspace identity, not by its simple
name. The type-argument sequence is ordered by the declaration's type-parameter ordinals. For a
function declared as `func <K, V> make(key: K, value: V)`, `make<String, int>` and `make<int,
String>` are different instances even if their resulting ABI shapes happen to be identical.

Resolved type identity includes the structure that source analysis owns: nominal declaration,
arguments, nullability, function effects, and ref-like classification. A pretty-printed type name
can be used in diagnostics but must not replace that identity in a cache key. The verifier rejects
foreign workspace types and open parameter references in instance arguments.

The current implementation uses stable type IDs within one analysis workspace and uses invariant
culture when rendering those IDs for internal key material. This is a compilation-local identity,
not a cross-process cache format. Persistent caches, reproducible cross-machine fingerprints and
incremental invalidation require a separately versioned contract.

## Generic call record

For a generic selected target, a FIR call records the closed type arguments in declaration order and
references the exact specialized function ID. For a nongeneric call, `typeArguments` is empty. The
call's return type is the selected source binding's return type after substitution. Its argument
slots retain the parameter index and passing mode; source argument evaluation order remains tied to
the source mapping and origin data.

The verifier checks that the call's argument sequence is complete and consistent with the target's
concrete parameters. It also checks that selected parameter types, result type and declared effects
agree with source analysis after applying both the caller's and callee's substitutions. A generic
call from a specialized body composes both maps: outer type parameters are first replaced by the
caller instance, then callee parameters by the selected call arguments.

Calls cannot request a fallback overload after a native-ineligible winner has been selected. Native
admission may reject the selected function, but it must not silently substitute a less-preferred
source candidate.

## Supported native type surface

The current native admission set is deliberately finite. It includes the primitive shapes already
admitted by verified FIR, `String`, and the one-level array ABI forms described by the primitive ABI
reference. Nullability, arbitrary object layout, nested value layouts, collection representation,
ref-like generic storage and open generic ABI are not implied by those examples.

The specializer does not synthesize hidden runtime layout rules. It asks the existing type workspace
for the resolved type and preserves its identity through FIR. Runtime allocation, object headers,
array descriptors, String ownership, exception carriers and Yellow Tea roots are governed by their
own compiler/runtime contracts. A future widening of the generic native surface must update those
contracts and their ABI qualification together.

## Resource limits

Specialization is bounded so that a small source file cannot request unbounded native work through
recursive type growth or duplicated bodies. The current builder caps one compilation at 4,096
specialized functions, 16,000,000 source AST nodes counted across specialized bodies, and 128 nested
structural type levels. `TypeSpecialization` also has a separate 100,000-additional-type workspace
capacity; exhaustion while FIR constructs a concrete generic body is reported as `SOL2403` without
incrementing the 4,096 function count. The same interner limit reached by another phase belongs to
that phase's diagnostic contract. Cancellation is checked while draining the work queue and while
the verifier traverses instances.

These caps are operational limits, not language-level restrictions on source genericity. A resource
exhaustion diagnostic must identify which bound was crossed and preserve a stable source origin.
The compiler must not emit a partial `VerifiedFirProgram`, silently omit a reachable instance, or
reuse a key with a different argument sequence when a budget is exhausted. Limits are documented
with their phase and diagnostic in the resource references above.

## Diagnostics and failure ownership

Failures should identify the phase that owns them:

| Code | Owner | Meaning |
|---|---|---|
| `SOL2400` | Native source admission | A bound source shape cannot be represented by the current native FIR subset. |
| `SOL2401` | Entry validation | The requested executable entry point is missing or malformed. |
| `SOL2402` | Independent FIR verifier | Constructed FIR disagrees with its source proof or violates a verified invariant. |
| `SOL2403` | Specialization resource accounting | A specialization or its expansion exceeded an explicit work bound. |

These codes are not interchangeable. Inference failure belongs to source analysis and should not be
relabelled a resource failure. A body outside native scope is not a verifier bug. A verifier failure
must not be hidden as source rejection after FIR construction. Cancellation is not a diagnostic
claim that the source is invalid.

## How to read the specifications

The files under `spec/` describe language and compiler requirements in normative terms. The
`examples/semantics` files use comments and `expect` records to make expected source and native
outcomes reviewable. A fixture is not automatically a test and does not prove that the implementation
has executed it. Current-state language such as *implemented*, *rejected*, *deferred* and *normative*
must remain explicit.

The implementation guides explain the current pipeline and its limitations. API documentation
describes the C# surface. These sources complement one another: none of them alone claims complete
Sol generic semantics or a stable external FIR serialization format.

## Next implementation work

The next work needed before broad generic native support is not merely more substitution cases. It
includes:

- instance-specific semantic rebinding, including expression typing, overloads, conversions,
  constraints, effects, flow facts and definite assignment;
- explicit rules for generic owners, methods, constructors, accessors, operators and function values;
- a coherent representation for nullable, reference, value, ref-like, array and collection types;
- inter-module identity, ABI naming and import/export rules for generic functions;
- cache invalidation tied to source declarations, constraints, target triple, runtime ABI and compiler
  version;
- cycle diagnostics for expanding instantiation graphs, separate from legal recursion on an exact
  existing key;
- end-to-end artifact qualification for `bin`, `cdylib`, `staticlib` and `solib`, including default
  dynamic SolRT and explicit `--static-solrt-lib` behavior;
- regression and fuzz oracles once the project authorizes those validation workflows for this area.

Until those pieces are implemented and qualified, the native specialization slice remains bounded
to the exact cases admitted by the current FIR and verifier.
