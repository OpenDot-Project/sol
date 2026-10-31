// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; protected receiver restriction is a known implementation gap.
module examples.semantics.protected_receiver

open class Vehicle {
    protected func inspect(): String = "vehicle"
    protected val code: String
}

class Car: Vehicle {
    func inspectSelf(): String = this.inspect()

    func inspectCar(other: Car): String = other.inspect()

    func inspectVehicle(other: Vehicle): String {
        // Expected diagnostic: a derived body cannot use protected access via arbitrary base value.
        return other.inspect()
    }

    func readCarCode(other: Car): String = other.code

    func readVehicleCode(other: Vehicle): String {
        // Expected diagnostic for the same receiver restriction on a property getter.
        return other.code
    }
}

class ElectricCar: Car {
    override func inspect(): String = "electric"
}

func useVehicle(value: Vehicle): String {
    return value.inspect()
}

// Protected access is based on the declaration owner and static receiver type. The public
// `useVehicle` call is rejected because the member itself is protected. The implementation does
// not insert a runtime type check or cast to Car. Override dispatch is a separate decision from
// access permission.
//
// The receiver restriction applies consistently to method calls and property access. It must also
// be validated for ref/out, safe calls, and companion access. This file intentionally records the
// target behavior while the current body analyzer still needs the complete check.
