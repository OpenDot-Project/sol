# Compiler/runtime ABI

This page specifies the first planned native GC ABI. No shipped header, generated root plan, or
native implementation currently establishes compatibility with it. The frontend's existing managed
APIs do not constitute this ABI.

## Descriptor responsibilities

A type descriptor is immutable process-lifetime metadata. It includes the object or variable-layout
size rule, alignment, type identity, kind, base/interface information, exact scan shape, dispatch
metadata, generic identity, field-layout version, and exception/type metadata ownership.

Scan shapes describe reference offsets and nested value layouts. They distinguish active enum payloads,
nullable-value tags, inline arrays, and reference-containing structs. Inactive payload bytes are not
interpreted as references. A source integer containing a pointer-like bit pattern is never scanned.

| Source representation | Tracing responsibility |
|---|---|
| Class, object, String, managed array | Trace the object reference and then its descriptor. |
| Interface value | Preserve the object identity reference; descriptor lookup supplies dispatch. |
| Function value | Code pointer plus managed closure-environment reference; only the latter is a managed edge. |
| Tuple/struct/inline array | Scan reference fields recursively at exact offsets. |
| Nullable reference | Null pointer or an ordinary object reference. |
| Nullable value | Read the presence tag before tracing the contained payload. |
| Payload enum | Trace only the active variant's reference-containing payload. |
| Interior location | Root the base object and retain an offset, not a standalone interior pointer. |

The compiler validates imported library target, compiler identity, runtime ABI, and LLVM identity
before native linking. A descriptor layout mismatch cannot fall back to conservative scanning.

## Planned entry points

The following names describe responsibilities. They are a design inventory, not declarations of
callable exports in the current release:

| Native entry | Responsibility |
|---|---|
| `sol_gc_alloc` | Allocate typed, aligned storage under the current allocation epoch. |
| `sol_gc_push_frame` | Register a complete frame descriptor and root-slot region. |
| `sol_gc_pop_frame` | Remove the matching frame exactly once. |
| `sol_gc_store_ref` | Protect and publish a managed reference store. |
| `sol_gc_poll` | Publish live roots and participate in a pending handshake. |
| `sol_gc_enter_native` | Publish roots and enter NativeSafe. |
| `sol_gc_leave_native` | Rejoin the managed epoch agreement before managed access. |
| `sol_gc_create_handle` | Create a strong native owner for a managed reference. |
| `sol_gc_resolve_handle` | Obtain a rooted reference under handle-lifetime synchronization. |
| `sol_gc_release_handle` | End that native ownership without racing active users. |

Native ABI widths and symbol spelling do not add builtin types to Sol or change Sol public API naming.
Allocation sizes use checked native-width arithmetic; source array counts remain `int`.

## Lowering boundary

SolRT is dynamically linked into a generated application by default. `solc --static-solrt-lib`
selects static SolRT linkage instead. This selects runtime linkage independently of `--artifact-type`:
requesting a `staticlib` artifact does not itself select static SolRT, and selecting static SolRT
does not turn a binary artifact into a library.

The CLI now records this selection, but native linking is not implemented. No runtime DLL, static
runtime library, or working native link command is supplied by the frontend release. Neither mode
changes the collector's root/barrier/EH contract or replaces the tools' separately packaged .NET runtime.

Static embedding must not create two independent collector/handle registries that exchange managed
references in one process. Cross-library sharing and packaging require an explicit qualified runtime
ownership contract; static selection alone does not establish that ABI.

The compiler's C# implementation owns semantic decisions, root liveness, barrier insertion, and
Windows exception lowering. A future thin C++/CLI LLVM bridge performs native-library operations;
it does not contain a second type checker or collector policy.

The frontend pipeline remains CST, PSI, AST, FIR, and LLVM IR. Root planning belongs to verified
compiler information and code generation; no HIR or MIR layer is introduced by this design.
AOT and ORC must use the same descriptors, frame ownership, barriers, and runtime layout.

## Exceptions and cleanup

The planned Windows exception carrier owns a strong runtime handle to the Sol Throwable. A raw
object address inside a foreign exception object is insufficient. Pending-exception ownership
transfers to a catch-local root exactly once, and rethrow preserves Throwable identity.

Generated exception and cleanup edges keep root frames alive through `finally` and `defer`. Cleanup
must not pop a frame twice or pop it before its live cleanup references are no longer needed.
A secondary cleanup failure follows the runtime's bounded suppressed-exception policy rather than
discarding the primary failure.

Foreign CLR, C++, and embedded-runtime exception mechanisms cannot propagate unchecked through
generated Sol frames. Boundary adapters translate to explicit status or Sol exception ownership
under their own contract. Collector corruption follows controlled abort, not arbitrary recovery
through a user catch clause.

ORC code must register its unwind information before execution. The initial code-lifetime policy
keeps generated code for process lifetime; unloading JIT code with active frames is outside scope.

## Native I/O and callbacks

Retained buffers need a strong handle until completion or cancellation actually releases native
ownership. Starting cancellation alone does not prove that the native operation has stopped using
the buffer. Callback registrations similarly own their environment and code lifetime.

NativeSafe scalar-buffer access cannot mutate managed reference edges. A callback must complete
thread attachment and managed reentry before using objects, even when their addresses never move.
