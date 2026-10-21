# Standard library implementation

Sol's standard library belongs to this repository. Compiler-owned native declarations and ordinary
Sol function bodies live under `src/stdlib/std`; native services live in `src/native/runtime`.
User declarations cannot become native intrinsics merely by matching a file path or spelling.

The current implementation is a restricted bootstrap, separate from the complete source contracts
in `spec`. Planned signatures are not shipped implementations.

| Module | Implemented native subset |
|---|---|
| `std.core` | Object/String/Throwable nominal roots; String scalar length, concatenation, content equality, explicit-range substring, explicit-start indexOf, ordinal comparison/hash; single-level scalar/String arrays; `Gc.collect()`. Native Throwable descriptors/carrier/handles exist; source construction/EH and complete Object/String APIs remain pending. |
| `std.stdio` | `print`, `println`, `error`, `errorln` for supported scalar and String arguments; argument evaluation order, no implicit separator, LF newline, checked IOException effect. Reader/Writer/file/stream APIs remain pending. |
| `std.math` | `abs`, `min`, `max` for sbyte/short/int/long/byte/uint/ulong, implemented as ordinary Sol bodies with exact types and checked integer arithmetic. Floating/extended math and other numeric families remain pending. |

Strings use valid immutable UTF-8 and count Unicode scalars rather than UTF-16 units. Substrings copy
their selected bytes, preserving embedded NUL. Comparison does not normalize Unicode or depend on
locale. `hashCode` hashes bytes consistently within this runtime; it is not a cryptographic hash.
Arrays are fixed length and traced by descriptor/element shape; reference writes use a GC barrier.
Boxing, general object dispatch, generic collections, nested value layouts and ref-like spans need
additional verified source/FIR/backend support.

The remaining module families are not implemented: `std.collections`, `std.hash`, `std.order`,
`std.fmt`, `std.text.unicode`, `std.text.json`, `std.time`, `std.date`, `std.crypto`, `std.net`,
`std.net.http`, `std.net.ssl`, `std.net.tls`, `std.net.socket`, `std.net.rpc`, `std.net.uri`,
`std.stdio.file`, `std.threading`, `std.text.regex`, `std.cmd.process`, `std.cmd.argv`,
`std.reflect`, `std.text.smtp`, `std.archive`, `std.archive.tar`, `std.archive.zip`,
`std.archive.compression.zstd`, and `std.archive.compression.xz`. Their implementation must include
ownership, errors, resource limits, source-call qualification and integration tests. Empty module
stubs or backend imports are not counted as completed library support.

Runtime/GC limits and process ownership are documented in the
[runtime inventory](yellow-tea/current-implementation.md); library artifact restrictions are in the
[artifact guide](library-artifacts.md).
