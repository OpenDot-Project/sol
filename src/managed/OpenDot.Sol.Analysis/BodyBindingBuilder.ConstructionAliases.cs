// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private readonly HashSet<AstExpression> _constructionReceiverValues = [];
    private bool _constructionReturnsReceiver;

    private bool CapturesConstructionReceiver(AstNode node, State state)
    {
        foreach (var atom in node.Descendants().OfType<AstAtom>())
        {
            _cancellation.ThrowIfCancellationRequested();
            if (ConstructionAlias(atom, state)) { return true; }
            var local = Sources.Lexical.Lookup(atom, atom.Spelling).Locals.FirstOrDefault();
            if (_constructionFrame?.OwnReceiver == true && local?.Kind == LocalSymbolKind.ConstructorProperty) { return true; }
        }
        return false;
    }

    private void MarkConstructionUnknown(State state)
    {
        if (_constructionFrame is not { } frame) { return; }
        var slots = state.Construction.Slots;
        foreach (var slot in ConstructionSlots(frame.Receiver))
        {
            if (slots.GetValueOrDefault(slot) != ConstructionAssignment.Assigned)
            { slots = slots.SetItem(slot, ConstructionAssignment.Unknown); }
        }
        state.Construction = state.Construction with { Slots = slots };
    }
}
