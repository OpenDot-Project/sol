// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: typed catches subtract only covered effects on protected edges.
module spec.effects.handlerpathsubtraction

open class IoFailure: Exception {}
class ReadFailure: IoFailure {}
class WriteFailure: IoFailure {}
class ParseFailure: Exception {}

func read(): int throws ReadFailure { 1 }
func write(): () throws WriteFailure {}
func parse(): int throws ParseFailure { 2 }

func catchOneSubtypeKeepSibling(flag: bool): int throws WriteFailure, ParseFailure {
    try {
        if flag {
            read()
        } else {
            write()
        }
        parse()
    } catch (failure: ReadFailure) {
        0
    }
}

func subtypeCatchDoesNotEraseBaseFamily(): int throws WriteFailure {
    try {
        read()
        write()
    } catch (failure: ReadFailure) {
        0
    }
}

func baseCatchCoversBothIoSubtypes(flag: bool): int {
    try {
        if flag {
            read()
        } else {
            write()
        }
    } catch (failure: IoFailure) {
        return -1
    }
    1
}

func catchBodyEffectEscapes(): int throws ParseFailure {
    try {
        read()
    } catch (failure: ReadFailure) {
        parse()
    }
}

func missingCatchBodyBudget(): int {
    try {
        read()
    } catch (failure: ReadFailure) {
        // Expected: the handler's ParseFailure is not handled by its sibling or same try.
        parse()
    }
}

func outerTryCanCatchHandlerFailure(): int {
    try {
        try {
            read()
        } catch (failure: ReadFailure) {
            parse()
        }
    } catch (failure: ParseFailure) {
        return 0
    }
    1
}

func catchesDoNotHandleFinallyFailure(): int throws CleanupFailure {
    try {
        read()
    } catch (failure: ReadFailure) {
        return 0
    } catch (failure: CleanupFailure) {
        // Expected: handler selection precedes finally execution.
        return 1
    } finally {
        cleanup()
    }
}

class CleanupFailure: Exception {}
func cleanup(): () throws CleanupFailure { throw CleanupFailure() }

func checkedStaticTypeSurvivesRuntimeSubtype(error: IoFailure): ! throws IoFailure {
    throw error
}

func genericEffectSubstitutionKeepsConcreteType(error: ReadFailure): ! throws ReadFailure {
    rethrow(error)
}

func rethrow<E: Throwable>(error: E): ! throws E { throw error }

func catchOrderMustBeMostSpecificFirst(error: IoFailure): int {
    try {
        throw error
    } catch (failure: ReadFailure) {
        return 1
    } catch (failure: IoFailure) {
        return 2
    }
}

// Catch coverage uses resolved subtype identity. A handler's own effects leave the handler and
// can be caught only by an enclosing try. Finally is applied after handler selection. Implemented
// today: parsing; planned: effect subtraction, reachability diagnostics, and generic substitution.
