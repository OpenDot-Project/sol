// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: type normalization implemented; smart casts/operator lowering remain planned.
module source.nulls.rules

func arrayNullability(
    nullableElements: []String?,
    sameElements: [](String?),
    nullableArray: ([]String)?,
    both: ([]String?)?,
    nested: [][]String?,
): () {}

func requireName(value: String?): String {
    value ?: fail("A name is required")
}

func fail(message: String): ! { throw RuntimeException(message) }
func byteCount(value: String?): int = value?.byteLength ?: 0
func castText(value: Object): String? = value as? String
func assertText(value: String?): String = value!!

func guarded(value: Object?): String {
    if value is String {
        return value
    }
    "unknown"
}

// Array prefixes contain the complete element type. []String? means nullable
// elements in a non-null array; it never means an outer nullable array reference.
// Repeated nullable wrapping normalizes to one layer, including specialization T?.
// ?. skips member/call arguments on null; ?: evaluates its right side only on null.
// !! checks null; as checks a conversion; as? returns null on failed checked casting.
// Stable local val values can be smart-cast. Mutable/open/custom properties cannot
// be assumed unchanged between a check and a read. Full flow proof is still required.
