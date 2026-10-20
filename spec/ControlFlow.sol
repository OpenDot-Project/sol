// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax/lexical pattern scopes; CFG/exhaustiveness/return proof remain planned.
module source.flow.rules

enum Command { Stop, Move(x: int, y: int), Rename(value: String) }

func loops(items: []int): int {
    var total = 0
    for var index: int = 0; index < items.length; index += 1 {
        total += items[index]
    }
    for val item in items { total += item }
    for total < 100 { total += 1 }
    for {
        if total == 100 { break }
        continue
    }
    total
}

func describe(command: Command): String {
    switch command {
        case Command.Stop: return "stop"
        case Command.Move(x, y): return "move"
        case Command.Rename(value): return value
    }
}

func cleanup(): () {
    defer { /* Release a scoped resource here on every exit. */ }
    val answer: int = try { 42 } finally { /* No value or outward jump. */ }
}

// Conditions are bool, not truthy numbers/objects. if/for require braced bodies.
// Switch scrutinee evaluates once. Cases have independent scopes and no fallthrough.
// Payload patterns introduce flat bindings. Exhaustive enum/sealed/bool switches can
// prove a return; open scalar/String switches need a default. Type patterns use case is T.
// The final normal expression returns from an annotated block function or lambda.
// Assignment/declaration/loop does not yield a value. A ! path has no normal continuation.
// Collection cursors use resolved iterator/moveNext/current and optional cleanup contracts.
