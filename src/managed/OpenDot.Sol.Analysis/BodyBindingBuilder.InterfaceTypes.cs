// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private Dictionary<ResolvedType, ResolvedType> InterfaceSubstitution(ResolvedType owner, AstFunction? method = null)
    {
        var result = Substitution(owner);
        var parameters = method is null ? [] : Sources.Types.GetTypeParameters(method).ToHashSet();
        foreach (var pair in _bodySubstitution)
        { if (!parameters.Contains(pair.Key)) { result.TryAdd(pair.Key, pair.Value); } }
        return result;
    }

    private ResolvedType InterfaceWritten(AstType syntax, ResolvedType owner, AstFunction? method = null) =>
        Types.Substitute(Sources.Types.GetType(syntax), InterfaceSubstitution(owner, method), _cancellation);
}
