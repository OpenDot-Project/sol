// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

public sealed partial class BodyAnalysis
{
    private void ValidateLexicalContext(AstFunction function, LexicalBodyContext context,
        CancellationToken cancellation)
    {
        if (context.EnclosingArguments.IsDefault || context.Locals.IsDefault || context.UnavailableReason is not null
            || context.EnclosingArguments.Length > LexicalBodyContext.MaximumEnclosingArguments
            || context.Locals.Length > LexicalBodyContext.MaximumLocals)
        { throw new ArgumentException("An unavailable or over-budget lexical context cannot create a body.", nameof(context)); }
        var file = BodySourceIndex.Get(Sources).Files[function];
        var ancestors = new List<AstNode>();
        for (var parent = file.GetParent(function); parent is not null; parent = file.GetParent(parent))
        {
            cancellation.ThrowIfCancellationRequested();
            ancestors.Add(parent);
        }
        if (!ancestors.Any(node => node is AstCallable or AstLambda))
        { throw new ArgumentException("A lexical body must be nested inside a source callable.", nameof(function)); }
        var parameters = ancestors.AsEnumerable().Reverse().OfType<AstDeclaration>()
            .SelectMany(declaration => Sources.Types.GetTypeParameters(declaration)).ToArray();
        if (context.EnclosingArguments.IsDefault || context.Locals.IsDefault
            || !parameters.SequenceEqual(context.EnclosingArguments.Select(pair => pair.Parameter)))
        { throw new ArgumentException("A lexical context must retain every enclosing generic parameter in source order.", nameof(context)); }
        var owners = ancestors.ToHashSet();
        var locals = Sources.Lexical.Scopes.Where(scope => owners.Contains(scope.Owner))
            .SelectMany(scope => scope.Symbols).Where(local => local.Kind != LocalSymbolKind.Function)
            .OrderBy(local => local.Id).ToArray();
        if (!locals.SequenceEqual(context.Locals.Select(fact => fact.Local)))
        { throw new ArgumentException("A lexical context must retain exactly its ancestor locals in identity order.", nameof(context)); }
        foreach (var fact in context.Locals)
        {
            cancellation.ThrowIfCancellationRequested();
            if (fact.Narrowed is null) { continue; }
            if (!fact.Assigned || fact.Local.IsMutable || fact.Type is null
                || Types.ClassifyConversion(fact.Narrowed, fact.Type, cancellation) == IntrinsicConversion.None)
            { throw new ArgumentException("A lexical smart cast requires assigned immutable storage and a compatible type.", nameof(context)); }
        }
    }
}
