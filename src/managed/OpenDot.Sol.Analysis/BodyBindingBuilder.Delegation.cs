// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private sealed record DelegateContract(ResolvedType ValueType, ImmutableArray<ResolvedType> GetEffects,
        ImmutableArray<ResolvedType> SetEffects, ImmutableArray<ResolvedType> InitEffects,
        ImmutableArray<ResolvedType> ProvideEffects, bool HasProvider);
    private readonly Dictionary<AstProperty, DelegateContract?> _delegates = [];
    private readonly HashSet<AstProperty> _activeDelegates = [];

    private DelegateContract? Delegate(AstProperty property, State? initializationState = null,
        Context? initializationContext = null)
    {
        if (_delegates.TryGetValue(property, out var cached))
        {
            if (cached is not null && initializationState is not null)
            {
                var replay = initializationState.Clone(); replay.Effects.Clear();
                _ = Expression(property.Delegate, replay, initializationContext ?? new(null, null));
                if (cached.HasProvider) { RecordPotentialCallEdges(replay, cached.ProvideEffects); }
                CopyFlowFacts(replay, initializationState);
            }
            return cached;
        }
        if (!_activeDelegates.Add(property)) { Report("SOL2200", "Delegation inference cycle requires a declared type.", property); return null; }
        try
        {
            if (property.Type is null || property.Delegate is null) { Defer(property); return null; }
            var propertyType = Written(property.Type);
            var owner = ContainingType(property) ?? Types.GetNullable(Types.GetBottom());
            var descriptorSymbol = Sources.Declarations.Modules.GetValueOrDefault("std.reflect")?.Declarations
                .SingleOrDefault(s => s.Kind == SymbolKind.Type && s.Name == "SolProperty" && s.Arity == 1);
            if (descriptorSymbol is null) { Defer(property); return null; }
            var descriptor = Types.Nominal(descriptorSymbol, [propertyType]);
            var state = initializationState?.Clone() ?? new State();
            state.Effects.Clear();
            if (initializationState is null)
            {
                foreach (var scope in Sources.Lexical.Scopes)
                {
                    foreach (var local in scope.Symbols.Where(l => l.Kind is LocalSymbolKind.ConstructorProperty or LocalSymbolKind.Parameter))
                    { state.Assigned.Add(local); }
                }
            }
            var value = Expression(property.Delegate, state, initializationContext ?? new(null, null));
            if (value.Type is null) { Defer(property); return null; }
            var actual = value.Type;
            var provider = Protocol(actual, "provideDelegate", [owner, descriptor], property, false);
            if (provider is { } provide)
            {
                actual = provide.Type;
                RecordPotentialCallEdges(state, provide.Effects);
                state.Effects.UnionWith(provide.Effects);
            }
            var getter = Protocol(actual, "getValue", [owner, descriptor], property, true);
            var setter = property.IsMutable ? Protocol(actual, "setValue", [owner, descriptor, propertyType], property, true) : null;
            if (getter is null || property.IsMutable && setter is null) { return null; }
            if (!Converts(getter.Value.Type, propertyType)) { Report("SOL2281", "Delegated getter result is incompatible with the property type.", property); }
            if (setter is { } write && write.Type != Empty) { Report("SOL2281", "Delegated setter must return ().", property); }
            if (property.Initializer is not null || property.Accessors.Any(a => a.Body.Kind != AstBodyKind.Missing) || property.Modifiers.Contains("ref"))
            { Report("SOL2282", "Delegation cannot be combined with an initializer, custom accessor, or ref property.", property); }
            var contract = new DelegateContract(propertyType, getter.Value.Effects,
                setter?.Effects ?? [], state.Effects.ToImmutableArray(),
                provider?.Effects ?? [], provider is not null);
            _delegates[property] = contract;
            if (initializationState is not null) { CopyFlowFacts(state, initializationState); }
            return contract;
        }
        finally { _activeDelegates.Remove(property); }
    }

    private (ResolvedType Type, ImmutableArray<ResolvedType> Effects)? Protocol(ResolvedType receiver, string name,
        ResolvedType[] arguments, AstProperty property, bool required)
    {
        var candidates = FindMembers(receiver, name, property).Where(m => Accessible(m.Symbol, property))
            .Select(m => (Member: m, Function: m.Symbol.Parts[0] as AstFunction))
            .Where(p => p.Function is not null && p.Function.Modifiers.Contains("operator")
                && p.Function.Parameters.Length == arguments.Length && !p.Function.TypeParameters.Any())
            .Where(p => p.Function!.Parameters.Select((parameter, index) => parameter.Type is not null
                && Converts(arguments[index], InterfaceWritten(parameter.Type, p.Member.DeclaringType))).All(b => b)).ToArray();
        if (candidates.Length != 1)
        {
            if (required || candidates.Length > 1) { Report("SOL2280", "Delegation protocol has no unique applicable operator: " + name, property); }
            return null;
        }
        var selected = candidates[0];
        var function = selected.Function!;
        foreach (var parameter in function.Parameters.Where(p => p.Type is not null))
            { ValidateFunctionEffects(InterfaceWritten(parameter.Type!, selected.Member.DeclaringType), property); }
        var result = function.ReturnType is null ? SpecializeInferredInterfaceReturn(function, selected.Member.DeclaringType)
            : InterfaceWritten(function.ReturnType, selected.Member.DeclaringType);
        if (result is null) { Defer(property); return null; }
        ValidateFunctionEffects(result, property);
        return (result, function.ThrownTypes.Select(t => InterfaceWritten(t, selected.Member.DeclaringType)).ToImmutableArray());
    }

    private static void CopyFlowFacts(State source, State destination)
    {
        destination.Assigned.Clear(); destination.Assigned.UnionWith(source.Assigned);
        destination.Narrowed.Clear(); foreach (var pair in source.Narrowed) { destination.Narrowed.Add(pair.Key, pair.Value); }
        destination.Fields.Clear(); destination.Fields.UnionWith(source.Fields);
        destination.Reachable = source.Reachable;
    }
}
