<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Fuzz corpus inventory

The repository stores **100,000 distinct input byte sequences**, divided into five targets.
Each target has 20 families of 1,000 inputs, stored in 40 JSONL shards of 500 records. The
managed verifier checks source SHA-256 digests and global uniqueness across all five targets.
Generated inputs, verified inputs and completed replay counts are separate evidence.

| Target | Deterministic seeds | Oracle and execution |
|---|---:|---|
| Frontend | 20,000 | Managed byte-preserving lexing, CST spans, bounded diagnostics, repeated parse equality, and accept/reject contracts; coverage-guided CST campaign. |
| AST/declaration analysis | 20,000 | Multi-file PSI/AST construction, AST verifier, stable inspection, declaration determinism, module graph order independence, and diagnostic ownership; coverage-guided single-source PSI/AST/declaration campaign. |
| Runtime | 20,000 | String byte/rune models, concat/slice/search/order/hash, reference arrays, exception causes/suppression, and carrier rooting; coverage-guided native runtime campaign. |
| LLVM backend | 20,000 | Typed native API operations, LLVM verification, bitcode/COFF output, ORC execution against independent arithmetic/branch results, and selected invalid-operation checks; coverage-guided native LLVM API campaign. |
| Yellow Tea GC | 20,000 | Allocated graph topologies, explicit roots, weak/strong handles, cycles, root changes, and collector results against independent reachability; coverage-guided native GC campaign. |

The checked-in 100,000 inputs are deterministic seed corpora. They are distinct input bytes,
not 100,000 coverage-guided executions or distinct code paths. The separate campaign workflow
mutates selected seeds using libFuzzer and checks them with the same target oracles. Managed
coverage comes from SharpFuzz-instrumented Sol assemblies. Native runtime and GC source files
and their harnesses are compiled by LLVM 23.1.3 `clang-cl` with libFuzzer and AddressSanitizer;
the loaded LLVM backend DLL, upstream LLVM libraries, and generated ORC machine code are not
ASan-instrumented. No fuzz duration proves conformance or absence of defects.

Declaration analysis does not cover all expression type checking, FIR flow/escape analysis,
or all lowering paths. The backend corpus does not fuzz the complete LLD linker, every artifact
container, or arbitrary external libraries. GC graph replay is single-mutator and is not a
concurrency/race detector. The coverage-guided GC target shares this single-mutator limitation.

## Representation and integrity

Records carry a scoped case ID, family, explicit expectation, source SHA-256 and base64 bytes.
Each target manifest binds shard digests, counts, target/schema identity and generator digest.
The [frontend corpus](frontend-fuzz.md) includes arbitrary/malformed UTF-8. Semantic inputs
encode explicit virtual source sets; the analyzer runs with `RequireCore = false`, without
inventing a standard library. Diagnostics are valid outcomes in many semantic families.

Native inputs use a 64-byte, versioned `SFZ1` trace envelope. Target and family select the
test model; every payload byte influences deterministic choices. The corpus verifier creates
a bounded binary pack with per-input SHA-256 digests; the native reader validates lengths,
target consistency, digests and trailing bytes before completing replay. Native case IDs and
hashes are flushed to a journal before runtime/backend/collector operations.

No corpus input is a shell command. The backend generates constrained typed LLVM operations
through the native C++ API; it does not generate or parse LLVM text as compiler input.
ORC executes bounded arithmetic programs in the native worker, without loading guest GC
into a CLR process. Runtime and GC targets also run in a plain C++ worker.

## Running

```powershell
./eng/new-frontend-fuzz-corpus.ps1 -Check
./eng/new-expanded-fuzz-corpus.ps1 -Check
./eng/build-backend.ps1
./eng/test-fuzz.ps1 -Target all
./eng/test-fuzz.ps1 -Target managed
./eng/test-fuzz.ps1 -Target native
./eng/test-fuzz.ps1 -Target gc -CaseId case-15500
```

Native backend replay requires an already-built LLVM 23.1.3 backend and the supported MSVC
toolchain. The runner builds the ASan native worker through MSBuild in an independent
`fuzz-harness` output/intermediate flavor. Native runtime/GC sources and the native harness
are instrumented; the loaded LLVM native DLL, upstream LLVM and generated ORC code are
not claimed to be ASan-instrumented. A deliberately poisoned test byte must produce an
ASan report in a separate instrumentation self-test process before native replay proceeds.

The default limits are 60 seconds and a sampled 512 MiB working set per worker. Managed
frontend inputs have a two-second cooperative cancellation budget; semantic inputs have five
seconds. Working-set sampling every 100 ms is not an instantaneous OS memory ceiling. Hard
worker termination bounds a stalled case, and the last journal entry identifies the active
input. `-WorkerTimeoutSeconds` and
`-MemoryLimitMB` override limits explicitly and are recorded in the run summary.

The global inventory is verified even for a single-case replay. `-CaseId` is scoped by
`-Target`; selecting several targets replays that ID separately in each selected corpus.
Reports under `out/fuzz/replay/` include actual completed counts, corpus identity, source SHA
and dirty-tree state, worker/backend digests and configured limits. Failures, missing reports,
wrong counts, timeouts and unexpected sanitizer errors fail the run. An input count never
substitutes for triage and a minimized regression when a failure is found.

Windows CI replays 40,000 managed inputs in the dedicated deterministic fuzz workflow and
60,000 native inputs in native validation. `coverage-fuzz.yml` runs bounded coverage-guided
smoke campaigns on PRs and a longer daily campaign. Native fuzz sources receive CppCoreCheck,
InspectCode and traced CodeQL compilation. Only the explicit run directory under
`out/fuzz/campaigns/<run-id>/` is uploaded; private notes and local tool caches are not artifacts.
