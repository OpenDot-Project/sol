// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

// Source storage identities are shared by construction proof and native layout. This inventory
// neither assigns offsets nor grants native admission, and never synthesizes AST declarations.
internal static class ConstructionStorage
{
    internal static ImmutableArray<ConstructionSlot> ForOwner(ResolvedType owner, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var slots = ImmutableArray.CreateBuilder<ConstructionSlot>();
        foreach (var part in owner.Declaration?.Parts.OfType<AstTypeDeclaration>() ?? [])
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (var property in part.Members.OfType<AstProperty>())
            {
                cancellation.ThrowIfCancellationRequested();
                if (!HasStorage(property, cancellation)) { continue; }
                slots.Add(new(property, owner, property.Name ?? "",
                    property.Delegate is null ? ConstructionSlotKind.BackingField : ConstructionSlotKind.PropertyDelegate,
                    property.IsMutable));
            }
            foreach (var parameter in part.PrimaryConstructor?.Parameters.Where(parameter => parameter.IsProperty) ?? [])
            {
                cancellation.ThrowIfCancellationRequested();
                slots.Add(new(part.PrimaryConstructor!, owner, parameter.Name, ConstructionSlotKind.PrimaryProperty, parameter.IsMutable));
            }
            foreach (var entry in part.Bases.Where(entry => entry.Children.Any(child => child.SyntaxKind == NodeKind.Delegation)))
            {
                cancellation.ThrowIfCancellationRequested();
                slots.Add(new(entry, owner, entry.Origin.Text, ConstructionSlotKind.InterfaceDelegate, false));
            }
        }
        return slots.ToImmutable();
    }

    private static bool HasStorage(AstProperty property, CancellationToken cancellation)
    {
        if (property.Modifiers.Contains("abstract")) { return false; }
        if (property.Delegate is not null || !property.Accessors.Any()
            || property.Accessors.All(accessor => accessor.Body.Kind == AstBodyKind.Missing)) { return true; }
        foreach (var accessor in property.Accessors)
        {
            if (accessor.Body.Node is not { } body) { continue; }
            foreach (var node in body.Descendants().Prepend(body))
            {
                cancellation.ThrowIfCancellationRequested();
                if (node is AstAtom { Spelling: "field" }) { return true; }
            }
        }
        return false;
    }
}
