// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using OpenDot.Sol.Analysis;

namespace OpenDot.Sol.Fir;

// FIR analysis metadata, not another executable IR. Storage order and initializer order are
// independent: an initializer refers to a canonical source slot, never an input-file index.
internal sealed record FirNativeStorage(ConstructionSlot Source, ResolvedType ValueType,
    string Identity, long Offset, int Size, int Alignment, bool IsGcReference, bool UsesInterfaceCell);

internal sealed record FirNativeTypeLayout(ResolvedType Type, string Identity, ulong TypeId,
    long Size, int Alignment, ResolvedType? Base, ImmutableArray<FirNativeStorage> Storage,
    ImmutableArray<uint> ReferenceOffsets, bool IsInterface, bool IsRuntimeOwned);
