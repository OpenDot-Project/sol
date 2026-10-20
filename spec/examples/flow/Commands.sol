// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: enum/switch syntax and pattern scopes; exhaustiveness/layout/dispatch are planned.
module demonstrations.commands

enum Command { Stop, Move(x: int, y: int), Rename(value: String) }

func description(command: Command): String {
    switch command {
        case Command.Stop: return "stop"
        case Command.Move(x, y):
            if x == y { return "diagonal" }
            return "move"
        case Command.Rename(value): return value
    }
}

// Every variant is covered; there is no implicit fallthrough. x/y/value belong
// to their own case scopes. Payload reference descriptors follow only the active tag.
