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

FIR/LLVM text emission does not load the native bridge. CST and AST emission retain their existing
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
source evaluation order, and declared effects. Its flow graph is an analysis side table rather than
another intermediate language. The pipeline remains CST → PSI → AST → FIR → LLVM IR.

`FirVerifier` independently checks type workspace ownership, literal domains, slot/call identities,
parameter modes, return types, checked call budgets, Boolean conditions, and supported operation shapes.
Its CFG analysis computes reachable blocks and a fixed point of definite assignment. Branches join
by intersection; loop backedges participate in convergence. Local declaration resets prevent a previous
iteration's value from proving initialization in a later iteration. Normal exits must assign every
out parameter and cannot fall through without a return.

Only the verifier can construct `VerifiedFirProgram`, which is the input to LLVM lowering. `FirJson`
schema 1 includes functions, slots, type identities, expression trees, and flow reads/writes/kills/edges,
with byte spans and no host source paths. IDs are local to this compilation, not stable library ABI IDs.

## Qualified native subset

Supported programs use scalar integer/Boolean/single/double values, empty-tuple function results,
local val/var bindings, source functions, stateless object methods, named argument reordering,
scalar ref/out locations, braced branches, condition/three-clause/infinite loops, break/continue,
short-circuit Boolean expressions, collectible String concatenation/content equality, and single-level
array literals/length/index access for supported scalars and String. String has scalar `length`,
`substring(start, length)`, `indexOf(value, start)`, `compareOrdinal`, and `hashCode`. `Gc.collect()`
requests and waits for a concurrent cycle. Integral `std.math.abs/min/max` overloads are source bodies.
The four standard output adapters accept scalar and String arguments, preserve evaluation order,
add no automatic separator, and append one LF for `println`/`errorln`.

Integer arithmetic uses checked LLVM overflow operations. Division guards zero and signed minimum/-1;
shifts reject invalid counts before LLVM instructions can observe poison-producing operands. Floating
operations preserve normal IEEE behavior and do not enable fast math. Runtime bounds and arithmetic
failures report an unhandled error and exit with status 1 in this bootstrap.

The native gate rejects general class construction, nested/value-layout arrays, closures, ref-struct borrowing,
generic body specialization, user default/vararg lowering, custom operators/conversions, exception
handlers, defer cleanup, reflection, and unqualified half/quad operations. Source body analysis may
understand more of these constructs; that does not make them eligible for native generation.
Bottom-typed source calls and global/receiver storage initialization also remain outside this first gate.

## LLVM boundary

`OpenDot.Sol.Lowering` selects LLVM instructions in C#. The C++20/CLI bridge marshals UTF-8 LLVM IR
to a native translation unit using LLVM's C interface from the exact `LLVM-C.dll`. This avoids exposing
native LLVM types or unresolved opaque typerefs in managed metadata. Native contexts, buffers, modules,
and target machines have deterministic RAII ownership. Errors are translated at the managed boundary.

The native unit parses the generated IR, verifies it, runs the `default<O1>` pass pipeline, verifies
again, and emits AMD64 COFF. C# launches `lld-link` with structured arguments in a temporary staging
directory. A missing backend, wrong LLVM version, verification failure, or linker failure cannot
replace an existing executable. The frontend SDK remains usable without LLVM installed.

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
