// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed record ArgumentMapping(ImmutableArray<CallArgumentBinding> Arguments,
    ImmutableArray<int> Defaults, ImmutableArray<int> EmptyVarargs, bool UsesVararg, string? Error);

/// <summary>Source ordering and parameter assignment, independent of expression typing.</summary>
internal static class CallArgumentMapper
{
    internal static ArgumentMapping Map(AstCall call, AstCallable function, ParameterDefaultSet defaultContract, CancellationToken cancellation)
    {
        var parameters = function.Parameters;
        var mappings = ImmutableArray.CreateBuilder<CallArgumentBinding>();
        var defaults = ImmutableArray.CreateBuilder<int>();
        var empty = ImmutableArray.CreateBuilder<int>();
        var occupied = new bool[parameters.Length];
        var named = new HashSet<string>(StringComparer.Ordinal);
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < parameters.Length; index++) { indexes.TryAdd(parameters[index].Name, index); }
        int position = 0;
        bool requiresNamed = false;
        // Ranking records whether a source argument actually uses the vararg parameter. An omitted
        // vararg is tracked separately as an empty pack and must not penalize ordinary arity.
        bool usesVararg = false;
        ArgumentMapping Fail(string message) => new(mappings.ToImmutable(), defaults.ToImmutable(), empty.ToImmutable(), usesVararg, message);

        for (int index = 0; index < call.Arguments.Length; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var argument = call.Arguments[index];
            int target;
            if (argument.IsTrailingLambda)
            {
                if (index != call.Arguments.Length - 1 || parameters.IsEmpty) { return Fail("A trailing lambda requires a final parameter."); }
                target = parameters.Length - 1;
                if (parameters[target].PassingMode != AstPassingMode.Value) { return Fail("A trailing lambda cannot fill a vararg/ref/out parameter."); }
            }
            else if (argument.Name is { } name)
            {
                if (!named.Add(name)) { return Fail("Duplicate named argument."); }
                if (!indexes.TryGetValue(name, out target)) { return Fail("Unknown named parameter."); }
                if (target != position) { requiresNamed = true; }
            }
            else
            {
                if (requiresNamed) { return Fail("Positional arguments must preserve parameter order before reordered or skipped named parameters."); }
                target = position;
            }
            if (target >= parameters.Length) { return Fail("Too many arguments."); }
            var mode = parameters[target].PassingMode;
            bool vararg = mode == AstPassingMode.Vararg;
            bool arrayArgument = vararg && argument.Name is not null;
            if (occupied[target] && (!vararg || arrayArgument || named.Contains(parameters[target].Name)))
            {
                return Fail("A parameter was supplied more than once.");
            }
            occupied[target] = true;
            usesVararg |= vararg;
            if (target == position && (!vararg || arrayArgument)) { position++; }
            if (vararg && arrayArgument) { requiresNamed = true; }
            bool markerMatches = mode switch
            {
                AstPassingMode.Ref => argument.Mode == AstArgumentMode.Ref,
                AstPassingMode.Out => argument.Mode is AstArgumentMode.Out or AstArgumentMode.OutVariable,
                AstPassingMode.Vararg => argument.Mode is AstArgumentMode.Value or AstArgumentMode.Spread,
                _ => argument.Mode == AstArgumentMode.Value,
            };
            if (!markerMatches) { return Fail("Argument passing mode mismatch."); }
            mappings.Add(new(index, target, vararg && !arrayArgument && argument.Mode != AstArgumentMode.Spread,
                argument.Mode == AstArgumentMode.Spread)
            {
                IsArrayArgument = arrayArgument,
                IsTrailingLambda = argument.IsTrailingLambda,
            });
        }
        for (int index = 0; index < parameters.Length; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (occupied[index]) { continue; }
            if (defaultContract.Parameters[index] is not null) { defaults.Add(index); continue; }
            if (parameters[index].PassingMode == AstPassingMode.Vararg) { empty.Add(index); continue; }
            return Fail("A required parameter is missing.");
        }
        return new(mappings.ToImmutable(), defaults.ToImmutable(), empty.ToImmutable(), usesVararg, null);
    }
}
