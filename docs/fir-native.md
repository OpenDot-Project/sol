<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Verified FIR and the first native programs

Development builds can produce verified FIR, LLVM IR, and Windows x86-64 AOT executables for a
restricted bootstrap subset. This functionality is not in the published Sol 0.0.1 release. The
complete language, general object model, native exception unwinding, and Yellow Tea are still incomplete.

## Build and run

Use the pinned .NET 10 SDK, MSVC C++/CLI tools, a Windows x64 SDK, and LLVM 23.1.3. `LLVM_ROOT` must
refer to the LLVM SDK root containing `bin`, `include`, and `lib`. The build validates the LLVM
runtime version as well as compiling and loading the bridge; a version string alone is insufficient.

From the repository root in PowerShell:

```powershell
dotnet restore Sol.slnx --locked-mode
./eng/build-backend.ps1
$env:SOLC_NATIVE_ROOT = Join-Path $PWD 'out/native/backend'
$solc = 'src/managed/OpenDot.Sol.Cli/bin/Release/net10.0-windows/OpenDot.Sol.Cli.dll'
dotnet $solc spec/examples/native/FirstProgram.sol -o out/hello/Hello.exe
./out/hello/Hello.exe Alice 20
dotnet $solc spec/examples/native/FirstProgram.sol --static-solrt-lib -o out/static/Hello.exe
./out/static/Hello.exe Alice 20
```

The program prints `Hello, Sol!`, `42`, and `Argument count: 2`. The runtime constructs `[]String`
from the Windows wide-character arguments and excludes the executable path. These are native PE
applications; their import tables have no CLR dependency. Solc itself remains a managed .NET 10 tool.

Dynamic linking is the default and places `SolRT.dll` beside the executable. `--static-solrt-lib`
links the static runtime and does not copy or import that DLL. Artifact type is a separate option:
development builds also produce scalar C-ABI `cdylib`/`staticlib` artifacts and a closed-subset `solib`
container. ORC executes in a separate native guest process:

```powershell
dotnet $solc spec/examples/native/FirstProgram.sol -C llvm-compiler=orc -- Alice 20
```

ORC defaults to `--emit=jit` and accepts application arguments after `--`. It does not create an
executable with `-o`; `--emit=llvm-ir` remains an inspection operation. Both runtime linkage modes
are available. The compiler's CLR GC and guest Yellow Tea never share a process or object reference.
See [library artifacts](library-artifacts.md) for native library restrictions and container boundaries.

## FIR inspection without LLVM

```powershell
dotnet $solc spec/examples/native/FirstProgram.sol --emit=fir -o out/Hello.fir.json
dotnet $solc spec/examples/native/FirstProgram.sol --emit=llvm-ir -o out/Hello.ll
```

FIR inspection does not load LLVM. LLVM inspection constructs a native module through the C++ API bridge and uses the LLVM printer; it requires the backend component. CST and AST emission retain their existing
per-file behavior. FIR, LLVM, and executable emission operate on the complete explicit source set;
`-o` can name one combined output for multiple input files. Native inputs and inspection outputs
still receive whole-input preflight and transactional replacement protection.

## Admission and verification

`FirCompilation.Create` composes the supplied AST snapshots with compiler-owned bootstrap declarations,
then runs declaration, type, scope, default, and body analysis. Runtime declarations are embedded
resources authored under `src/stdlib/std`; ordinary user
declarations cannot impersonate their native adapters by using a matching spelling or path.

The AST-to-FIR builder rejects unresolved expressions and unsupported constructs with `SOL2400`.
It does not translate an unverified body into a successful empty function. The entry is exactly one
accessible `Main` object method accepting `[]String` and returning `()` for bin output. Library
compilations have no entry and verify that distinction before lowering.

FIR records resolved type identities, slots, exact callable targets, parameter mappings, passing modes,
source evaluation order, and declared effects. Closed module-level generic free-function calls in the
first ABI subset produce monomorphic FIR functions. The original declaration origin remains attached;
function specialization arguments and call-site type arguments are recorded and verified in order.
Nested instances are interned by declaration identity plus closed type identities. The flow graph is
an analysis side table rather than another intermediate language. The pipeline remains
CST → PSI → AST → FIR → LLVM IR.

`FirVerifier` independently checks type workspace ownership, literal domains, slot/call identities,
parameter modes, return types, checked call budgets, Boolean conditions, and supported operation shapes.
Each FIR function is tied back to its AST declaration: name, substituted result, parameter
modes/types/names, and ordered effect metadata must agree with source analysis. A generic instance
must have a complete closed substitution, satisfy its declaration constraints, and have a unique
declaration-plus-arguments identity. Each call is tied back to the selected overload, caller-substituted
type arguments, exact target instance, and argument-origin mapping, so changing the call target or
swapping same-typed named arguments cannot pass verification. Function effect entries must be
workspace-owned Throwable types without duplicates. The verifier retains explicit unchecked effect
metadata while only requiring checked effects in caller budgets.
Its CFG analysis computes reachable blocks and a fixed point of definite assignment. Branches join
by intersection; loop backedges participate in convergence. Local declaration resets prevent a previous
iteration's value from proving initialization in a later iteration. Normal exits must assign every
out parameter and cannot fall through without a return.

Only the verifier can construct `VerifiedFirProgram`, which is the input to LLVM lowering. `FirJson`
Schema 6 includes functions, slots, type identities, expression trees, ordered call evaluation values,
call-value references, function references and indirect calls. Function type records include parameter
modes and checked effects. Flow call entries use a null direct target for indirect calls and preserve
their declared effect type IDs and source byte spans. These are conservative call-site effect facts;
the native subset does not lower catch or exceptional CFG edges. The JSON contains byte spans but no
host source paths. IDs are local to this compilation, not stable library ABI IDs. Schema 6 adds
function-value nodes and signature metadata; it invalidates consumers that assumed schema 5 or earlier.
The enclosing `.solib` compiler identity changes with the compiler assembly.

## Qualified native subset

Supported programs use scalar integer/Boolean/single/double values, empty-tuple function results,
local val/var bindings, source functions, closed module-level generic free-function specializations
whose type arguments lower to supported primitives, String, or one-level arrays of those values,
stateless object methods, named argument reordering,
scalar ref/out locations, braced branches, condition/three-clause/infinite loops, break/continue,
short-circuit Boolean expressions, collectible String concatenation/content equality, and single-level
array literals/length/index access plus dynamic-array `for-in` for the exact supported scalar and
String element subset. Array iteration evaluates its array expression once, snapshots the fixed length,
loads in ascending order, and copies each element into the item slot. The source identity and item slot
are independently checked by FIR verification. The CFG includes zero-trip, body, continue, break, and
backedge paths; lowering retains and roots the array reference, item references where applicable, and
the saved length/index state. Polls on the loop backedge preserve bounded guest-runtime progress. String has scalar `length`,
`substring(start, length)`, `indexOf(value, start)`, `compareOrdinal`, and `hashCode`. `Gc.collect()`
requests and waits for a concurrent cycle. Integral `std.math.abs/min/max` overloads are source bodies.
The four standard output adapters accept scalar and String arguments, preserve evaluation order,
add no automatic separator, and append one LF for `println`/`errorln`.

Integer arithmetic uses checked LLVM overflow operations. Division guards zero and signed minimum/-1;
shifts reject invalid counts before LLVM instructions can observe poison-producing operands. Floating
operations preserve normal IEEE behavior and do not enable fast math. Runtime bounds and arithmetic
failures report an unhandled error and exit with status 1 in this bootstrap.

The native gate rejects general class construction, nested/value-layout arrays, fixed-array iteration,
user-defined iterator/cursor iteration, closures, ref-struct borrowing, generic owner/member
specialization, open generic calls, function-value specialization, unsupported generic ABI types,
user default/vararg lowering, custom operators/conversions, exception
handlers, defer cleanup, reflection, and unqualified half/quad operations. Source body analysis may
understand more of these constructs; that does not make them eligible for native generation.
Bottom-typed source calls and global/receiver storage initialization also remain outside this first gate.

Generic FIR currently substitutes the already-bound body facts; it does not rerun full source
overload and body analysis under every concrete type argument. Its verifier checks the concrete
signature, types, effects, constraints, and call binding before LLVM construction. Generic body
rebinding and specialization of operators or members that depend on the concrete type remain future
work, so the feature is a bounded compiler slice rather than full generic semantics.

## LLVM boundary

`OpenDot.Sol.Lowering` selects instructions, blocks, roots and guards in C#. An immediate operation
interface crosses the thin C++20/CLI bridge into LLVM 23.1.3 C++ `LLVMContext`, `Module`, `IRBuilder`,
and `TargetMachine` APIs. It never builds or parses textual LLVM assembly as a compilation step.
Opaque handles carry session identities; foreign, wrong-kind, cross-thread and disposed use is rejected.
Native contexts, modules and target machines have deterministic ownership. Errors are translated at
the managed boundary. LLVM text is only inspection output produced by the LLVM printer.

The SDK uses the static CRT. `OpenDot.Sol.LlvmNative.dll` owns its C++ objects and allocations behind
an opaque C ABI; the C++/CLI adapter uses the dynamic CRT. Buffers are freed in the component that
allocated them. Pinned static compression/XML dependencies are built under `out/dependencies` by
`eng/install-llvm-native-deps.ps1`; first-party projects remain MSBuild projects.

The native component verifies the module and optimizes a clone with the O1 pipeline, verifies
again, and emits AMD64 COFF. ORC receives binary LLVM bitcode from the same constructed module,
verifies it and owns execution in the native guest. C# supplies structured arguments to the embedded
LLD.COFF and LLD.Common 23.1.3 libraries through the native bridge. The same driver handles `/lib`
archive production; Solc launches neither `lld-link.exe` nor `llvm-lib.exe`. Linking uses a temporary staging
directory. A missing backend, wrong LLVM version, verification failure, or ordinary linker failure cannot
replace an existing executable. The frontend SDK remains usable without LLVM installed.

LLD driver calls are serialized according to its re-entry contract. Ordinary failures return diagnostics
and allow later links. An unrecoverable LLD failure requires terminating the compiler process; it cannot
continue with potentially corrupted driver state. Cancellation is checked before and after embedded
linking; an active native link is not forcibly interrupted.

## SolRT bootstrap lifetime

This runtime now includes an experimental concurrent collector. Argument arrays have a strong entry
handle; dynamically created Strings and arrays are collectible when their roots disappear. Literal storage is static;
its descriptor is atomically published before the value is returned. Object headers are 16 bytes,
String data begins at byte 24, and array elements begin at byte 32. Descriptor layout version 0 is
provisional and does not establish the future collectible ABI.

Generated functions register zero-initialized root slots, publish reference stores through a barrier,
poll on entry/backedges, and pop frames on normal returns. A worker performs concurrent mark/sweep
between root handshakes. Native C++ exception carriers own traced Throwable handles, but source EH
lowering remains gated. Individual aligned allocations currently replace the planned span allocator.
Read the [implementation inventory](yellow-tea/current-implementation.md) for limits and qualification
work; the [design chapters](yellow-tea/index.md) are not claims of full ABI or GC completion.

## Validation

`eng/test-native.ps1` compiles and executes positive/negative cases, checks PE imports for dynamic/static
SolRT and absence of CLR, verifies evaluation order and Unicode arguments, and preserves a previous
artifact on invalid input. `eng/test-runtime.ps1` builds a native ASan harness for repeated argument
allocation/shutdown, layout/alignment, UTF-8 conversion, and literal descriptor publication.

Managed regressions test FIR construction, rejection, cancellation, deterministic inspection, and
independent verifier failures after deliberate FIR corruption. The Native backend workflow uses only
Windows 2025 and verifies the pinned LLVM archive digest before extraction. CodeQL's native build
also captures the runtime and unmanaged bridge translation unit. These checks do not certify a
collector, full language semantics, or native exception support.
