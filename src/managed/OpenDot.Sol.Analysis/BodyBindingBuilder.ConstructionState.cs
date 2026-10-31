// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    internal sealed record ConstructionFacts(ImmutableDictionary<ConstructionSlot, ConstructionAssignment> Slots,
        ImmutableHashSet<LocalSymbol> Aliases)
    {
        internal ImmutableDictionary<LocalSymbol, ConstructionCallback> Callbacks { get; init; }
            = ImmutableDictionary<LocalSymbol, ConstructionCallback>.Empty;
        internal static readonly ConstructionFacts Empty = new(ImmutableDictionary<ConstructionSlot, ConstructionAssignment>.Empty, []);
        internal static ConstructionFacts Join(ConstructionFacts left, ConstructionFacts right) => new(
            left.Slots.Keys.Union(right.Slots.Keys).ToImmutableDictionary(slot => slot, slot =>
                left.Slots.GetValueOrDefault(slot) == right.Slots.GetValueOrDefault(slot)
                    ? left.Slots.GetValueOrDefault(slot) : ConstructionAssignment.Unknown),
            left.Aliases.Union(right.Aliases))
        { Callbacks = left.Callbacks.Where(pair => right.Callbacks.GetValueOrDefault(pair.Key) == pair.Value).ToImmutableDictionary() };
        internal ConstructionFacts Update(ConstructionFacts before, ConstructionFacts after)
        {
            var slots = Slots.ToBuilder();
            foreach (var slot in before.Slots.Keys.Union(after.Slots.Keys))
            {
                if (before.Slots.GetValueOrDefault(slot) != after.Slots.GetValueOrDefault(slot))
                { slots[slot] = after.Slots.GetValueOrDefault(slot); }
            }
            return new(slots.ToImmutable(), Aliases.Except(before.Aliases.Except(after.Aliases))
                .Union(after.Aliases.Except(before.Aliases))) { Callbacks = after.Callbacks };
        }
    }

    internal sealed partial class State
    {
        internal ConstructionFacts Construction = ConstructionFacts.Empty;
    }

    private ConstructionSlot PropertySlot(AstProperty property, ResolvedType owner) => new(property, owner,
        property.Name ?? "", property.Delegate is null ? ConstructionSlotKind.BackingField : ConstructionSlotKind.PropertyDelegate,
        property.IsMutable);

    private IEnumerable<ConstructionSlot> ConstructionSlots(ResolvedType owner)
    {
        foreach (var part in owner.Declaration?.Parts.OfType<AstTypeDeclaration>() ?? [])
        {
            foreach (var property in part.Members.OfType<AstProperty>().Where(property =>
                !property.Modifiers.Contains("abstract") && (property.Delegate is not null || !property.Accessors.Any()
                || property.Accessors.All(accessor => accessor.Body.Kind == AstBodyKind.Missing)
                || property.Accessors.Any(accessor => accessor.Body.Node is { } body
                    && body.Descendants().Prepend(body).OfType<AstAtom>().Any(atom => atom.Spelling == "field")))))
            { yield return PropertySlot(property, owner); }
            foreach (var parameter in part.PrimaryConstructor?.Parameters.Where(parameter => parameter.IsProperty) ?? [])
            { yield return new(part.PrimaryConstructor!, owner, parameter.Name, ConstructionSlotKind.PrimaryProperty, parameter.IsMutable); }
            foreach (var entry in part.Bases.Where(entry => entry.Children.Any(child => child.SyntaxKind == OpenDot.Sol.Cst.NodeKind.Delegation)))
            { yield return new(entry, owner, entry.Origin.Text, ConstructionSlotKind.InterfaceDelegate, false); }
        }
    }

    private bool ConstructionReceiver(AstNode? expression, State state)
    {
        if (_constructionFrame is null) { return false; }
        if (expression is AstExpression value && _constructionReceiverValues.Contains(value)) { return true; }
        if (expression is AstSequence { IsGrouping: true } grouping)
        { return grouping.Elements.Any(element => ConstructionReceiver(element, state)); }
        if (expression is AstOperation { Operation: "as" or "as?" or "!!" or "?:" } operation)
        { return operation.Operands.Any(operand => ConstructionReceiver(operand, state)); }
        return (expression is null || expression is AstAtom { Spelling: "this" or "super" }) && _constructionFrame.OwnReceiver
            || expression is AstAtom atom && Sources.Lexical.Lookup(atom, atom.Spelling).Locals
                .Any(local => state.Construction.Aliases.Contains(local));
    }

    private bool ConstructionAlias(AstNode? expression, State state)
    {
        if (expression is null || _constructionFrame is null) { return false; }
        if (expression is AstExpression value && _constructionReceiverValues.Contains(value)) { return true; }
        if (ConstructionReceiver(expression, state)) { return true; }
        if (expression is AstSequence { IsGrouping: true } grouping)
        { return grouping.Elements.Any(element => ConstructionAlias(element, state)); }
        if (expression is AstOperation { Operation: "as" or "as?" or "!!" or "?:" } operation)
        { return operation.Operands.Any(operand => ConstructionAlias(operand, state)); }
        return false;
    }

    private void AssignConstructionSlot(ConstructionSlot slot, AstNode site, State state, bool initialization = false)
    {
        if (_constructionFrame is null || !state.Reachable) { return; }
        var before = CaptureCleanupFacts(state);
        var previous = state.Construction.Slots.GetValueOrDefault(slot);
        if (!slot.Mutable && previous != ConstructionAssignment.Unassigned)
        { Report("SOL2241", "A readonly construction slot may already be assigned.", site); }
        if (!slot.Mutable && !initialization && _constructionFrame.Helper)
        { Report("SOL2241", "A helper cannot initialize readonly storage.", site); }
        state.Construction = state.Construction with
        { Slots = state.Construction.Slots.SetItem(slot, ConstructionAssignment.Assigned) };
        if (slot.Declaration is AstProperty property) { state.Fields.Add(property); }
        UpdateCleanupFacts(state, before, CaptureCleanupFacts(state));
    }

    private void ReadConstructionSlot(ConstructionSlot slot, AstNode site, State state)
    {
        if (_constructionFrame is not null && state.Reachable
            && state.Construction.Slots.GetValueOrDefault(slot) != ConstructionAssignment.Assigned)
        {
            if (state.Construction.Slots.GetValueOrDefault(slot) == ConstructionAssignment.Unknown && _deferred.Count != 0)
            { Defer(site); }
            else { Report("SOL2271", "Construction reads unassigned storage: " + slot.Name, site); }
        }
    }

    private void ConstructionLocalAlias(LocalSymbol local, AstNode? value, State state)
    {
        if (_constructionFrame is null) { return; }
        var callback = ConstructionCallbackAt(value, state);
        state.Construction = state.Construction with
        { Aliases = ConstructionAlias(value, state) ? state.Construction.Aliases.Add(local)
            : state.Construction.Aliases.Remove(local),
            Callbacks = callback is null ? state.Construction.Callbacks.Remove(local) : state.Construction.Callbacks.SetItem(local, callback) };
    }

    private void ApplyConstructionAssignment(AstAssignment assignment, Value location, State state, Context context)
    {
        if (_constructionFrame is not { } frame) { return; }
        var receiver = (assignment.Location as AstMember)?.Receiver;
        if (location.Local is null && ConstructionAlias(assignment.Value, state))
        {
            if (ConstructionReceiver(receiver, state) || receiver is AstAtom { Spelling: "this" })
            { Defer(assignment); }
            else { Report("SOL2273", "Unpublished receiver cannot escape into externally reachable storage.", assignment); }
        }
        if (!ConstructionReceiver(receiver, state)) { return; }
        if (assignment.Location is AstAtom { Spelling: "field" } && context.Callable is AstAccessor accessor
            && ParentProperty(accessor) is { } backing)
        { AssignConstructionSlot(PropertySlot(backing, ContainingType(backing)!), assignment, state, accessor.Name == "init"); }
        else if (location.Symbol?.Parts[0] is AstProperty property)
        {
            var owner = location.Receiver ?? ContainingType(property) ?? frame.Owner;
            var setter = property.Accessors.FirstOrDefault(accessor => accessor.Name is "set" or "init");
            if (setter is { Body.Kind: not AstBodyKind.Missing })
            { ApplyConstructionAccessor(setter, owner, assignment, state); }
            else if (property.Delegate is not null && Delegate(property) is { Setter: { } protocol })
            { ApplyConstructionProtocol(protocol, assignment, state); }
            else if (ConstructionSlots(owner).Any(slot => slot.Declaration == property))
            { AssignConstructionSlot(PropertySlot(property, owner), assignment, state); }
        }
        else if (assignment.Location is AstMember member && location.Receiver is { } owner)
        {
            var slot = ConstructionSlots(owner).FirstOrDefault(slot => slot.Kind == ConstructionSlotKind.PrimaryProperty && slot.Name == member.Name);
            if (slot is not null) { AssignConstructionSlot(slot, assignment, state); }
        }
    }

    private AstParameter? FindConstructionPrimaryProperty(ResolvedType type, string name, AstNode site, out ResolvedType owner)
    {
        owner = type;
        var pending = new Queue<(ResolvedType Type, int Depth)>();
        var seen = new HashSet<int>();
        pending.Enqueue((type, 0));
        while (pending.TryDequeue(out var entry))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (entry.Depth >= 128) { Defer(site); return null; }
            var next = entry.Type;
            if (!seen.Add(next.Id)) { continue; }
            if (seen.Count > 8192) { Defer(site); return null; }
            var parameter = next.Declaration?.Parts.OfType<AstTypeDeclaration>().SelectMany(part => part.PrimaryConstructor?.Parameters ?? [])
                .FirstOrDefault(parameter => parameter.IsProperty && parameter.Name == name);
            if (parameter is not null) { owner = next; return parameter; }
            foreach (var parent in Types.GetDirectBases(next, _cancellation)) { pending.Enqueue((parent, entry.Depth + 1)); }
        }
        return null;
    }
}
