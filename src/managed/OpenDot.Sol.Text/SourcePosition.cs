// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
namespace OpenDot.Sol.Text;

/// <summary>Zero-based line, Unicode scalar column, and editor UTF-16 column.</summary>
public readonly record struct SourcePosition(int Line, int ScalarColumn, int Utf16Column);
