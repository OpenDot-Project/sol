// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax examples; execution and constant evaluation are not implemented.
module source.lexical.rules

// Source is UTF-8. ASCII identifiers are case-sensitive. Strings, runes and comments
// may contain Unicode. Indentation has no meaning; braces delimit blocks.
// LF and CRLF are accepted. Invalid UTF-8, invalid scalar escapes and source NUL
// outside strings are diagnosed. Block comments do not nest.

/// A declaration documentation comment.
func literals(): () {
    val decimal = 1_024
    val hexadecimal = 0xff
    val binary = 0b1010
    val octal = 0o755
    val fraction = 1.25
    val scientific = 2.5e-3
    val enabled = true
    val text = "UTF-8: Sol, \u{1F31E}\n"
    val scalar = 'λ'
    val escaped = '\n'
    /* A non-nesting block comment. */
}

func separators(): int {
    val left = 20; val right = 22
    // A line ending after an operator continues the expression.
    val answer = left +
        right
    answer
}

// Numeric literals have no suffix. Expected types supply their numeric domain.
// A minus sign is a unary operation; signed minimum values need range checking
// together with that operation. There are no raw/interpolated strings in this edition.
// Return's value starts on the same logical line. Trailing commas are accepted
// in parameter/argument lists. Backtick identifier escapes are not part of Sol.
