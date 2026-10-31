// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private bool IsProvenNonEscaping(AstNode callable) => callable switch
    {
        AstLambda lambda => Sources.Lexical.GetLambdaEscape(lambda).Disposition
            == LambdaEscapeDisposition.ProvenNonEscaping,
        AstAnonymousFunction function => Sources.Lexical.GetAnonymousFunctionEscape(function).Disposition
            == AnonymousFunctionEscapeDisposition.ProvenNonEscaping,
        _ => false,
    };

    private void CheckRefLikeReceiverCaptures(AstNode callable, IEnumerable<AstTypeDeclaration> owners,
        IEnumerable<System.Collections.Immutable.ImmutableArray<AstAtom>> references)
    {
        if (IsProvenNonEscaping(callable)) { return; }
        foreach (var pair in owners.Zip(references))
        {
            if (pair.First.SyntaxKind != NodeKind.RefStructDeclaration) { continue; }
            foreach (var reference in pair.Second)
            {
                _cancellation.ThrowIfCancellationRequested();
                Report("SOL2262", "A closure that is not proven nonescaping cannot capture ref/out storage or a ref-like value.", reference);
            }
        }
    }
}
