# Sol native implementation

This tree owns the Windows launcher, LLVM C++ component/bridge, ORC execution host, and native SolRT. All projects
use MSBuild and C++20. The implementation namespace is `OpenDot::Sol`; it is separate from Sol
source-language naming.

## LLVM bridge

`llvm-bridge/Compiler.cpp` is the C++/CLI adapter implementing the managed `ILlvmCompiler` contract.
It translates immediate builder operations, opaque handles and native failures at the boundary. Instruction selection and
Sol semantics remain in C# lowering. The adapter requires .NET 10 in the compiler process.

`llvm-native/Session.cpp` owns LLVM 23.1.3 C++ contexts, modules, IRBuilder operations, and target
machines. Handles carry session identities and reject wrong-kind, foreign, cross-thread and disposed
access. `Output.cpp` verifies modules, optimizes a clone and emits COFF or writes binary bitcode.
`Orc.cpp` reads bitcode and owns LLJIT inside the native guest. There is no textual assembly parser
in the compilation path; LLVM's printer is used only for explicit inspection.

The native DLL uses the SDK's static CRT; the thin mixed-mode adapter uses the dynamic CRT. An opaque
C ABI keeps allocation and destruction in the owning component. Pinned compression/XML libraries
are built by `eng/install-llvm-native-deps.ps1`. First-party projects use MSBuild and `/W4 /WX`.

## SolRT bootstrap

`runtime/SolRT.h` declares the provisional ABI0 headers and C entry points. Header, String, and
String-array fixed prefixes occupy 16, 24, and 32 bytes respectively. Static assertions and the
ASan harness check these boundaries. The descriptor layout is provisional, not a stable collectible ABI.

`runtime/SolRT.cpp` roots UTF-8 arguments with an entry handle until shutdown. Literals use static
storage and publish their descriptor atomically. Output helpers use Windows handles; scalar
formatting does not introduce a CLR dependency. Dynamic and static libraries expose the same C
entry points. The generated program imports SolRT.dll by default; static SolRT linking removes
that import.

`Gc.cpp` owns thread registration, frames, the insertion barrier, allocation records and handles.
`GcWorker.cpp` implements root handshakes and concurrent mark/sweep; sweep uses a frozen allocation
boundary so new allocations cannot indefinitely extend a cycle. `GcConfig.cpp` validates heap limits.
`Core.cpp` implements UTF-8 String services and rooted native Throwable carriers/suppression;
`Arrays.cpp` supplies typed scalar/reference arrays. The allocator is individually aligned storage,
not the planned span allocator. Source class construction and EH lowering remain incomplete.

## ORC guest process

`exec-host/Main.cpp` supplies the guest runtime and symbol allowlist to the native LLVM component.
It accepts binary bitcode; the component verifies its target and executes the entry with LLJIT. It is plain C++20 and
does not import CLR or C++/CLI hosting. The C# compiler launches it as a separate child: host .NET
GC and guest Yellow Tea do not share a process or object references. This is not an OS sandbox.
The current source subset does not establish generated Windows/ORC exception-unwind qualification.

## Launcher

`launcher/main.cpp` locates the packaged private .NET runtime and managed compiler payload. It
preserves arguments and process handles and returns the child status. The launcher is native,
while the compiler it starts remains a .NET application.

## Validation tools

The native tooling solution is `Sol.Native.sln`. Repository helpers invoke `jb cleanupcode` with
the formatting-only profile and `jb inspectcode` for native and C++/CLI inspections. The root
`.clang-format` supplies Microsoft formatting and derived LF line endings.

MSVC Core Check uses `EnableCppCoreCheck=true`, `RunCodeAnalysis=true`, and
`CodeAnalysisRuleSet=CppCoreCheckRules.ruleset` on supported native translation units. The managed
adapter is excluded from native Core Check and remains in JetBrains inspection scope.

The root `Doxyfile` extracts this tree into HTML/XML. Documentation warnings fail validation;
extraction of a symbol does not certify ABI stability or complete API documentation. The separate
Windows Doxygen workflow validates these artifacts; DocFX owns the managed API documentation.
