// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative required-field fixture. Complete constructor verification remains a FIR obligation.
module spec.flow.requiredfields

class Config {
    val name: String
    val count: int

    constructor(name: String, count: int): super() {
        this.name = name
        this.count = count
    }
}

class MissingOne {
    val name: String
    val count: int

    constructor(name: String): super() {
        this.name = name
        // Expected: count is not assigned on normal constructor completion.
    }
}

class AssignOnEveryArm {
    val name: String

    constructor(flag: bool): super() {
        if flag {
            name = "left"
        } else {
            name = "right"
        }
    }
}

class MissingOnOneArm {
    val name: String

    constructor(flag: bool): super() {
        if flag {
            name = "left"
        } else {
            // Expected: normal completion leaves name unassigned.
        }
    }
}

class ThrowingConstructor {
    val name: String

    constructor(): super() {
        throw Failure()
    }
}

class Failure: RuntimeException {}

class AssignmentBeforeThrow {
    val name: String

    constructor(flag: bool): super() {
        name = "ready"
        if flag {
            throw Failure()
        }
    }
}

class DuplicateStoredWrite {
    val name: String

    constructor(): super() {
        name = "first"
        // Expected: a stored val cannot be assigned twice in one constructor path.
        name = "second"
    }
}

open class Base {
    val baseValue: int

    constructor(): super() {
        baseValue = 1
    }
}

class Derived: Base {
    val derivedValue: int

    constructor(): super() {
        derivedValue = baseValue + 1
    }
}

class UnsafeThisPublication {
    val required: String

    constructor(): super() {
        // Expected: this cannot escape before required fields are initialized.
        publish(this)
        required = "ready"
    }
}

func publish(value: Object): () {}

class ReadBeforeInitialization {
    val required: String

    constructor(): super() {
        // Expected: required is not initialized yet.
        consume(required)
        required = "ready"
    }
}

func consume(value: String): () {}

// Constructor state tracks required fields through normal branches, explicit returns, and
// throws. A throwing path need not publish an initialized object. A normal path must prove all
// required non-null fields are assigned exactly according to val/var rules before this escapes.
// Primary/base constructor order and initializer effects are separate obligations.
