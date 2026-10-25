// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative catch fixture. Most-specific ordering and effect subtraction are semantic rules.
module spec.effects.hierarchyc

open class IoFailure: Exception {}
class FileFailure: IoFailure {}
class JsonFailure: Exception {}
class BoundsFailure: RuntimeException {}

func readFile(): String throws FileFailure { "" }
func parseJson(text: String): int throws JsonFailure { text.length }

func catchSpecificBeforeBase(): int {
    try {
        readFile()
        parseJson("")
    } catch (file: FileFailure) {
        1
    } catch (io: IoFailure) {
        2
    } catch (json: JsonFailure) {
        3
    }
}

func broadCatchBeforeSpecific(): int {
    try {
        readFile()
    } catch (io: IoFailure) {
        1
    } catch (file: FileFailure) {
        // Expected: FileFailure is already handled by IoFailure.
        2
    }
}

func catchDoesNotOverclaimCoverage(): int throws JsonFailure {
    try {
        readFile()
        parseJson("")
    } catch (io: IoFailure) {
        0
    }
}

func catchSiblingDoesNotCoverFile(): int throws FileFailure {
    try {
        parseJson("")
        readFile()
    } catch (json: JsonFailure) {
        0
    }
}

func runtimeCatchDoesNotCoverIo(): int throws IoFailure {
    try {
        readFile()
    } catch (bounds: BoundsFailure) {
        0
    }
}

func catchRuntimeExceptionStillHandlesRuntime(): int {
    try {
        throw BoundsFailure()
    } catch (runtime: RuntimeException) {
        0
    }
}

func broadThrowableCatchCoversEveryCheckedType(): int {
    try {
        readFile()
    } catch (failure: Throwable) {
        1
    } catch (file: FileFailure) {
        // Expected: Throwable already covers this entire type domain.
        2
    }
}

func rethrowStaticCatchType(): ! throws IoFailure {
    try {
        readFile()
    } catch (failure: IoFailure) {
        throw failure
    }
}

func rethrowRequiresBudget(): ! {
    try {
        readFile()
    } catch (failure: IoFailure) {
        // Expected: the catch binding's static IoFailure escapes.
        throw failure
    }
}

func narrowCatchValue(error: Throwable): ! {
    if error is RuntimeException {
        throw error
    }
    throw error
}

func narrowerCatchValue(error: Throwable): ! throws Throwable {
    if error is FileFailure {
        throw error
    }
    throw error
}

// Catch reachability is based on possible thrown types and subtype relationships. A catch does
// not become reachable merely because its syntax is valid; a handler with no possible matching
// input is diagnosed according to the closed effect model. The source front end may retain an
// explicit deferred result until all call effects and hierarchy edges are resolved.
