// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private HashSet<(int Owner, AstNode Site)> _interfaceInitializationActive = [];

    private void ValidateInterfaceDelegateConstruction(ResolvedType constructed, AstCall call)
    {
        var pending = new Queue<(ResolvedType Type, int Depth)>();
        var visited = new HashSet<int>();
        bool hasDelegation = false;
        pending.Enqueue((constructed, 0));
        while (pending.TryDequeue(out var current))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (!visited.Add(current.Type.Id)) { continue; }
            if (current.Depth > 128 || visited.Count > 8192) { Defer(call); return; }
            hasDelegation |= current.Type.Declaration?.Parts.OfType<AstTypeDeclaration>()
                .SelectMany(part => part.Bases).Any(entry => entry.Children.Any(child => child.SyntaxKind == NodeKind.Delegation)) == true;
            foreach (var parent in Types.GetDirectBases(current.Type, _cancellation))
            { pending.Enqueue((parent, current.Depth + 1)); }
        }
        if (!hasDelegation) { return; }
        // The existing construction boundary does not certify interface target storage/publication.
        Defer(call);
        if (_interfaceResolutionActive.Contains(constructed.Id)) { return; }
        var records = ResolveInterfaceImplementations(constructed, call);
        if (records.Any(record => record.Status == BodyBindingStatus.Invalid))
        { Report("SOL2236", "Construction requires valid interface implementations and delegate initialization.", call); }
        else if (_interfaceDelegateValues.Any(pair => visited.Contains(pair.Key.Receiver) && pair.Value.Status == BodyBindingStatus.Invalid))
        { Report("SOL2250", "Interface delegate initialization is invalid for this constructed owner.", call); }
    }

    private DelegateInitialization AnalyzeInterfaceInitialization(ResolvedType owner, AstNode site,
        AstExpression expression, ResolvedType? expected = null, AstProperty? property = null)
    {
        _cancellation.ThrowIfCancellationRequested();
        var key = (owner.Id, site);
        if (_interfaceInitializationActive.Count >= 128 || !_interfaceInitializationActive.Add(key))
        { Defer(site); return new(null, [], BodyBindingStatus.Deferred); }
        try
        {
            // Initializer facts belong to the constructed owner, not the method currently being checked.
            // Keep its expression/call/local facts in a private workspace to avoid cross-instance overwrite.
            var worker = new BodyBindingBuilder(Sources, Types, _cancellation)
            {
                ClosedMemberReturn = ClosedMemberReturn,
                ClosedOwnerReturnTemplate = ClosedOwnerReturnTemplate,
                ClosedLexicalReturn = ClosedLexicalReturn,
                InterfaceSourceBody = callable => Bodies.GetValueOrDefault(callable) ?? InterfaceSourceBody?.Invoke(callable),
                _interfaceInitializationActive = _interfaceInitializationActive,
                _constructionSession = _constructionSession,
                ConstructionSourceStatus = ConstructionSourceStatus,
                _bodySubstitution = new(_bodySubstitution),
            };
            foreach (var pair in Substitution(owner)) { worker._bodySubstitution[pair.Key] = pair.Value; }
            worker._interfaceResolutionActive.UnionWith(_interfaceResolutionActive);
            worker._interfaceResolutionActive.Add(owner.Id);
            var state = new State();
            foreach (var local in Sources.Lexical.Scopes.SelectMany(scope => scope.Symbols)
                .Where(local => local.Kind is LocalSymbolKind.Parameter or LocalSymbolKind.ConstructorProperty))
            {
                if (local.PassingMode != AstPassingMode.Out) { state.Assigned.Add(local); }
                if (local.WrittenType is { } written) { worker._localTypes[local] = worker.Written(written); }
            }
            var contract = property is null ? null : worker.Delegate(property);
            var value = property is null ? worker.Expression(expression, state, new(null, expected), expected)
                : new Value(contract?.ValueType);
            var effects = contract?.InitEffects ?? state.Effects.ToImmutableArray();
            bool invalid = worker.Diagnostics.Count != 0;
            bool incomplete = worker._deferred.Count != 0 || value.Type is null
                || expression is AstAtom { Spelling: "this" }
                || expression.Descendants().OfType<AstAtom>().Any(atom => atom.Spelling == "this");
            foreach (var diagnostic in worker.Diagnostics)
            {
                var origin = worker._reported.FirstOrDefault(pair => pair.Item2 == diagnostic.Code
                    && pair.Item1.Origin.Span == diagnostic.Span && ReferenceEquals(pair.Item1.Origin.Source, diagnostic.Source)).Item1 ?? site;
                Report(diagnostic.Code, diagnostic.Message, origin);
            }
            if (incomplete) { Defer(site); }
            return new(value.Type, effects, invalid ? BodyBindingStatus.Invalid
                : incomplete ? BodyBindingStatus.Deferred : BodyBindingStatus.Bound) { PropertyContract = contract };
        }
        finally { _interfaceInitializationActive.Remove(key); }
    }
}
