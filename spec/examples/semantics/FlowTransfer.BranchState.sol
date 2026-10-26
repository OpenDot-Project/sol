// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative flow fixture. The whole file is syntax-checked; semantic labels are expected behavior.
module spec.flow.branchstate

class Vehicle {
    val wheels: int = 4
}

class Bicycle: Vehicle {
    val pedals: int = 2
}

class MutableSlot {
    var vehicle: Vehicle? = null
}

func assignedOnBothArms(flag: bool): int {
    var value: int
    if flag {
        value = 10
    } else {
        value = 20
    }
    // Expected: valid. Every reachable predecessor assigned value.
    value
}

func assignedOnOneArm(flag: bool): int {
    var value: int
    if flag {
        value = 10
    }
    // Expected: unassigned-local diagnostic. The false edge reaches the read.
    value
}

func terminatingArm(flag: bool): int {
    var value: int
    if flag {
        return 1
    }
    value = 2
    // Expected: valid. The only continuing arm assigned value.
    value
}

func throwArmDoesNotJoinAsNormal(flag: bool): int {
    var value: int
    if flag {
        throw Failure()
    } else {
        value = 3
    }
    // Expected: valid when Failure is a declared checked effect or unchecked type.
    value
}

class Failure: RuntimeException {}

func nestedJoin(flag: bool, outer: bool): int {
    var value: int
    if outer {
        if flag {
            value = 1
        } else {
            value = 2
        }
    } else {
        value = 3
    }
    // Expected: valid. All three reachable leaf paths assign the same storage identity.
    value
}

func nestedIncompleteJoin(flag: bool, outer: bool): int {
    var value: int
    if outer {
        if flag {
            value = 1
        }
    } else {
        value = 3
    }
    // Expected: unassigned-local diagnostic on the outer-true/inner-false path.
    value
}

func nonNullOnContinuingEdge(vehicle: Vehicle?): int {
    if vehicle == null {
        return 0
    }
    // Expected: valid. Return removes the null edge from the join.
    vehicle.wheels
}

func nonNullAfterBothArms(vehicle: Vehicle?, flag: bool): int {
    if flag {
        if vehicle == null {
            return 0
        }
    } else {
        if vehicle == null {
            return 1
        }
    }
    // Expected: valid. Both normal arms prove the same local is non-null.
    vehicle.wheels
}

func typeFactOnOneArm(vehicle: Vehicle, flag: bool): int {
    if flag {
        if vehicle is Bicycle {
            return vehicle.pedals
        }
    }
    // Expected: the type fact is not available here; only an ordinary base member is read.
    vehicle.wheels
}

func writeKillsEarlierFact(vehicle: Vehicle?): int {
    var current = vehicle
    if current == null {
        return 0
    }
    current = null
    // Expected: nullable-receiver diagnostic. Assignment kills the prior fact.
    current.wheels
}

func branchWriteKillsOnlyItsBranch(vehicle: Vehicle?, flag: bool): int {
    var current = vehicle
    if flag {
        current = null
    } else {
        if current == null {
            return 0
        }
        current.wheels
    }
    // Expected: no non-null proof after the join because one arm writes null.
    current.wheels
}

func localCopyCanBeProved(vehicle: Vehicle?): int {
    val stable = vehicle
    if stable == null {
        return 0
    }
    // Expected: stable is non-null on the remaining edge.
    stable.wheels
}

func propertyIsNotStable(slot: MutableSlot): int {
    if slot.vehicle == null {
        return 0
    }
    // Expected: a second property read is not assumed to equal the first getter result.
    slot.vehicle.wheels
}

func booleanJoinDoesNotExportShortCircuitFact(vehicle: Vehicle?): int {
    val present = vehicle != null && vehicle.wheels > 0
    // Expected: the right operand may use vehicle safely; present does not refine vehicle.
    vehicle.wheels
}

// Invalid cases retain a compact source span at the failing read. Diagnostics are deterministic
// by source order and carry the source snapshot that owns this module.
