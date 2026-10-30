// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    internal readonly Dictionary<int, ImmutableArray<InterfaceImplementation>> InterfaceImplementations = [];
    private readonly HashSet<int> _interfaceResolutionActive = [];

    private ImmutableArray<InterfaceImplementation> ResolveInterfaceImplementations(ResolvedType receiver, AstNode location)
    {
        _cancellation.ThrowIfCancellationRequested();
        Types.ValidateOwnership(receiver);
        if (!Files.ContainsKey(location)) { throw new ArgumentException("Interface selection requires an owned source location.", nameof(location)); }
        if (Sources.HasErrors) { Defer(location); return []; }
        if (InterfaceImplementations.TryGetValue(receiver.Id, out var cached)
            && cached.All(record => record.Status != BodyBindingStatus.Deferred)) { return cached; }
        if (!_interfaceResolutionActive.Add(receiver.Id)) { Defer(location); return []; }
        int gapsAtEntry = _deferred.Count;
        try
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<(ResolvedType Type, int Depth)>();
            var visited = new HashSet<int>();
            var owners = new List<ResolvedType>();
            pending.Enqueue((receiver, 0));
            while (pending.TryDequeue(out var next))
            {
                _cancellation.ThrowIfCancellationRequested();
                if (!visited.Add(next.Type.Id)) { continue; }
                owners.Add(next.Type);
                if (next.Depth > 128 || visited.Count > 8192)
                { Defer(location); return []; }
                foreach (var member in next.Type.Declaration?.Members ?? [])
                { if (!string.IsNullOrWhiteSpace(member.Name)) { names.Add(member.Name); } }
                foreach (var parent in _memberResolver.DirectBases(next.Type))
                { pending.Enqueue((parent, next.Depth + 1)); }
            }
            var results = ImmutableArray.CreateBuilder<InterfaceImplementation>();
            // Delegates are initialized even when every forwarded operation has an explicit override.
            _ = InterfaceForwarders(receiver, [], location).ToArray();
            foreach (var name in names.Order(StringComparer.Ordinal))
            {
                var lookup = _memberResolver.Resolve(receiver, name, location, retainContracts: true);
                if (lookup.Status == MemberLookupStatus.LimitExceeded) { Defer(location); return []; }
                if (!lookup.Candidates.Any(candidate => candidate.DeclaringType.Declaration?.Parts[0].SyntaxKind
                    == NodeKind.InterfaceDeclaration)) { continue; }
                var operations = lookup.Candidates.SelectMany(InterfaceOperations).ToArray();
                foreach (var group in operations.GroupBy(operation => operation.Signature))
                {
                    var contracts = group.ToImmutableArray();
                    var all = contracts.AddRange(PrimaryInterfaceProperties(owners, contracts));
                    if (!all.Any(IsInterfaceOperation)) { continue; }
                    var mostSpecific = all.Where(candidate => !all.Any(other =>
                        !ReferenceEquals(candidate, other) && IsInterfaceAncestor(candidate, other))).ToArray();
                    var explicitLocal = mostSpecific.Where(operation =>
                        ReferenceEquals(operation.Owner, receiver) && IsInterfaceOverride(operation)).ToArray();
                    var forwarders = explicitLocal.Length != 0 ? [] : InterfaceForwarders(receiver, all, location).ToArray();
                    var providers = explicitLocal.Length != 0 ? explicitLocal.Where(operation => operation.HasBody).ToArray()
                        : mostSpecific.Where(operation => operation.HasBody && !forwarders.Any(forwarder =>
                            IsInterfaceOperation(operation) && Converts(forwarder.DelegateInterface!, operation.Member.DeclaringType)))
                            .Concat(forwarders).ToArray();
                    var selected = providers.Length == 1 ? providers[0] : null;
                    bool abstractOwner = receiver.Kind == ResolvedTypeKind.Parameter
                        || receiver.Declaration?.Parts[0].SyntaxKind == NodeKind.InterfaceDeclaration
                        || receiver.Declaration?.Parts[0].Modifiers.Contains("abstract") == true;
                    var status = providers.Length > 1 ? BodyBindingStatus.Invalid
                        : selected is null && !abstractOwner ? BodyBindingStatus.Invalid : BodyBindingStatus.Bound;
                    string? reason = providers.Length > 1 ? "Competing interface implementations require an explicit override."
                        : selected is null && !abstractOwner ? "A concrete type must implement every interface operation." : null;
                    if (status == BodyBindingStatus.Invalid)
                    { Report(providers.Length > 1 ? "SOL2236" : "SOL2237", reason!, location); }
                    if (selected is not null)
                    {
                        if (selected.ValueType is null && selected.Body is AstFunction { Body.Kind: AstBodyKind.Expression } inferred
                            && contracts.Any(contract => contract.ValueType is not null))
                        { selected = selected with { ValueType = SpecializeInferredInterfaceReturn(inferred, selected.Owner) }; }
                        bool valid = selected.ProofStatus != BodyBindingStatus.Invalid;
                        if ((ReferenceEquals(selected.Owner, receiver) || all.Where(IsInterfaceOperation)
                                .Any(contract => Converts(selected.Owner, contract.Owner)))
                            && !IsInterfaceOperation(selected) && selected.DelegateExpression is null
                            && !IsInterfaceOverride(selected))
                        { Report("SOL2231", "An inherited signature requires explicit override.", selected.SourceOrigin); valid = false; }
                        foreach (var contract in contracts)
                        { valid &= ValidateInterfaceOperation(selected, contract, location); }
                        if (!valid)
                        { status = BodyBindingStatus.Invalid; reason = "Interface implementation violates its inherited contracts."; }
                        else if (selected.ProofStatus == BodyBindingStatus.Deferred)
                        { status = BodyBindingStatus.Deferred; reason = "Interface delegate initialization evidence is incomplete."; }
                    }
                    results.Add(new(receiver, group.Key, contracts, selected, status, reason));
                }
            }
            foreach (var propertyGroup in results.Where(record => record.Contracts[0].Kind != InterfaceOperationKind.Method)
                .GroupBy(record => InterfaceAccessorKey(record.Contracts[0])))
            {
                var types = propertyGroup.Select(record => record.Implementation?.ValueType ?? record.Contracts[0].ValueType)
                    .Where(type => type is not null).Distinct().ToArray();
                if (types.Length <= 1) { continue; }
                Report("SOL2239", "Merged interface getter/setter value types are incompatible.", location);
                foreach (var record in propertyGroup.ToArray())
                {
                    int index = results.IndexOf(record);
                    results[index] = record with { Status = BodyBindingStatus.Invalid, Reason = "Incompatible accessor value types." };
                }
            }
            var completed = results.ToImmutable();
            if (_deferred.Count != gapsAtEntry)
            {
                completed = completed.Select(record => record.Status == BodyBindingStatus.Invalid ? record
                : record with { Status = BodyBindingStatus.Deferred, Reason = "Interface implementation evidence is incomplete." }).ToImmutableArray();
            }
            InterfaceImplementations[receiver.Id] = completed;
            return completed;
        }
        finally { _interfaceResolutionActive.Remove(receiver.Id); }
    }

    private static bool IsInterfaceOperation(InterfaceOperation operation) =>
        operation.PrimaryProperty is null && operation.Member.DeclaringType.Declaration!.Parts[0].SyntaxKind == NodeKind.InterfaceDeclaration;

    private static bool IsInterfaceOverride(InterfaceOperation operation) => operation.PrimaryProperty is { } parameter
        ? HasGroupModifier(parameter, "override") : operation.Member.Symbol.Parts[0].Modifiers.Contains("override");

    private IEnumerable<InterfaceOperation> PrimaryInterfaceProperties(IEnumerable<ResolvedType> owners,
        ImmutableArray<InterfaceOperation> contracts)
    {
        var contract = contracts.FirstOrDefault(IsInterfaceOperation);
        if (contract?.Member.Symbol.Parts[0] is not AstProperty) { yield break; }
        foreach (var owner in owners)
        {
            foreach (var part in owner.Declaration?.Parts.OfType<AstTypeDeclaration>() ?? [])
            {
                foreach (var parameter in part.PrimaryConstructor?.Parameters ?? [])
                {
                    if (!parameter.IsProperty || parameter.Name != contract.Member.Symbol.Name
                        || contract.Kind == InterfaceOperationKind.Set && !parameter.IsMutable) { continue; }
                    if (parameter.Type is null) { Defer(part); continue; }
                    yield return contract with
                    {
                        PrimaryOwner = owner,
                        PrimaryProperty = parameter,
                        Body = null,
                        StorageSource = ParameterGroupNode(parameter),
                        ValueType = Specialize(parameter.Type, owner),
                        Effects = [],
                        HasBody = true
                    };
                }
            }
        }
    }

    private static string InterfaceAccessorKey(InterfaceOperation operation) => operation.Signature
        .Replace(":Get:", ":Accessor:", StringComparison.Ordinal).Replace(":Set:", ":Accessor:", StringComparison.Ordinal);

    private static bool IsInterfaceAncestor(InterfaceOperation ancestor, InterfaceOperation derived) =>
        !ReferenceEquals(ancestor.Owner, derived.Owner)
        && derived.Owner.Declaration != ancestor.Owner.Declaration
        && ancestor.Member.InheritancePaths.Any(path => path.Take(path.Length - 1)
            .Any(type => ReferenceEquals(type, derived.Owner)));

    private IEnumerable<InterfaceOperation> InterfaceOperations(ResolvedMemberCandidate member)
    {
        var declaration = member.Symbol.Parts[0];
        if (declaration is AstFunction function)
        {
            var parameters = function.Parameters.Select(parameter => parameter.Type is null ? Types.GetBottom()
                : Specialize(parameter.Type, member.DeclaringType)).ToImmutableArray();
            string signature = "method:" + function.Name + "`" + function.TypeParameters.Count() + ":"
                + string.Join(",", parameters.Select((type, index) => function.Parameters[index].PassingMode + ":" + InterfaceTypeKey(type, function)));
            yield return new(member, InterfaceOperationKind.Method, function,
                function.ReturnType is not null ? Specialize(function.ReturnType, member.DeclaringType)
                    : function.Body.Kind == AstBodyKind.Expression ? null : Empty, parameters,
                function.ThrownTypes.Select(type => Specialize(type, member.DeclaringType)).ToImmutableArray(),
                signature, function.Body.Kind != AstBodyKind.Missing);
            yield break;
        }
        var accessors = declaration switch
        {
            AstProperty property => property.Accessors.ToArray(),
            AstIndexer indexer => indexer.Accessors.ToArray(),
            _ => null,
        };
        if (accessors is null) { yield break; }
        var valueSyntax = declaration is AstProperty p ? p.Type : ((AstIndexer)declaration).ValueType;
        var value = valueSyntax is null ? null : Specialize(valueSyntax, member.DeclaringType);
        var indices = declaration is AstIndexer ix ? ix.Parameters.Select(parameter => parameter.Type is null
            ? Types.GetBottom() : Specialize(parameter.Type, member.DeclaringType)).ToImmutableArray() : [];
        foreach (var kind in new[] { InterfaceOperationKind.Get, InterfaceOperationKind.Set })
        {
            var accessor = accessors.FirstOrDefault(item => kind == InterfaceOperationKind.Get ? item.Name == "get" : item.Name == "set");
            bool implicitAccessor = accessors.Length == 0 && declaration is AstProperty property
                && (kind == InterfaceOperationKind.Get || property.IsMutable);
            if (accessor is null && !implicitAccessor) { continue; }
            bool concrete = member.DeclaringType.Declaration!.Parts[0].SyntaxKind != NodeKind.InterfaceDeclaration;
            string signature = (declaration is AstIndexer ? "index:" : "property:" + declaration.Name + ":")
                + kind + ":" + string.Join(",", indices.Select((type, index) =>
                    (declaration is AstIndexer ixDeclaration ? ixDeclaration.Parameters[index].PassingMode + ":" : "") + InterfaceTypeKey(type)));
            yield return new(member, kind, accessor, value, indices,
                accessor?.ThrownTypes.Select(type => Specialize(type, member.DeclaringType)).ToImmutableArray() ?? [],
                signature, accessor?.Body.Kind != AstBodyKind.Missing && accessor is not null
                    || concrete && !declaration.Modifiers.Contains("abstract") && declaration is AstProperty
                        && (implicitAccessor || accessors.All(item => item.Body.Kind == AstBodyKind.Missing)));
        }
    }

    private string InterfaceTypeKey(ResolvedType type, AstCallable? method = null) => type.Kind == ResolvedTypeKind.Parameter
        && method is not null && ReferenceEquals(type.Parameter?.Owner, method) ? "M" + type.Parameter!.Ordinal
        : type.Kind == ResolvedTypeKind.Parameter ? "P" + type.Id
        : type.Kind + ":" + type.Name + ":" + type.Declaration?.Id + "["
            + string.Join(",", type.Arguments.Select(argument => InterfaceTypeKey(argument, method))) + "](" + string.Join(",", type.ParameterModes)
            + ")throws[" + string.Join(",", type.Effects.Select(effect => InterfaceTypeKey(effect, method)).Order(StringComparer.Ordinal)) + "]";

    private ResolvedType? SpecializeInferredInterfaceReturn(AstFunction function, ResolvedType owner)
    {
        var result = CheckBody(function).ReturnType;
        return result is null ? null : Types.Substitute(result, Substitution(owner), _cancellation);
    }

    private bool ValidateInterfaceOperation(InterfaceOperation implementation, InterfaceOperation contract, AstNode location)
    {
        bool valid = true;
        if (implementation.PrimaryProperty is { } parameter
            && (HasGroupModifier(parameter, "private") || HasGroupModifier(parameter, "protected") || HasGroupModifier(parameter, "internal")))
        { Report("SOL2233", "An interface constructor property cannot reduce accessibility.", implementation.SourceOrigin); valid = false; }
        var substitutions = new Dictionary<ResolvedType, ResolvedType>();
        if (implementation.Body is AstFunction actualMethod && contract.Body is AstFunction contractMethod)
        {
            var actualParameters = Sources.Types.GetTypeParameters(actualMethod);
            var contractParameters = Sources.Types.GetTypeParameters(contractMethod);
            for (int index = 0; index < contractParameters.Length; index++)
            { substitutions[contractParameters[index]] = actualParameters[index]; }
            if (actualMethod.Parameters.Zip(contractMethod.Parameters).Any(pair => pair.First.IsEscaping != pair.Second.IsEscaping))
            { Report("SOL2265", "Interface implementations must preserve @escaping contracts.", location); valid = false; }
        }
        if (implementation.ValueType is { } actual && contract.ValueType is { } expected)
        {
            expected = Types.Substitute(expected, substitutions, _cancellation);
            bool compatible = implementation.Kind == InterfaceOperationKind.Method
                ? Converts(actual, expected) : ReferenceEquals(actual, expected);
            if (!compatible)
            {
                Report(implementation.Kind == InterfaceOperationKind.Method ? "SOL2234" : "SOL2239",
                "Interface implementation has an incompatible result or property type.", location); valid = false;
            }
        }
        if (AccessRank(implementation.Member.Symbol.Parts[0]) < AccessRank(contract.Member.Symbol.Parts[0]))
        { Report("SOL2233", "Interface implementation cannot reduce accessibility.", location); valid = false; }
        if (implementation.Body is AstAccessor accessor && accessor.Modifiers.Any(modifier => modifier is "private" or "protected" or "internal")
            && AccessRank(accessor) < AccessRank(contract.Member.Symbol.Parts[0]))
        { Report("SOL2233", "An interface implementation accessor cannot reduce accessibility.", location); valid = false; }
        if (implementation.Effects.Where(IsChecked).Any(effect => !contract.Effects.Any(budget =>
            Converts(effect, Types.Substitute(budget, substitutions, _cancellation)))))
        { Report("SOL2235", "Interface implementation cannot widen checked effects.", location); valid = false; }
        var modifiers = contract.Member.Symbol.Parts[0].Modifiers;
        if (!ReferenceEquals(implementation.Owner, contract.Owner)
            && (modifiers.Contains("final") || !IsInterfaceOperation(contract)
                && !modifiers.Any(modifier => modifier is "open" or "abstract" or "override")))
        { Report("SOL2232", "The inherited operation cannot be overridden.", location); valid = false; }
        return valid;
    }

    private void ValidateInterfaceImplementations()
    {
        foreach (var declaration in Files.Keys.OfType<AstTypeDeclaration>())
        {
            _cancellation.ThrowIfCancellationRequested();
            var receiver = ContainingDeclaredType(declaration);
            if (receiver is not null) { ResolveInterfaceImplementations(receiver, declaration); }
        }
    }

    private ResolvedType? ContainingDeclaredType(AstTypeDeclaration declaration) =>
        Sources.Declarations.GetDeclaredSymbol(declaration) is { } symbol
            ? Types.Nominal(symbol, Sources.Types.GetTypeParameters(symbol.Parts[0])) : null;
}
