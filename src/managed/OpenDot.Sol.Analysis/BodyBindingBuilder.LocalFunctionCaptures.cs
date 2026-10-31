// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private void ValidateBorrowedLocalFunctionCaptures()
    {
        foreach (var escape in Sources.Lexical.LocalFunctionEscapes)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (escape.Disposition == LocalFunctionEscapeDisposition.ProvenNonEscaping) { continue; }
            foreach (var capture in Sources.Lexical.GetLocalFunctionCaptures(escape.Function))
            {
                _cancellation.ThrowIfCancellationRequested();
                var type = LocalType(capture.Local);
                bool borrowed = capture.Local.PassingMode is AstPassingMode.Ref or AstPassingMode.Out
                    || type is not null && MayContainBorrow(type);
                if (!borrowed) { continue; }
                foreach (var reference in capture.References)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    Report("SOL2262", "A local function that may escape cannot capture ref/out storage or a ref-like value.",
                        reference);
                }
            }
        }
    }
}
