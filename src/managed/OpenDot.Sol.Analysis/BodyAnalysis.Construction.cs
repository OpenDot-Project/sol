// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

public sealed partial class BodyAnalysis
{
    private BodyBindingStatus? GetConstructionSourceStatus(ResolvedType owner, AstConstructor? constructor)
    {
        Types.ValidateOwnership(owner);
        return ConstructionProofs.Values.Any(proof => proof.Selection.Owner.Declaration == owner.Declaration
            && proof.Selection.Constructor == constructor
            && (proof.Selection.Site == constructor || constructor is null && proof.Selection.Site is AstTypeDeclaration)
            && proof.Status == BodyBindingStatus.Invalid) ? BodyBindingStatus.Invalid : null;
    }
}
