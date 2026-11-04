// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using OpenDot.Sol.Analysis;

namespace OpenDot.Sol.Fir;

// ABI identities contain logical declarations and closed types. Workspace IDs and source paths
// are deliberately absent; length prefixes make punctuation and Unicode names unambiguous.
internal static class FirNativeIdentity
{
    internal static string Type(TypeSpecialization workspace, ResolvedType type, CancellationToken cancellation,
        Func<GenericParameterIdentity, string>? formalParameter = null)
    {
        cancellation.ThrowIfCancellationRequested();
        workspace.ValidateOwnership(type);
        int nodes = 0;
        return "sol-type-v0;" + Encode(type, 0);

        string Encode(ResolvedType current, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (depth > 128 || ++nodes > 65536) { throw new FirNativeLayoutException("SOL2403", "Native type identity expansion limit exceeded."); }
            workspace.ValidateOwnership(current);
            if (current.Parameter is { } parameter && formalParameter is not null)
            { return "formal:" + formalParameter(parameter); }
            if (current.Kind is ResolvedTypeKind.Error or ResolvedTypeKind.Parameter)
            { throw new FirNativeLayoutException("SOL2400", "Native identities require closed, resolved types."); }
            var text = new StringBuilder();
            void Atom(string value) => text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
            Atom(current.Kind.ToString());
            if (current.Declaration is { } declaration)
            {
                Atom(declaration.ModuleName); Atom(declaration.QualifiedName);
                Atom(declaration.Arity.ToString(CultureInfo.InvariantCulture));
            }
            else { Atom(current.Name); }
            Atom(current.Arguments.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var argument in current.Arguments) { Atom(Encode(argument, depth + 1)); }
            Atom(current.ParameterModes.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var mode in current.ParameterModes) { Atom(mode.ToString()); }
            Atom(current.Effects.Length.ToString(CultureInfo.InvariantCulture));
            // Effect order is not semantic; all children share the depth and node budget.
            if (!current.Effects.IsEmpty)
            {
                var effects = new string[current.Effects.Length];
                for (int index = 0; index < effects.Length; index++)
                { effects[index] = Encode(current.Effects[index], depth + 1); }
                Array.Sort(effects, StringComparer.Ordinal);
                foreach (string effect in effects) { Atom(effect); }
            }
            return text.ToString();
        }
    }

    internal static string Symbol(string identity) => "sol_v0_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();

    internal static ulong TypeId(string identity)
        => BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) | (1UL << 63);
}

internal sealed class FirNativeLayoutException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}
