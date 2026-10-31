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
    private Value InterfaceIndex(AstExpression expression, ResolvedType receiver, State state, Context context, bool read)
    {
        var arguments = ImmutableArray.CreateBuilder<AstArgument>();
        foreach (var node in expression.Children.FirstOrDefault(node => node.SyntaxKind == NodeKind.ArgumentList)?.Children ?? [])
        {
            if (node is AstExpression direct)
            { arguments.Add(new(null, AstArgumentMode.Value, direct, direct.Origin)); continue; }
            var value = node.Children.LastOrDefault();
            if (value is null) { continue; }
            var mode = value.SyntaxKind switch
            {
                NodeKind.RefArgument => AstArgumentMode.Ref,
                NodeKind.OutArgument => AstArgumentMode.Out,
                NodeKind.OutVariable => AstArgumentMode.OutVariable,
                NodeKind.SpreadArgument => AstArgumentMode.Spread,
                _ => AstArgumentMode.Value,
            };
            if (mode is AstArgumentMode.Ref or AstArgumentMode.Out or AstArgumentMode.Spread)
            { value = value.Children.FirstOrDefault() ?? value; }
            arguments.Add(new(node.Children.FirstOrDefault(child => child.SyntaxKind == NodeKind.Name)?.Origin.Text,
                mode, value, node.Origin));
        }
        var values = arguments.Select(argument => argument.Mode == AstArgumentMode.OutVariable
            || argument.Value is AstLambda or AstLabelledExpression or AstSequence { SyntaxKind: NodeKind.ArrayExpression } ? new Value(null)
            : InterfaceIndexArgumentValue(argument, state, context)).ToArray();
        var allRecords = SourceIndexerOperations(receiver, expression);
        var records = allRecords.Where(record =>
            record.Contracts[0].Member.Symbol.Parts[0] is AstIndexer
            && record.Contracts[0].Kind == (read ? InterfaceOperationKind.Get : InterfaceOperationKind.Set)).ToArray();
        var applicable = new List<(InterfaceImplementation Record, InterfaceOperation Operation, ArgumentMapping Mapping, int[] Ranks)>();
        bool incompleteDefaults = false;
        foreach (var record in records)
        {
            var operation = record.Implementation ?? record.Contracts[0];
            var indexer = (AstIndexer)operation.Member.Symbol.Parts[0];
            var defaults = Sources.Defaults.GetDefaults(indexer);
            if (!defaults.IsComplete) { incompleteDefaults = true; continue; }
            var mapping = CallArgumentMapper.Map(arguments.ToImmutable(), indexer, defaults, _cancellation);
            if (mapping.Error is not null) { continue; }
            var ranks = new List<int>();
            bool valid = true;
            foreach (var entry in mapping.Arguments)
            {
                var value = values[entry.ArgumentIndex];
                var expected = operation.ParameterTypes[entry.ParameterIndex];
                if (entry.IsArrayArgument || entry.IsSpread) { expected = Types.GetArray(expected); }
                if (arguments[entry.ArgumentIndex].Mode == AstArgumentMode.OutVariable) { ranks.Add(0); continue; }
                if (arguments[entry.ArgumentIndex].Value is AstLambda or AstLabelledExpression or AstSequence { SyntaxKind: NodeKind.ArrayExpression })
                {
                    _ = Speculate(state, () => Expression(arguments[entry.ArgumentIndex].Value as AstExpression,
                        state.Clone(), context, expected), out bool invalid);
                    if (invalid) { valid = false; break; }
                    ranks.Add(0); continue;
                }
                int rank = value.Type is null ? -1 : ConversionRank(value.Type, expected);
                if (value.Literal is { } literal && expected.Kind == ResolvedTypeKind.Primitive)
                { rank = !CallBindingBuilder.NumericLiteralFits(literal, expected) ? -1
                    : ReferenceEquals(value.Type, expected) ? 0 : 1; }
                if (indexer.Parameters[entry.ParameterIndex].PassingMode is AstPassingMode.Ref or AstPassingMode.Out)
                { rank = value.Storage && value.Writable && ReferenceEquals(value.Type, expected) ? 0 : -1; }
                if (rank < 0) { valid = false; break; }
                ranks.Add(rank);
            }
            if (valid) { applicable.Add((record, operation, mapping, ranks.ToArray())); }
        }
        if (incompleteDefaults) { Defer(expression); return new(null); }
        var best = applicable.Where(candidate => !applicable.Any(other => other.Record != candidate.Record
            && BetterInterfaceIndex(other.Mapping, other.Ranks, candidate.Mapping, candidate.Ranks))).ToArray();
        if (best.Length != 1)
        { Report(best.Length == 0 ? "SOL2221" : "SOL2222", "Indexer has no unique applicable interface operation.", expression); return new(null); }
        var selected = best[0];
        foreach (var entry in selected.Mapping.Arguments)
        {
            var argument = arguments[entry.ArgumentIndex];
            if (argument.Mode == AstArgumentMode.OutVariable
                || values[entry.ArgumentIndex].Type is not null && values[entry.ArgumentIndex].Literal is null) { continue; }
            var expected = selected.Operation.ParameterTypes[entry.ParameterIndex];
            if (entry.IsArrayArgument || entry.IsSpread) { expected = Types.GetArray(expected); }
            values[entry.ArgumentIndex] = Expression(argument.Value as AstExpression, state, context, expected);
        }
        ValidateInterfaceAccess(selected.Record, expression, IsQualifiedSuperUse(expression));
        if (IsQualifiedSuperUse(expression) && selected.Record.Implementation is null)
        { Report("SOL2238", "Qualified super requires a concrete indexer accessor.", expression); Defer(expression); }
        if (selected.Record.Status != BodyBindingStatus.Bound)
        {
            if (selected.Record.Status == BodyBindingStatus.Invalid)
            { Report("SOL2236", selected.Record.Reason ?? "The interface indexer is unresolved.", expression); }
            Defer(expression);
        }
        var setters = allRecords.Where(record =>
            record.Contracts[0].Kind == InterfaceOperationKind.Set
            && record.Contracts[0].Member.Symbol.Parts[0] is AstIndexer)
            .Where(record => InterfaceAccessorKey(record.Contracts[0]) == InterfaceAccessorKey(selected.Operation)).ToArray();
        if ((!read || IsWriteTarget(expression)) && setters.Length == 1)
        {
            _interfaceWrites[expression] = setters[0];
            _interfaceIndexArguments[expression] = new(arguments.ToImmutable(), values, selected.Mapping, selected.Operation.ParameterTypes);
        }
        if (read)
        {
            ApplyInterfaceIndexEffects(new(arguments.ToImmutable(), values, selected.Mapping, selected.Operation.ParameterTypes),
                state, selected.Operation.Effects);
            ApplyConstructionIndexAccessor(selected.Operation, expression, state);
        }
        return new(selected.Operation.ValueType, setters.Length == 1 && setters[0].Status == BodyBindingStatus.Bound, false,
            Symbol: selected.Operation.Member.Symbol, Receiver: selected.Operation.Member.DeclaringType);
    }

    private ImmutableArray<InterfaceImplementation> SourceIndexerOperations(ResolvedType receiver, AstNode site)
    {
        var records = _interfaceResolutionActive.Contains(receiver.Id) ? [] : ResolveInterfaceImplementations(receiver, site);
        var covered = records.SelectMany(record => record.Contracts).Select(operation => operation.Member.Symbol).ToHashSet();
        var lookup = _memberResolver.Resolve(receiver, "this", site);
        if (lookup.Status == MemberLookupStatus.LimitExceeded) { Defer(site); return records; }
        var own = lookup.Candidates.Where(member => member.Symbol.Parts[0] is AstIndexer
            && member.DeclaringType.Declaration?.Parts[0].SyntaxKind != NodeKind.InterfaceDeclaration
            && !covered.Contains(member.Symbol)).SelectMany(InterfaceOperations).Select(operation =>
                new InterfaceImplementation(receiver, operation.Signature, [operation], operation,
                    operation.HasBody ? BodyBindingStatus.Bound : BodyBindingStatus.Deferred, null));
        return records.AddRange(own);
    }

    private void ApplyConstructionIndexAccessor(InterfaceOperation operation, AstNode site, State state)
    {
        if (_constructionFrame is null || site.Children.OfType<AstExpression>().FirstOrDefault() is not { } receiver
            || !ConstructionReceiver(receiver, state)) { return; }
        if (operation.Body is AstAccessor accessor)
        { ApplyConstructionAccessor(accessor, operation.Owner, site, state); }
        else { Defer(site); MarkConstructionUnknown(state); }
    }

    private static bool BetterInterfaceIndex(ArgumentMapping left, int[] leftRanks, ArgumentMapping right, int[] rightRanks)
    {
        if (leftRanks.Zip(rightRanks).Any(pair => pair.First > pair.Second)) { return false; }
        if (leftRanks.Zip(rightRanks).Any(pair => pair.First < pair.Second)) { return true; }
        if (left.UsesVararg != right.UsesVararg) { return !left.UsesVararg; }
        return left.Defaults.Length < right.Defaults.Length;
    }

    private Value InterfaceIndexArgumentValue(AstArgument argument, State state, Context context)
    {
        if (argument.Value is AstAtom { SyntaxKind: NodeKind.LiteralExpression } literal
            && literal.Origin.Element.Tokens.FirstOrDefault()?.Kind is SyntaxKind.IntegerLiteralToken or SyntaxKind.FloatLiteralToken)
        {
            return new(Types.GetPrimitive(literal.Origin.Element.Tokens.First().Kind == SyntaxKind.IntegerLiteralToken ? "int" : "double"),
                Literal: literal.Spelling);
        }
        var value = Expression(argument.Value as AstExpression, state, context, read: argument.Mode != AstArgumentMode.Out);
        return argument.Mode is AstArgumentMode.Ref or AstArgumentMode.Out && value.Local is { } local
            ? value with { Type = LocalType(local) } : value;
    }
}
