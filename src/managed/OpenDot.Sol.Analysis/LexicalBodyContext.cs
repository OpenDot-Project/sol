// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

/// <summary>Immutable source facts at a direct local call; never a runtime closure environment.</summary>
internal sealed record LexicalBodyContext(SourceAnalysis Sources, AstFunction Function,
    ImmutableArray<LexicalTypeArgument> EnclosingArguments,
    ImmutableArray<LexicalLocalFact> Locals)
{
    internal const int MaximumEnclosingArguments = 128;
    internal const int MaximumLocals = 4096;
    internal string? UnavailableReason { get; init; }
    // Types and locals are workspace-owned identities. The declaration participates separately
    // in the instance key; source order makes this representation independent of dictionary order.
    internal string Identity => (UnavailableReason ?? "") + "|" + string.Join(";", EnclosingArguments.Select(argument =>
        Number(argument.Parameter.Id) + "=" + Number(argument.Argument.Id))) + "|"
        + string.Join(";", Locals.Select(local => Number(local.Local.Id) + ":"
            + (local.Type is null ? "?" : Number(local.Type.Id)) + ":"
            + (local.Assigned ? "1" : "0") + ":"
            + (local.Narrowed is null ? "-" : Number(local.Narrowed.Id))));

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

internal sealed record LexicalTypeArgument(ResolvedType Parameter, ResolvedType Argument);
internal sealed record LexicalLocalFact(LocalSymbol Local, ResolvedType? Type,
    bool Assigned, ResolvedType? Narrowed);
