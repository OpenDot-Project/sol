// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    internal AccessorBodyAnalysis BuildAccessorBody(ResolvedType owner, AstAccessor accessor)
    {
        _cancellation.ThrowIfCancellationRequested();
        Types.ValidateOwnership(owner);
        foreach (var pair in Substitution(owner)) { _bodySubstitution[pair.Key] = pair.Value; }
        foreach (var part in owner.Declaration!.Parts.OfType<AstTypeDeclaration>())
        {
            var constraints = Types.CheckConstraints(part, owner.Arguments, _cancellation);
            if (constraints.Status == ConstraintStatus.Unsatisfied)
            {
                Report("SOL2221", "Generic owner constraints are not satisfied for the closed accessor body.", part);
                return new(this, owner, new(accessor, null, false, BodyBindingStatus.Invalid, []));
            }
            if (constraints.Status == ConstraintStatus.Indeterminate)
            {
                Defer(accessor);
                return new(this, owner, new(accessor, null, false, BodyBindingStatus.Deferred, []));
            }
        }
        var summary = CheckBody(accessor);
        CompleteInterfaceImplementations();
        CompleteConstructionProofs();
        _cancellation.ThrowIfCancellationRequested();
        return new(this, owner, Bodies.GetValueOrDefault(accessor, summary));
    }
}
