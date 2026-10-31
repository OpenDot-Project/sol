// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using OpenDot.Sol.Analysis;

namespace OpenDot.Sol.Fir;

public static partial class FirVerifier
{
    internal static bool IsEmpty(ResolvedType type) => type.Kind == ResolvedTypeKind.Tuple && type.Arguments.IsEmpty;

    internal static bool IsString(ResolvedType type) =>
        type.Declaration is { ModuleName: "std.core", Name: "String", Arity: 0 };

    internal static bool IsNullSentinel(ResolvedType type) => type.Kind == ResolvedTypeKind.Nullable
        && type.Arguments is [{ Kind: ResolvedTypeKind.Bottom }];

    internal static bool IsNullableFunction(ResolvedType type) => type.Kind == ResolvedTypeKind.Nullable
        && type.Arguments is [{ Kind: ResolvedTypeKind.Function }];

    internal static bool IsAssignable(ResolvedType actual, ResolvedType expected) => actual == expected
        || IsNullSentinel(actual) && IsNullableFunction(expected)
        || actual.Kind == ResolvedTypeKind.Function && IsNullableFunction(expected)
            && actual == expected.Arguments[0];

    /// <summary>Bounds and validates the recursively supported FIR value-type shapes.</summary>
    internal static bool Supported(ResolvedType type)
    {
        var pending = new Stack<(ResolvedType Type, int Depth, bool AllowsVoid, bool AllowsNull)>();
        pending.Push((type, 0, true, true));
        while (pending.TryPop(out var current))
        {
            if (current.Depth > 128) { return false; }
            if (IsEmpty(current.Type) || current.Type.Kind == ResolvedTypeKind.Bottom)
            {
                if (current.AllowsVoid) { continue; }
                return false;
            }
            if (current.Type.Kind == ResolvedTypeKind.Primitive)
            {
                if (current.Type.Name is "half" or "quad") { return false; }
                continue;
            }
            if (IsString(current.Type)) { continue; }
            if (current.Type.Kind == ResolvedTypeKind.Array && current.Type.Arguments.Length == 1)
            {
                if (IsNullSentinel(current.Type.Arguments[0])) { return false; }
                pending.Push((current.Type.Arguments[0], current.Depth + 1, false, false));
                continue;
            }
            if (current.Type.Kind == ResolvedTypeKind.Nullable && current.Type.Arguments.Length == 1)
            {
                var payload = current.Type.Arguments[0];
                if (payload.Kind == ResolvedTypeKind.Bottom)
                {
                    if (current.AllowsNull) { continue; }
                    return false;
                }
                if (payload.Kind == ResolvedTypeKind.Function)
                {
                    pending.Push((payload, current.Depth + 1, false, false));
                    continue;
                }
                return false;
            }
            if (current.Type.Kind == ResolvedTypeKind.Function
                && current.Type.Arguments.Length == current.Type.ParameterModes.Length + 1)
            {
                for (int index = 0; index < current.Type.ParameterModes.Length; index++)
                { pending.Push((current.Type.Arguments[index], current.Depth + 1, false, false)); }
                pending.Push((current.Type.Arguments[^1], current.Depth + 1, true, false));
                continue;
            }
            return false;
        }
        return true;
    }
}
