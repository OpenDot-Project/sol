// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax/semantic contract fixture; complete protected receiver validation remains in progress.
module examples.semantics.protected_callable_receiver_matrix

open class Vehicle<T> {
    protected func inspect(value: T): String = "vehicle"
    protected val serial: String
}

open class Car: Vehicle<String> {
    override func inspect(value: String): String = value

    func selfCall(): String = this.inspect("self")

    func sameDerivedReceiver(other: Car): String = other.inspect("other")

    func subtypeReceiver(other: ElectricCar): String = other.inspect("electric")

    func superCall(): String = super.inspect("base")

    func sameDerivedProperty(other: Car): String = other.serial

    func wrongBaseReceiver(other: Vehicle<String>): String {
        // Expected diagnostic: a base-typed receiver does not satisfy the protected receiver rule.
        return other.inspect("base")
    }

    func wrongBaseProperty(other: Vehicle<String>): String {
        // Expected diagnostic: property access uses the same static receiver rule.
        return other.serial
    }
}

class ElectricCar: Car {
    override func inspect(value: String): String = value
}

func outsideCall(value: Car): String {
    // Expected diagnostic: the lexical caller is not in Car/Vehicle's protected hierarchy.
    return value.inspect("outside")
}

func outsideProperty(value: Car): String {
    // Expected diagnostic: protected property cannot be read from a top-level function.
    return value.serial
}

func nullableSafeCall(value: Car?): String? {
    // Receiver access is checked on the non-null Car path; null does not grant additional access.
    return value?.inspect("safe")
}

func narrowedCall(value: Vehicle<String>): String {
    if (value is Car) {
        // The narrowed static receiver can satisfy the rule only within the valid flow region.
        return value.inspect("narrowed")
    }
    return "not a car"
}

class ShadowingBase {
    public func name(): String = "public"
}

open class ProtectedShadow: ShadowingBase {
    protected override func name(): String = "protected"
}

func noPublicFallback(value: ProtectedShadow): String {
    // Expected access diagnostic: inaccessible nearest override does not reveal the base method.
    return value.name()
}

// Access facts are independent:
//
// - `Vehicle<String>` is the constructed declaration owner for the inherited generic contract;
// - `Car`/`ElectricCar` is the source receiver type for protected access;
// - `Car`/`Vehicle` membership is the lexical caller check;
// - `super` is an explicit allowed base route;
// - safe access handles null evaluation but does not alter protected permission;
// - virtual override selection occurs only after source access succeeds.
//
// The compiler does not query runtime object class, CLR reflection, vtable layout, or source file
// order to approve a protected call. Getter/setter visibility and checked effects are checked
// separately for property operations.
