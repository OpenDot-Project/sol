// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.IO;

namespace OpenDot.Sol.Fuzz;

internal static class CorpusTargets
{
    internal static string[] Families(string target) => target switch
    {
        "frontend-utf8-v1" => Corpus.FrontendFamilies,
        "semantic-modules-v1" => ["binding-scopes", "default-arguments", "named-arguments", "varargs", "ref-out", "nullables",
            "generic-types", "partial-types", "module-cycles", "import-aliases", "duplicate-symbols", "overloads",
            "inheritance", "lambda-shapes", "exception-types", "properties", "destructuring", "enum-patterns",
            "malformed-declarations", "missing-imports"],
        "runtime-trace-v1" => ["ascii-short", "ascii-long", "unicode-bmp", "unicode-astral", "embedded-nul", "empty-strings",
            "concat-left-empty", "concat-right-empty", "substring-empty", "substring-whole", "substring-middle",
            "search-present", "search-missing", "hash-equality", "array-zero", "array-single", "array-many",
            "exception-cause", "exception-suppressed", "exception-carrier"],
        "backend-trace-v1" => ["add", "subtract", "multiply", "signed-divide", "unsigned-divide", "signed-remainder",
            "unsigned-remainder", "bit-and", "bit-or", "bit-xor", "shift-left", "shift-logical", "shift-arithmetic",
            "stack-load-store", "branch-phi", "function-call", "integer-casts", "floating-compare", "foreign-handle", "terminated-block"],
        "gc-trace-v1" => ["chain-first", "chain-last", "cycle-single", "cycle-none", "tree-root", "tree-leaves", "random-sparse",
            "random-dense", "self-cycles", "disconnected", "multi-roots", "root-drop", "strong-handle", "weak-handles",
            "two-clusters", "large-graph", "small-graph", "diamond", "cross-links", "root-rotation"],
        _ => throw new InvalidDataException("Unknown fuzz target."),
    };

    internal static bool IsNative(string target) => target is "runtime-trace-v1" or "backend-trace-v1" or "gc-trace-v1";
    internal static byte NativeCode(string target) => target switch
    {
        "runtime-trace-v1" => 1, "backend-trace-v1" => 2, "gc-trace-v1" => 3,
        _ => throw new InvalidDataException("Not a native fuzz target."),
    };
}
