// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private void ValidateLexicalCaptureLifetime(AstFunction function)
    {
        var escape = Sources.Lexical.GetLocalFunctionEscape(function);
        if (escape.Disposition == LocalFunctionEscapeDisposition.ProvenNonEscaping) { return; }
        foreach (var capture in Sources.Lexical.GetLocalFunctionCaptures(function))
        {
            _cancellation.ThrowIfCancellationRequested();
            var type = LocalType(capture.Local);
            bool borrowed = capture.Local.PassingMode is AstPassingMode.Ref or AstPassingMode.Out
                || type is not null && MayContainBorrow(type);
            if (!borrowed) { continue; }
            foreach (var reference in capture.References)
            {
                Report("SOL2262", "A local function that may escape cannot capture ref/out storage or a ref-like value.",
                    reference);
            }
        }
        foreach (var receiver in Sources.Lexical.GetLocalFunctionReceiverCaptures(function))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (receiver.Owner.SyntaxKind != NodeKind.RefStructDeclaration) { continue; }
            foreach (var reference in receiver.References)
            { Report("SOL2262", "A local function that may escape cannot capture a ref-like receiver.", reference); }
        }
    }
}
