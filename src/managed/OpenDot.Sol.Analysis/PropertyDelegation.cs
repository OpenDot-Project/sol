// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal readonly record struct ProtocolSelection(ResolvedType Type, ImmutableArray<ResolvedType> Effects,
    AstFunction Function, ResolvedType Owner);

// Source-selected protocol bodies and the post-provideDelegate storage type. Invocation effects
// remain separate from initialization effects. Native consumers do not resolve these names again.
internal sealed record DelegateContract(AstProperty Property, ResolvedType Owner, ResolvedType StoredType,
    ResolvedType ValueType, ImmutableArray<ResolvedType> GetEffects, ImmutableArray<ResolvedType> SetEffects,
    ImmutableArray<ResolvedType> InitEffects, ImmutableArray<ResolvedType> ProvideEffects, bool HasProvider,
    ProtocolSelection Getter, ProtocolSelection? Setter, ProtocolSelection? Provider);
