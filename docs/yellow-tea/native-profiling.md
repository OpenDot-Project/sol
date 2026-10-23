<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Native runtime profiling

The repository includes a native harness and PowerShell runner for Yellow Tea experiments.
These tools exercise the runtime directly in a guest-native process.
They do not load the runtime into the managed compiler and do not require source-level classes
or threading to be implemented before the native collector can be tested.

Profiles are local development evidence, not a published performance guarantee.
All reports identify the workload, build mode, heap limit and sampling method.
The runner validates both JSON structure and accounting before writing a report.

## Build and run

Run from the repository root in PowerShell with MSVC v143 or v145 installed:

```powershell
./eng/measure-runtime.ps1
```

The runner builds the Release native harness with `/W4 /WX` and the configured CppCoreCheck
properties. It uses `TestHarness=true`, `StaticRuntime=true` and `SanitizeRuntime=false`.
Output and intermediate directories are separate from the ASan correctness harness.
It neither disables the normal sanitizer test path nor changes production linkage defaults.

The default run uses all six scenarios, 10,000 nodes, five cycles, 32 root slots and seed 1.
Only the concurrent scenario uses the default four mutator threads.
Every other scenario uses exactly one mutator and rejects an unused multi-thread setting.
The managed heap limit defaults to 128 MiB.

For a smaller explicit run with event records:

```powershell
./eng/measure-runtime.ps1 -Scenario wide,churn -Nodes 2000 -Cycles 3 -Roots 17 -Events
```

For repeated concurrent runs after building:

```powershell
./eng/measure-runtime.ps1 -Scenario concurrent -Threads 4 -Repeat 5 -SkipBuild
```

`-SkipBuild` requires an existing profile executable.
It is the caller's responsibility to build the intended source revision first.
The script does not silently fall back to an ASan or stale executable in a different directory.
Record the commit and machine details alongside evidence used for comparison.

## Runner settings

| Parameter | Range/default | Purpose |
|---|---|---|
| `Scenario` | Six known names; all by default | Select native graph/allocation workload. |
| `Nodes` | 16–200,000; default 10,000 | Nodes allocated per workload cycle and mutator. |
| `Cycles` | 1–10; default 5 | Repeated workload construction; not exact GC-cycle count. |
| `Roots` | 1–4,096; default 32 | Registered slots per frame. |
| `Threads` | 2–8; default 4 | Mutator count for the concurrent scenario. |
| `Seed` | Unsigned 32-bit; default 1 | Repeatable payload-size choices in churn. |
| `Repeat` | 1–20; default 1 | Fresh child processes for each scenario. |
| `TimeoutSeconds` | 1–600; default 120 | Bound each child process. |
| `HeapLimit` | Default `128MiB` | Passed to the runtime's checked heap-limit parser. |
| `Events` | Off | Enable the 4,096-record native event ring. |
| `OutputDirectory` | `out/native/runtime/profiles` | Destination for validated JSON and build log. |
| `SkipBuild` | Off | Reuse the profile-harness executable. |

The product's Solc CLI does not gain a `gc-profile` command.
The harness's positional mode is an internal test interface.
It rejects signs, whitespace, suffixes, missing arguments, overflow and invalid combinations.
At most two million node allocations may be sampled in one process.
Wide-array construction adds one timed array allocation per workload cycle.

The timeout kills the child's process tree and reports failure.
A nonzero exit, unexpected stderr, malformed JSON or inconsistent report prevents publication.
File names include a fresh run identifier and use create-new semantics.
Existing evidence is not overwritten by another run.
No automatic upload or background monitoring is configured.

## Workloads and correctness checkpoints

### Dense

Nodes occupy the normal small class for the two-reference fixture type.
Each new node points to the previous head, and a registered root retains the whole chain.
Remaining root slots duplicate that head without adding more live objects.
After collection, the harness checks the exact live count and every node's ordinal payload.
After dropping the frame, another collection must reclaim the entire chain.

### Sparse

The graph and reference descriptor match Dense, but each allocation requests 2,000 extra bytes.
This selects a larger small-object class and reduces address density.
Comparisons must account for the larger zeroing cost and physical commitment.
It is a locality stress case, not an isolated cache-miss measurement.
Live-count and chain-content checks are identical to Dense.

### Wide

A single rooted reference array owns all leaf nodes.
Its descriptor has one reference per element and its scan spans many marker batches.
The live checkpoint expects `nodes + 1` objects.
All element payloads are checked after collection.
The reclaim checkpoint requires zero remaining objects after the frame is removed.
This workload exercises resumable cursors rather than only many short descriptors.

### Churn

Nodes have no outgoing references and vary aligned payload size using the requested seed.
They replace slots in a rolling root frame.
Exactly `min(nodes, roots)` objects must survive the live checkpoint.
The harness checks that each surviving payload belongs to its expected root-slot residue.
Dropping the frame must reclaim the remainder.
Earlier collections can happen during allocation and are included in diagnostics.

### Roots

Two nested frames each contain `roots` slots.
They duplicate ownership of one live linked graph.
This increases frame and root-slot traversal without increasing graph node count.
The settings field is slots per frame, so this scenario registers twice that number.
Root snapshot timings include active scanning, not mutator arrival delay.
Safepoint wait is reported in a separate histogram.

### Concurrent

Each native mutator attaches, registers its own rolling root frame and allocates leaf nodes.
A native-safe start gate aligns their start without blocking collector handshakes.
Mutators request collection periodically and verify rooted payloads after collection.
The main harness thread waits for them in NativeSafe mode.
Thread-local timing vectors are merged only after all workers detach.
The final checkpoint requires complete reclamation.
Scheduling is intentionally nondeterministic even when the seed is fixed.

## Timing interpretation

Allocation latency brackets the entire `sol_gc_alloc` call.
It includes polling, index publication, slot-cache work, native refill, assists and collection waits.
It excludes the harness's later graph-edge stores and payload assignment.
All call durations are retained, bounded by the operation-count limit.
The native harness stores these samples outside the guest managed heap budget.
Two million 64-bit samples alone require approximately 16 MB of native memory.

The allocation report uses nearest-rank p50, p95 and p99 from all recorded calls.
`max_ns` is the maximum actual allocation duration observed in the run.
`total_ns` sums durations across calls; with concurrent mutators it can exceed elapsed wall time.
The profiler does not report an allocation p99.9 from too few samples or invent a distribution fit.

Collector histograms use fixed logarithmic-like duration buckets.
Their percentile fields are explicitly named `p50_upper_ns`, `p95_upper_ns` and `p99_upper_ns`.
They identify a bucket upper bound, not an exact observed percentile.
The maximum field remains an actual observation.
The final bucket's bound is `UINT64_MAX`, representing an open-ended overflow range.
Do not turn that sentinel into a claimed pause duration.

Events are disabled by default because timestamping and recording perturb measurements.
Counter updates and histogram timing remain active.
The profile executable is a test-harness build: native failure/pause hooks are compiled in but
their observers remain unset. Those checks are absent from the production runtime.
This is another reason to qualify production binaries before publishing comparative timings.
Compare event-enabled and event-disabled runs separately.
ASan adds a different source of overhead and is rejected by the timing-report validator by default.
Correctness tests can explicitly validate sanitized reports without treating them as benchmarks.

## JSON report layout

`schema_version` is 1 and `kind` is `yellow-tea-native-profile`.
The schema is intentionally closed: unknown or missing fields fail validation.
An extension requires a deliberate schema and reader change.

| Object | Contents |
|---|---|
| `settings` | Scenario, counts, seed, mutators and effective physical heap limit. |
| `environment` | Windows x64 identity, MSVC version, logical processors, sanitizer flag and clock. |
| `allocation_latency` | Sampling method, calls, totals, maximum and exact nearest-rank percentiles. |
| `diagnostics` | Version, epoch, phase, counters, histogram bounds, histograms and ready queue. |
| `allocator` | Final physical commitment, reservation, capacity, spans, objects and reused slots. |
| `cache` | Cache hits, refills, unused slots, returns, flushes and dedicated allocations. |
| `heap` | Final registered bytes, object count, live bytes and completed epoch. |
| `checkpoints` | Expected/actual objects, physical/live bytes and completed epoch. |
| `event_ring` | Enable flag, capacity, retained sequence window, loss count and numeric records. |

The final registered heap, large allocations and occupied slot capacity must be empty.
The allocator may retain at most one empty 64 KiB span per size class.
The validator requires its final reservation and commitment to match that bounded reserve.
This does not mean all native process allocations have been returned to Windows.
Collector metadata, timing vectors, CRT state and the process itself are outside those counters.
All profiles are captured before runtime shutdown resets diagnostic counters.

## Validation and corrupted evidence

Run the report validation suite with freshly built small workloads:

```powershell
./eng/test-runtime-profiles.ps1
```

To validate reports already collected by the runner:

```powershell
./eng/test-runtime-profiles.ps1 -ProfilePath ./out/native/runtime/profiles/<run>-dense-1.json
```

The suite validates real reports, then corrupts independent copies of one report.
It must reject wrong numeric types, missing fields, unknown schemas, invalid workload ranges,
impossible percentile order, inconsistent allocation/reclaim counts, incorrect histogram ranks,
uncompleted marks, nonempty final heaps, broken checkpoints and inconsistent ready queues.
When event records are available it also rejects duplicates, backward timestamps,
future epochs, unknown event kinds and disabled rings that still claim records.

Arithmetic is checked in decimal precision for 64-bit unsigned counters.
Floating-point or string-shaped counters are rejected rather than coerced to integers.
Histogram bucket sums must equal samples, and reported percentile bounds must match bucket ranks.
The complete retained event window must match its sequence range and overwrite count.
These checks detect damaged or incompatible evidence; they cannot establish honest instrumentation
in an arbitrarily modified executable.

## Comparing revisions

Use the same machine, power policy, MSVC toolset, heap limit and workload settings.
Record the source revision and native build flags with the report paths.
Run multiple fresh processes and alternate revision order when possible.
Do not compare an ASan build with a non-ASan build as a collector improvement.
Do not compare a large live graph with a mostly dead churn run using only elapsed time.

Inspect root counts, scan work, queue claims, assists, physical commitment and event loss first.
A change in those values can explain a latency difference without indicating a faster barrier.
The current profiler does not capture hardware cache counters, process working-set peaks,
CPU model, processor frequency history or a full scheduler trace.
Capture those with appropriate Windows tooling before attributing results to cache locality.
The profiler supplies reproducible native workloads and accounting, not a complete qualification lab.
