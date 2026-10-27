// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Fir;

public static partial class FirVerifier
{
    private sealed partial class Checker
    {
        private void CheckObjectMethodBoundary(AstFunction source, int genericArity)
        {
            var file = program.Analysis.Sources.Declarations.Files.Single(candidate => candidate.Nodes
                .OfType<AstFunction>().Any(function => ReferenceEquals(function.Origin.Element, source.Origin.Element)));
            bool hasOwner = HasTypeOwner(file, source);
            if (!hasOwner) { return; }
            if (!GenericMethodSourcePolicy.IsSupportedStatelessObjectMethod(source, program.Analysis.Sources))
            { Fail("Object method FIR is outside the stateless-object method subset.", _function.Origin); }
            if (genericArity != 0
                && !GenericMethodSourcePolicy.IsSupportedGenericObjectMethod(source, program.Analysis.Sources))
            { Fail("Generic member FIR is outside the stateless-object method subset.", _function.Origin); }
        }

        private static bool HasTypeOwner(AstSnapshot file, AstNode node)
        {
            for (var parent = file.GetParent(node); parent is not null; parent = file.GetParent(parent))
            { if (parent is AstTypeDeclaration) { return true; } }
            return false;
        }
    }
}
