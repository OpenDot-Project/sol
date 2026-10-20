// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: conventional source-layout example; member typing and execution remain planned.
// Source root: spec/examples/layout/src. Source set: Greeter.sol + Main.sol.
module myproject.example

class Greeter(private val name: String) {
    func message(): String = "Hello, " + this.name
}

// The lowercase myproject/example path follows the declared module by convention.
// Greeter.sol uses the main declaration's PascalCase name. A different explicit path
// would remain valid: the module declaration, not the directory, determines identity.
