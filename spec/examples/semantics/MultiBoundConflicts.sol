// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; exact same-name multi-bound conflict behavior remains incomplete.
module examples.semantics.multi_bound_conflicts

interface FirstName { func name(): String }
interface SecondName { func name(): String }

class Named: FirstName, SecondName {
    override func name(): String = "named"
}

func <T> read(value: T): String where T: FirstName, T: SecondName {
    return value.name()
}

func useSharedDeclaration(value: Named): String {
    return read(value)
}

interface FirstId { func id(): int }
interface SecondId { func id(): int }

func <T> readId(value: T): int where T: FirstId, T: SecondId {
    // Expected ambiguity until one declaration identity is shared or an explicit resolution exists.
    return value.id()
}

// One concrete class implementing two contracts is not sufficient proof that the generic bounds
// themselves identify the same declaration. The analysis compares constructed declaration
// identities and valid override relationships, rather than deduplicating by name/signature text.
