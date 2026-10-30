// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private sealed record InterfaceIndexArguments(ImmutableArray<AstArgument> Arguments, Value[] Values,
        ArgumentMapping Mapping, ImmutableArray<ResolvedType> Parameters);
    private readonly Dictionary<AstNode, InterfaceIndexArguments> _interfaceIndexArguments = [];

    private void ApplyInterfaceIndexEffects(InterfaceIndexArguments invocation, State state, ImmutableArray<ResolvedType> effects)
    {
        var assignedAfterCall = new HashSet<LocalSymbol>();
        foreach (var entry in invocation.Mapping.Arguments)
        {
            var argument = invocation.Arguments[entry.ArgumentIndex];
            var value = invocation.Values[entry.ArgumentIndex];
            var type = invocation.Parameters[entry.ParameterIndex];
            LocalSymbol? local = value.Local;
            if (argument.Mode == AstArgumentMode.OutVariable)
            {
                local = Sources.Lexical.Scopes.SelectMany(scope => scope.Symbols).FirstOrDefault(symbol =>
                    argument.Value.Origin.Span.Start <= symbol.Origin.Span.Start
                    && symbol.Origin.Span.End <= argument.Value.Origin.Span.End
                    && ReferenceEquals(symbol.Origin.Source, argument.Origin.Source));
                if (local is null) { Defer(argument.Value); continue; }
                _localTypes[local] = type;
            }
            if (local is null || argument.Mode is not (AstArgumentMode.Ref or AstArgumentMode.Out or AstArgumentMode.OutVariable)) { continue; }
            state.Narrowed.Remove(local);
            if (argument.Mode is AstArgumentMode.Out or AstArgumentMode.OutVariable)
            { state.Assigned.Remove(local); assignedAfterCall.Add(local); }
        }
        foreach (var local in state.Narrowed.Keys.Where(local => local.IsMutable).ToArray()) { state.Narrowed.Remove(local); }
        InvalidateMutableCleanupNarrowings(state);
        RecordPotentialCallEdges(state, effects);
        state.Effects.UnionWith(effects);
        state.Assigned.UnionWith(assignedAfterCall);
    }
}
