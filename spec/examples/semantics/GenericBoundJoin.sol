// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; unique common-bound solving is not yet complete.
module examples.semantics.generic_bound_join

interface Labelled { func label(): String }
interface Identified { func id(): int }

open class Record: Labelled {
    open func label(): String = "record"
}

class UserRecord: Record, Identified {
    override func label(): String = "user"
    func id(): int = 1
}

class ServiceRecord: Record {
    override func label(): String = "service"
}

class LabelledOnly: Labelled {
    override func label(): String = "left"
}

class IdentifiedOnly: Identified {
    override func id(): int = 2
}

func <T> choose(left: T, right: T): T = left

func recordJoin(): Record {
    return choose(UserRecord(), ServiceRecord())
}

func interfaceJoin(): Labelled {
    return choose(UserRecord(), ServiceRecord())
}

func unrelatedInterfaces(): () {
    // Expected result: inference must not pick Labelled or Identified without unique best bound.
    val ambiguous = choose(LabelledOnly(), IdentifiedOnly())
}

func orderedInputs(): Record {
    return choose(ServiceRecord(), UserRecord())
}

func explicitJoin(): Record {
    return choose<Record>(UserRecord(), ServiceRecord())
}

// A closed class common type and one uniquely best interface are valid when the source conversion
// graph proves them. Several incomparable interfaces do not have a unique minimal candidate.
// Reversing inputs cannot change the result. Explicit type arguments supply the candidate directly
// but do not bypass the conversions required from each argument.
//
// This example intentionally does not use numeric widening. A typed int and long pair cannot
// acquire a new common type solely from machine representation; source conversion rules still
// apply after inference.
