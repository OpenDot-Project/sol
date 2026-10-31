// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: parser example for local-function escape and source-environment contracts; no FIR or
// native local-function value lowering is implied.
module spec.local_function_values

func directCallsRemainLocal(seed: int): int {
    var current = seed
    func next(): int {
        current += 1
        current
    }
    next()
}

func returnLocalFunction(seed: int): () -> int {
    func read(): int = seed
    return read
}

func passAndAggregateLocalFunction(seed: int) {
    func read(): int = seed
    publish(read)
    val callbacks = [read]
}

func aliasUseIsUnmodeled(seed: int) {
    func read(): int = seed
    val alias = read
    alias()
}

func nestedCallableRetainsLocalFunction(seed: int) {
    func read(): int = seed
    val callback = { read() }
    callback()
}

class Holder {
    var callback: () -> int
}

func publishIntoField(holder: Holder, seed: int) {
    func read(): int = seed
    holder.callback = read
}

ref struct Buffer { val length: int }

func escapingBorrowedLocal(ref value: int): () -> int {
    func read(): int = value
    return read
}

func escapingRefLikeLocal(buffer: Buffer): () -> int {
    func read(): int = buffer.length
    return read
}

class ReceiverOwner(val value: int) {
    func returnReceiverUsingLocal(): () -> int {
        func read(): int = value
        return read
    }
}

// `LocalFunctionEscapeAnalysis` identifies each declaration by its resolved `LocalSymbol`.
// Direct invocation is distinct from using a function as a value. Return, call-argument,
// aggregate, and field stores are publication-capable; a local alias or nested callable remains
// Unknown because its value flow is not modeled. The source-only environment planner preserves
// resolved local captures as ordered field candidates. Escaping ref/out or ref-like captures and
// receiver-dependent functions remain Unknown; no closure allocation, ABI, or GC layout is proven.
