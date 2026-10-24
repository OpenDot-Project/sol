<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Coverage-guided fuzz campaigns

Sol keeps deterministic corpus replay as a reproducible regression layer and runs separate
coverage-guided campaigns to discover new paths. `fuzz/campaigns.json` records the budgets and
tool pins. The runner rejects unknown campaign/target names, an over-budget duration, or a driver
whose byte size and SHA-256/SHA-512 do not match the pins. SharpFuzz is pinned by the NuGet lock
file and tool manifest; the native build script checks LLVM 23.1.3.

## Targets and oracle contracts

| Target | Instrumented code | Oracle invariants |
|---|---|---|
| `frontend` | `OpenDot.Sol.Cst`, `OpenDot.Sol.Text`, `OpenDot.Sol.Diagnostics` | Termination, exact byte coverage by tokens/trivia, EOF, valid source spans, bounded CST expansion/diagnostics, and deterministic repeated output. Invalid UTF-8 and parser diagnostics are normal results. |
| `semantic` | `OpenDot.Sol.Analysis`, `OpenDot.Sol.Ast`, `OpenDot.Sol.Psi`, plus CST/text/diagnostic dependencies | PSI/AST ownership, source-span bounds, AST verifier acceptance, deterministic normalized AST and declaration results, and diagnostic source ownership. Fuzz input is a single raw Sol source file; deterministic corpus replay separately tests multi-file modules and order independence. |
| `runtime` | Yellow Tea runtime and `RuntimeCases.cpp` | UTF-8 bytes/runes, string slicing/search/order/hash, reference arrays, exception cause/suppression, and exception-carrier rooting against independent models. |
| `backend` | `BackendCases.cpp` | Typed LLVM C++ API operations, module verification, bounded bitcode/COFF output, ORC arithmetic/branch results, and rejection of invalid handle/terminator operations. The backend must be built with LLVM 23.1.3. |
| `gc` | Yellow Tea runtime and `GcCases.cpp` | Object liveness against a separate slow graph-reachability model through cycles, disconnected components, weak/strong handles, root drops, and root rotation. This target uses one mutator and does not replace a race detector. |

Managed target assemblies are instrumented by SharpFuzz 2.3.0 and driven by the pinned
Windows `libfuzzer-dotnet` executable. Native targets compile the runtime and oracle into each
fuzzer executable with LLVM 23.1.3 `clang-cl`, libFuzzer coverage, and AddressSanitizer. The
backend itself is dynamically loaded and its LLVM implementation is not represented as ASan
coverage. The native campaign disables MSVC STL string/vector annotations to match the ABI of
LLVM's prebuilt Windows libFuzzer runtime; AddressSanitizer remains active, while these fuzzer
executables do not validate those STL annotations. The separate MSVC ASan replay worker keeps
its normal STL annotations. Backend corpus data selects operations from a bounded typed LLVM
operation set; the harness verifies the resulting module and executes its arithmetic through
ORC in-process. It does not launch an external program or accept an arbitrary executable image.

## Budgets and schedule

| Campaign | Seed selection | Time per target | Maximum input | Per-input timeout | Working-set limit |
|---|---:|---:|---:|---:|---:|
| `smoke` (PR) | 400 (20/family) | 20 seconds | 8 KiB | 10 seconds | 1 GiB |
| `nightly` | 2,000 (100/family) | 300 seconds | 8 KiB | 10 seconds | 1 GiB |

The fuzzer also receives `-rss_limit_mb=1024`, `-use_value_profile=1`, and the Sol token
dictionary for managed source targets. Runtime, backend, and GC native targets use
AddressSanitizer. The process-tree limit and a hard campaign timeout supplement the fuzzer's
per-input and RSS checks. A resource-limit termination fails the job.

Each target summary records executed inputs, libFuzzer feature count (`ft`), compiled inline
counter count, sampled process-tree working-set peak, seed count, exact flags, commit, and final
log phase. Inline counters describe instrumentation points in the loaded module; they are not
a source-line coverage percentage. The managed feature count includes runtime/oracle effects
reported by the driver and must not be read as a percentage of Sol code covered.

The PR smoke workflow and scheduled campaign both run only on `windows-2025`. The scheduled
campaign starts daily at 02:00 UTC. The run artifact contains the writable corpus, engine logs,
target summaries, and minimized `crash-*`, `timeout-*`, or `oom-*` inputs. Artifacts are kept
for 14 days. A campaign does not add discovered inputs to Git automatically.

## Local commands

First build the LLVM backend for ORC and ensure the pinned compiler directory is selected:

```powershell
./eng/install-llvm.ps1
./eng/build-backend.ps1
./eng/run-fuzz-campaign.ps1 -Campaign smoke -Target frontend -SecondsOverride 2 -RunId local-frontend
./eng/run-fuzz-campaign.ps1 -Campaign smoke -Target runtime -SecondsOverride 2 -RunId local-runtime
```

Omit `-Target` to run all five targets. `-SecondsOverride` only shortens a campaign and cannot
exceed its configured budget. `-RunId` must be unique; outputs are never overwritten. Native
builds remain MSBuild-orchestrated through `CoverageFuzz.proj`; PowerShell only selects tools,
arguments, budgets, and run output paths.

## Failure handling

An uncaught oracle failure, native sanitizer finding, crash, timeout, memory-limit breach, zero
executions, missing instrumented counters, zero final libFuzzer features, or nonzero engine exit
fails the campaign. The last log lines,
coverage-corpus file, exact seed set, flags, toolchain, commit, and minimized reproducer remain
in the run artifact. Reduce a finding to one input, reproduce it deterministically, then add a
focused regression before changing the seed corpus. “No crash during this budget” is evidence
only for that bounded run; it does not establish correctness or safety.
