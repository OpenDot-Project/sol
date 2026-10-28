<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Native and Sol library artifacts

The Sol 0.1.0 native component supports the four artifact selections for the documented closed verified subset. It does not provide general-purpose library compatibility or complete language support.

| Selection | Primary output | Current boundary |
|---|---|---|
| `bin` | Windows x64 EXE | Exactly one Main object with `main([]String): ()`. |
| `cdylib` | DLL and import LIB | Public module-level scalar functions, unique unmangled C names. |
| `staticlib` | COFF LIB | The same C wrappers and private internal functions, consumed by a native linker. |
| `solib` | Deterministic ZIP container | Closed nongeneric FIR data, module export metadata, LLVM bitcode, native object and digests. |

Example library source:

```sol
module arithmetic

func add(a, b: int): int = a + b
```

```powershell
dotnet $solc Library.sol --artifact-type=cdylib -o out/Library.dll
dotnet $solc Library.sol --artifact-type=staticlib -o out/Library.lib
dotnet $solc Library.sol --artifact-type=solib -o out/Library.solib
```

## C consumers

C wrappers accept supported integer/single/double scalar values and scalar ref/out locations. They
reject String/Object/array references, Boolean representation ambiguity, half/quad, checked exception
escapes, overload export-name collisions, and stateful receivers. Reserved `sol_`/underscore names
and runtime-used CRT names such as `memset` are rejected to prevent helper interposition.
DLL output paths must end in `.dll`; the import library names the actual deployed basename.
Native ref/out callers must supply valid unmanaged storage. Do not infer general FFI support
from a successfully emitted scalar library. Fail-stop arithmetic/bounds errors cannot escape into a
foreign C caller as an uncontrolled native exception.

Wrappers attach an unregistered caller, retain an existing managed registration, or transition a
registered native-safe caller back into guest execution. They restore that state on normal return.
Internal functions remain private symbols, avoiding cross-library `sol_fn_N` collisions.
Call the exported `sol_library_shutdown()` after all guest calls/threads finish and before unloading
the library. Unloading active guest code or a runtime worker is unsupported.
Current qualification covers one generated library per native consumer. Composing multiple generated
libraries, their shared shutdown entry, and their runtime ownership still requires separate work.

Dynamic SolRT remains the default. DLL output places `SolRT.dll` alongside it. Dynamic staticlib
output includes the SolRT import library and DLL for the final native consumer to link/deploy.
`--static-solrt-lib` links the runtime into the DLL or flattens its objects into the static archive.
The final static-library consumer still needs the matching MSVC/Windows system libraries; it is not
a freestanding archive. Artifact and companion output paths use one output transaction.

## Sol containers

`manifest.json` pins schema 2, exact compiler identity, edition 2026, LLVM 23.1.3, Windows target,
runtime layout version 0, linkage, module IDs, and each payload's byte length/SHA-256. Module metadata
lives under `modules/`; verified FIR is UTF-8 JSON data stored under its digest in `fir/*.bin`.
Native objects live under `objects/<target>/`; `llvm/module.bc` holds binary bitcode from the C++ API module; `provenance.json` declares the closed nongeneric subset.
Entry order is ordinal, ZIP timestamps are fixed, and identical inputs yield identical containers.

The reader bounds archive size, expanded bytes, and entry count; rejects traversal, duplicate/case
collisions and undeclared payloads; checks digests and exact compatibility. It reads data and cannot
mint a verified FIR capability or execute contained code. A digest inventory is integrity metadata,
not an authenticated signature or a complete release provenance system.

External Sol module import/linking, generic body specialization, dependency metadata, license
aggregation, debug payloads, and stable runtime ABI 1 qualification are still pending. Current `.solib`
emission and validation do not imply `--extern` consumption is implemented.

## ORC boundary

ORC bin execution uses native `sol-exec-host.exe`; Solc does not execute a guest function pointer in
the CLR process. The host checks LLVM version/target, verifies binary bitcode, binds an explicit runtime symbol
allowlist, and owns the LLJIT context/module/code lifetime. Standard streams and exit status propagate
through the child. Cancellation terminates its process tree. The guest import table must contain no
CLR host or C++/CLI dependency; the compiler bridge must contain no SolRT import.

The compiler transfers bitcode through a dedicated inherited anonymous pipe, using a four-byte
little-endian length followed by 4 bytes to 64 MiB of binary bitcode. The host accepts a decimal pipe
handle and validates its type; it accepts no module filename. Invalid lengths and truncated frames
fail before LLVM execution. The channel is separate from guest stdin, stdout, and stderr and leaves
the inherited working directory intact. A transfer or output failure terminates the child and
drains the transport tasks. See Microsoft's [anonymous pipe API](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.anonymouspipeserverstream?view=net-10.0).

The current native subset excludes source EH, and generated Windows/ORC unwind qualification remains
pending. The host is an execution boundary, not a restricted-token security sandbox.
