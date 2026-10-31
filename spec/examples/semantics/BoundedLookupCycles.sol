// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; inheritance-cycle diagnostics and traversal limits need semantic coverage.
module examples.semantics.bounded_lookup_cycles

interface Root<T> { func value(): T }
interface Left<T>: Root<T> { }
interface Right<T>: Root<T> { }

class Shared<T>: Left<T>, Right<T> {
    override func value(): T = default<T>()
}

func <T> read(source: Root<T>): T = source.value()

func repeatedPath(source: Shared<String>): String {
    return read(source)
}

// The two interface paths revisit one Root<String> identity. The resolver visits it once and
// returns the same method candidate as a direct path. It does not recurse forever through repeated
// interface declarations or report the shared member twice.
//
// A malformed inheritance cycle is rejected by declaration analysis. Even in recovery, the
// constructed identity set terminates traversal. If a generated or adversarial graph exceeds the
// depth/node budget, the resolver returns a resource status with no partial candidate set claimed
// complete.
