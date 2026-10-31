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
    private sealed record DelegateInitialization(ResolvedType? Type, ImmutableArray<ResolvedType> Effects, BodyBindingStatus Status)
    { internal DelegateContract? PropertyContract { get; init; } }
    private readonly Dictionary<(int Receiver, AstNode Entry), DelegateInitialization> _interfaceDelegateValues = [];
    private IEnumerable<InterfaceOperation> InterfaceForwarders(ResolvedType receiver,
        ImmutableArray<InterfaceOperation> contracts, AstNode location)
    {
        var owners = new Queue<ResolvedType>();
        var visited = new HashSet<int>();
        owners.Enqueue(receiver);
        while (owners.TryDequeue(out var owner))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (!visited.Add(owner.Id)) { continue; }
            foreach (var parent in Types.GetDirectBases(owner, _cancellation).Where(type =>
                type.Declaration?.Parts[0].SyntaxKind != NodeKind.InterfaceDeclaration))
            { owners.Enqueue(parent); }
            foreach (var part in owner.Declaration?.Parts.OfType<AstTypeDeclaration>() ?? [])
            {
                foreach (var entry in part.Bases)
                {
                    var expression = entry.Children.FirstOrDefault(node => node.SyntaxKind == NodeKind.Delegation)
                        ?.Children.OfType<AstExpression>().FirstOrDefault();
                    var syntax = entry.Children.OfType<AstType>().FirstOrDefault();
                    if (expression is null || syntax is null) { continue; }
                    var delegatedInterface = InterfaceWritten(syntax, owner);
                    if (delegatedInterface.Declaration?.Parts[0].SyntaxKind != NodeKind.InterfaceDeclaration)
                    { Report("SOL2280", "Interface delegation must target an interface.", entry); continue; }
                    var matched = contracts.Where(operation => IsInterfaceOperation(operation)
                        && Converts(delegatedInterface, operation.Member.DeclaringType)).ToArray();
                    var contract = matched.FirstOrDefault(operation => !matched.Any(other =>
                        !ReferenceEquals(operation, other) && IsInterfaceAncestor(operation, other)));
                    if (contracts.Any(operation => !IsInterfaceOperation(operation)
                        && !ReferenceEquals(operation.Owner, owner)
                        && Converts(operation.Owner, owner))) { continue; }
                    if (!_interfaceDelegateValues.TryGetValue((owner.Id, entry), out var initialization))
                    {
                        initialization = AnalyzeInterfaceInitialization(owner, entry, expression, delegatedInterface);
                        _interfaceDelegateValues.Add((owner.Id, entry), initialization);
                    }
                    if (initialization.Status != BodyBindingStatus.Bound) { Defer(location); }
                    if (initialization.Type is null) { Defer(entry); continue; }
                    if (!Converts(initialization.Type, delegatedInterface))
                    { Report("SOL2281", "The delegate value does not implement its declared interface.", entry); continue; }
                    if (expression is AstAtom { Spelling: "this" }
                        || expression.Descendants().OfType<AstAtom>().Any(atom => atom.Spelling == "this"))
                    { Defer(entry); }
                    if (contract is null) { continue; }
                    yield return contract with
                    {
                        HasBody = true,
                        DelegateExpression = expression,
                        DelegateInterface = delegatedInterface,
                        DelegateTargetType = initialization.Type,
                        DelegateOwner = owner,
                        ProofStatus = initialization.Status,
                        InitializationEffects = initialization.Effects
                    };
                }
            }
        }
    }
}
