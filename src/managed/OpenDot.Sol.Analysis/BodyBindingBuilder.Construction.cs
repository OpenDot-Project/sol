// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private IEnumerable<AstProperty> StoredProperties(ResolvedType owner) => owner.Declaration?.Parts
        .OfType<AstTypeDeclaration>().SelectMany(t => t.Members).OfType<AstProperty>()
        .Where(p => !p.Accessors.Any() || p.Accessors.All(a => a.Body.Kind == AstBodyKind.Missing)) ?? [];

    private IEnumerable<AstNode> InitializationMembers(ResolvedType owner) => owner.Declaration?.Parts
        .OfType<AstTypeDeclaration>().SelectMany(t => t.Members)
        .Where(member => member is AstProperty or AstInitializer) ?? [];

    private void InitializeFields(AstConstructor constructor, State state, Context context)
    {
        var owner = ContainingType(constructor);
        if (owner is null) { Defer(constructor); return; }
        if (constructor.Delegation is not null || Types.GetDirectBases(owner).Any(t => t.Declaration is not { ModuleName: "std.core", Name: "Object" }))
        { Defer(constructor); }
        var stored = StoredProperties(owner).ToHashSet();
        foreach (var member in InitializationMembers(owner))
        {
            if (member is AstProperty delegatedProperty && stored.Contains(delegatedProperty)
                && delegatedProperty.Delegate is not null)
            {
                var contract = Delegate(delegatedProperty, state, context);
                if (contract is null) { Defer(delegatedProperty); continue; }
                state.Effects.UnionWith(contract.InitEffects);
                state.Fields.Add(delegatedProperty);
            }
            else if (member is AstProperty { Delegate: null, Initializer: { } propertyInitializer } storedProperty
                && stored.Contains(storedProperty))
            {
                var expected = storedProperty.Type is null ? null : Specialize(storedProperty.Type, owner);
                Require(Expression(propertyInitializer, state, context, expected), expected, propertyInitializer, state);
                state.Fields.Add(storedProperty);
            }
            else if (member is AstInitializer { Body: not null } initBlock)
            { Statements(initBlock.Body!.Statements, state, context, false); }
        }
    }

    private void CheckInitialized(State state, Context context, AstNode exit)
    {
        if (context.Callable is not AstConstructor constructor || ContainingType(constructor) is not { } owner) { return; }
        foreach (var property in StoredProperties(owner))
        {
            if (!state.Fields.Contains(property)) { Report("SOL2270", "Constructor leaves a stored property unassigned: " + property.Name, exit); }
        }
    }

    private bool IsOwnConstructorReceiver(AstExpression? receiver, Context context) => context.Callable is AstConstructor
        && receiver is AstAtom { Spelling: "this" };

    private void ConstructorRead(AstProperty property, AstNode location, State state, Context context, bool read)
    {
        if (location is AstMember { Receiver: not AstAtom { Spelling: "this" } }) { return; }
        if (read && context.Callable is AstConstructor && ContainingType(property)?.Declaration == ContainingType(context.Callable)?.Declaration
            && !state.Fields.Contains(property) && StoredProperties(ContainingType(context.Callable)!).Contains(property))
        { Report("SOL2271", "Stored property is read before initialization.", location); }
    }

    private Value? ImplicitConstruction(AstExpression? expression, AstCall call, State state)
    {
        if (expression is not AstAtom atom || !call.Arguments.IsEmpty) { return null; }
        var symbols = Sources.Lexical.Lookup(atom, atom.Spelling).Declarations.Candidates;
        if (symbols is not [var symbol] || symbol.Kind != SymbolKind.Type || symbol.Arity != 0
            || symbol.Parts[0].SyntaxKind is not (NodeKind.ClassDeclaration or NodeKind.StructDeclaration or NodeKind.RefStructDeclaration)) { return null; }
        if (symbol.Parts.OfType<AstTypeDeclaration>().Any(t => t.PrimaryConstructor is not null)
            || symbol.Members.Any(m => m.Kind == SymbolKind.Constructor)) { return null; }
        if (symbol.Parts[0].Modifiers.Contains("abstract")) { Report("SOL2223", "An abstract type cannot be constructed.", call); }
        var type = Types.Nominal(symbol, []);
        if (Types.GetDirectBases(type).Any(t => t.Declaration is not { ModuleName: "std.core", Name: "Object" }))
        {
            Defer(call);
            return new(type);
        }

        var parts = symbol.Parts.OfType<AstTypeDeclaration>().ToArray();
        var stored = StoredProperties(type).ToHashSet();
        if (parts.Length == 0 || parts.Any(part => part.Members.OfType<AstInitializer>().Any())
            || parts.SelectMany(part => part.Members.OfType<AstProperty>())
                .Any(property => (property.Delegate is not null || property.Initializer is not null)
                    && !stored.Contains(property)))
        {
            Defer(call);
            return new(type);
        }

        var initializerParts = parts.Select(part => (Part: part, Properties: part.Members.OfType<AstProperty>()
                .Where(property => stored.Contains(property) && (property.Initializer is not null || property.Delegate is not null)).ToArray()))
            .Where(entry => entry.Properties.Length != 0).ToArray();
        if (initializerParts.Length > 1)
        {
            // Partial type member order is not initialization order. This analyzer supports only
            // the single initialization part required by the source contract.
            Defer(call);
            return new(type);
        }

        var initialization = state.Clone();
        var exceptionalEdges = new List<State.ExceptionEdge>();
        initialization.ExceptionSink = exceptionalEdges;
        // Heap allocation can fail before any class field initializer has run. Value and ref
        // struct construction does not allocate an object and must not gain this edge.
        if (symbol.Parts.Any(part => part.SyntaxKind == NodeKind.ClassDeclaration))
        { RecordPotentialCallEdges(initialization, []); }
        var initialized = new HashSet<AstProperty>();
        if (initializerParts.Length == 1)
        {
            var context = new Context(null, null);
            foreach (var property in initializerParts[0].Properties)
            {
                if (property.Delegate is not null)
                {
                    if (ReferencesConstructingInstance(property.Delegate, symbol))
                    { Defer(call); return new(type); }
                    var contract = Delegate(property, initialization, context);
                    if (contract is null || contract.HasProvider)
                    {
                        // provideDelegate receives the not-yet-published owner. Until that escape
                        // is proved safe, do not publish effects from the partially analyzed object.
                        Defer(call);
                        return new(type);
                    }
                    initialization.Effects.UnionWith(contract.InitEffects);
                    initialized.Add(property);
                }
                else if (property.Initializer is { } initializer)
                {
                    if (ReferencesConstructingInstance(initializer, symbol))
                    { Defer(call); return new(type); }
                    var expected = property.Type is null ? null : Specialize(property.Type, type);
                    var value = Expression(initializer, initialization, context, expected);
                    Require(value, expected, initializer, initialization);
                    if (value.Type?.Kind == ResolvedTypeKind.Bottom)
                    {
                        initialization.Reachable = false;
                        break;
                    }
                    initialized.Add(property);
                }
            }
        }

        // A bottoming initializer cannot produce an object, so later fields are not required on
        // that path. The exceptional edges already captured the state at each possible failure.
        if (initialization.Reachable)
        {
            foreach (var property in stored)
            {
                if (initialized.Contains(property)) { continue; }
                Report("SOL2270", "Implicit construction leaves a stored property unassigned: " + property.Name, call);
            }
        }

        state.Effects.UnionWith(initialization.Effects);
        state.Reachable = initialization.Reachable;
        if (state.ExceptionSink is { } sink) { sink.AddRange(exceptionalEdges); }
        return new(initialization.Reachable ? type : Types.GetBottom());
    }

    private bool ReferencesConstructingInstance(AstNode root, DeclarationSymbol owner)
    {
        var pending = new Stack<AstNode>();
        var visited = new HashSet<AstNode>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (!visited.Add(node)) { continue; }
            if (visited.Count > 65536) { Defer(root); return true; }
            if (node is AstAtom { Spelling: "this" or "super" }) { return true; }
            if (node is AstAtom { SyntaxKind: NodeKind.NameExpression } atom
                && !Sources.Lexical.Lookup(atom, atom.Spelling).IsLocal
                && owner.Members.Any(member => member.Name == atom.Spelling))
            { return true; }
            foreach (var child in node.Children) { pending.Push(child); }
        }
        return false;
    }
}
