// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal enum ConstructionSlotKind { BackingField, PrimaryProperty, PropertyDelegate, InterfaceDelegate }
internal enum ConstructionAssignment { Unassigned, Assigned, Unknown }
internal sealed record ConstructionSlot(AstNode Declaration, ResolvedType Owner, string Name,
    ConstructionSlotKind Kind, bool Mutable);
internal sealed record ConstructionOrigin(AstNode Site, string Phase, ImmutableArray<ResolvedType> Effects);
internal sealed record ConstructionSelection(AstNode Site, ResolvedType Owner, AstConstructor? Constructor,
    ImmutableArray<CallArgumentBinding> Arguments, ImmutableArray<int> Defaults, bool IsThisDelegation);
internal sealed record ConstructionProof(ConstructionSelection Selection, BodyBindingStatus Status,
    bool CanCompleteNormally, ImmutableDictionary<ConstructionSlot, ConstructionAssignment> Fields,
    ImmutableArray<ConstructionOrigin> Origins, ImmutableArray<ResolvedType> Effects, string? Reason);
