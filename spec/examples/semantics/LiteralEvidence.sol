// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; exact literal contextual inference remains incomplete.
module examples.semantics.literal_evidence

func <T> identity(value: T): T = value
func pick(value: int): String = "int"
func pick(value: long): String = "long"

func defaultInteger(): int = identity(12)

func expectedUnsigned(): ulong {
    return identity(18446744073709551615)
}

func overloadLiteral(): String = pick(4)

func typedVariable(value: byte): byte {
    return identity(value)
}

func overflowTarget(): () {
    // Expected diagnostic: the source magnitude cannot be represented as the target type.
    val invalid: byte = identity(300)
}

// A literal keeps exact token data through candidate comparison. Its default int/double type is
// one inference source, and a local expected context may select another representable type. A typed
// variable contributes only its already resolved type and never receives literal widening rules.
// Binary128 conversion stays deferred until the exact rounding rule is implemented.
