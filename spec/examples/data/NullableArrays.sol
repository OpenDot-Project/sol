// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: written nullability identity available; contextual array typing/flow remain incomplete.
module demonstrations.arrays

func elements(): []String? = ["Sol", null]
func sameElements(): [](String?) = elements()
func missingArray(): ([]String)? = null
func bothNullable(): ([]String?)? = null

func count(values: ([]String?)?): int {
    if values == null { return 0 }
    var result = 0
    for val value in values {
        if value != null { result += 1 }
    }
    result
}

// []String? and [](String?) have identical element-nullable identities.
// Parentheses are required to put ? on the array reference itself.
// The local checks require later smart-cast and nullability flow verification.
