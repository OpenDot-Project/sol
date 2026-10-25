// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: virtual dispatch is checked against the inherited source contract.
module spec.effects.dispatchcontract

open class DiskFailure: Exception {}
class MissingFailure: DiskFailure {}
class WriteFailure: Exception {}

open class Reader {
    open func read(): String throws DiskFailure { "base" }
    open func reset(): () {}
}

class CachedReader: Reader {
    override func read(): String { "cached" }
}

class LocalReader: Reader {
    override func read(): String throws MissingFailure { "local" }
}

class WideningReader: Reader {
    // Expected: override cannot add an unrelated checked effect.
    override func read(): String throws WriteFailure { "invalid" }
}

class WideningReset: Reader {
    // Expected: an override cannot add a checked budget to a base pure contract.
    override func reset(): () throws DiskFailure {}
}

func throughBaseContract(reader: Reader): String throws DiskFailure {
    // The selected slot's source contract governs even when runtime dispatch is narrower.
    reader.read()
}

func directNarrowImplementation(): String throws MissingFailure {
    LocalReader().read()
}

func catchBaseCoversRuntimeImplementation(reader: Reader): String {
    try {
        reader.read()
    } catch (failure: DiskFailure) {
        "unavailable"
    }
}

interface Store {
    func load(): String throws DiskFailure
}

class MemoryStore: Store {
    override func load(): String { "memory" }
}

func interfaceCallKeepsDeclaredBudget(store: Store): String throws DiskFailure {
    store.load()
}

func overrideEffectsDoNotFormAnOverload(reader: Reader): String throws DiskFailure {
    reader.read()
}

func genericDispatch<E: DiskFailure>(reader: Reader, fallback: String): String throws E, DiskFailure {
    if fallback.length == 0 {
        reader.read()
    }
    fallback
}

func callbackCapturesStaticContract(reader: Reader): () -> String throws DiskFailure {
    { reader.read() }
}

func callbackInvocationIsChecked(action: () -> String throws DiskFailure): String throws DiskFailure {
    action()
}

func wideningCallbackBudget(action: () -> String throws MissingFailure): String throws DiskFailure {
    action()
}

// Overrides may narrow compatible checked effects, never widen the base slot contract. Calls
// bind against the static API identity and preserve effects through interfaces and function
// values. Implemented today: parsing/tree construction; hierarchy-aware effect checks are planned.
