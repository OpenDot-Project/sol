// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class SpecializedBodyAnalysis
{
    internal LambdaBodySummary? GetLambdaBody(AstLambda lambda)
    {
        ArgumentNullException.ThrowIfNull(lambda);
        if (!_nodes.Contains(lambda))
        { throw new ArgumentException("The lambda belongs to another source analysis.", nameof(lambda)); }
        return _lambdaBodies.GetValueOrDefault(lambda);
    }

    internal AnonymousFunctionBodySummary? GetAnonymousFunctionBody(AstAnonymousFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (!_nodes.Contains(function))
        { throw new ArgumentException("The anonymous function belongs to another source analysis.", nameof(function)); }
        return _anonymousFunctionBodies.GetValueOrDefault(function);
    }
}
