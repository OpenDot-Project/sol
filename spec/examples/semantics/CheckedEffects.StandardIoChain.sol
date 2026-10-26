// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative standard-I/O fixture. IOException is a checked std.stdio type.
module spec.effects.standardio

import std.stdio
import std.stdio.file

func readText(path: String): String throws IOException {
    std.stdio.file.readText(path)
}

func reportLength(path: String): int throws std.stdio.IOException {
    val text = readText(path)
    std.stdio.println(text.length)
    text.length
}

func catchIoAtBoundary(path: String): int {
    try {
        reportLength(path)
    } catch (error: IOException) {
        std.stdio.errorln(error.message)
        -1
    }
}

func catchWrongSibling(path: String): int throws IOException {
    try {
        readText(path)
    } catch (error: RuntimeException) {
        // Expected: runtime failures do not cover checked IOException.
        -1
    }
}

func catchBaseException(path: String): int {
    try {
        readText(path)
    } catch (error: Exception) {
        0
    }
}

func writeAndClose(path: String): () throws IOException {
    val writer = std.stdio.file.openWrite(path)
    defer { writer.close() }
    writer.write("done")
}

func closeEffectCannotBeDropped(path: String): () {
    val writer = std.stdio.file.openWrite(path)
    defer { writer.close() }
    writer.write("done")
}

object Main {
    func main(args: []String): () throws IOException {
        if args.length > 0 {
            std.stdio.println(readText(args[0]))
        }
    }
}

// The canonical hierarchy is Throwable -> Exception -> std.stdio.IOException. std.core remains
// implicit; explicit std.stdio imports expose output functions and the exact IOException owner.
// RuntimeException descendants are unchecked, while I/O exceptions remain checked unless caught
// or covered by a declared supertype.
