// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private static LexicalBodyContext MergeLexicalCallContexts(BodyCallBinding previous,
        BodyCallBinding current, LexicalBodyContext next)
    {
        var prior = previous.LexicalContext;
        if (prior?.UnavailableReason is { } reason)
        { return next with { UnavailableReason = reason }; }
        if (next.UnavailableReason is not null) { return next; }
        // A finally/defer body can visit the same source call on several abstract exits.
        // Its published proof must cover every visit, independently of traversal order.
        if (prior is null || !ReferenceEquals(previous.Target, current.Target)
            || !previous.TypeArguments.SequenceEqual(current.TypeArguments)
            || !ReferenceEquals(previous.ReturnType, current.ReturnType)
            || !prior.EnclosingArguments.SequenceEqual(next.EnclosingArguments)
            || !prior.Locals.Select(fact => fact.Local).SequenceEqual(next.Locals.Select(fact => fact.Local)))
        {
            return next with { UnavailableReason = "Local call has incompatible lexical contexts across source flow paths." };
        }
        var locals = ImmutableArray.CreateBuilder<LexicalLocalFact>(next.Locals.Length);
        for (int index = 0; index < next.Locals.Length; index++)
        {
            var left = prior.Locals[index];
            var right = next.Locals[index];
            bool assigned = left.Assigned && right.Assigned;
            locals.Add(new(right.Local, ReferenceEquals(left.Type, right.Type) ? right.Type : null,
                assigned, assigned && ReferenceEquals(left.Narrowed, right.Narrowed) ? right.Narrowed : null));
        }
        return next with { Locals = locals.ToImmutable() };
    }
}
