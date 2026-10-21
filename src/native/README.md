# Sol native implementation

This tree owns the Windows launcher, the LLVM bridge, and the native SolRT bootstrap. All projects
use MSBuild and C++20. The implementation namespace is `OpenDot::Sol`; it is separate from Sol
source-language naming.

## LLVM bridge

`llvm-bridge/Compiler.cpp` is the C++/CLI adapter implementing the managed `ILlvmCompiler` contract.
It translates managed UTF-8 input and native failures at the boundary. Instruction selection and
Sol semantics remain in C# lowering. The adapter requires .NET 10 in the compiler process.

`llvm-bridge/NativeCompiler.cpp` is compiled without `/clr`. It owns the LLVM context, module,
memory buffers, and target machine with deterministic session cleanup. LLVM 23.1.3 verifies the
input, runs the optimization pipeline, verifies again, and emits AMD64 COFF. This translation unit
uses the exact LLVM C interface; native LLVM types do not enter managed metadata.

## SolRT bootstrap

`runtime/SolRT.h` declares the provisional ABI0 headers and C entry points. Header, String, and
String-array fixed prefixes occupy 16, 24, and 32 bytes respectively. Static assertions and the
ASan harness check these boundaries. The descriptor layout is provisional, not a stable collectible ABI.

`runtime/SolRT.cpp` owns UTF-8 argument storage from entry until shutdown. Literals use static
storage and publish their descriptor atomically. Output helpers use Windows handles; scalar
formatting does not introduce a CLR dependency. Dynamic and static libraries expose the same C
entry points. The generated program imports SolRT.dll by default; static SolRT linking removes
that import.

This arena does not implement Yellow Tea. User allocation, root frames, barriers, exception
unwinding, and general managed-object layouts remain outside the qualified native subset. The
native failure helper reports an unhandled bootstrap error and exits; it does not allocate or
throw a full Sol exception object.

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
