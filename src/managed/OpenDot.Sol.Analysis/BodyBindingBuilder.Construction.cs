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
        foreach (var property in StoredProperties(type))
        {
            if (property.Delegate is not null) { Defer(call); }
            else if (property.Initializer is null) { Report("SOL2270", "Implicit construction leaves a stored property unassigned: " + property.Name, call); }
            else { Defer(call); }
        }
        if (Types.GetDirectBases(type).Any(t => t.Declaration is not { ModuleName: "std.core", Name: "Object" })) { Defer(call); }
        if (symbol.Parts.OfType<AstTypeDeclaration>().Any(t => t.Members.Any(m => m is AstInitializer))) { Defer(call); }
        return new(type);
    }
}
