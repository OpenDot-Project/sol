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
    private ResolvedType? ContainingType(AstNode node)
    {
        var declaration = Ancestors(node).OfType<AstTypeDeclaration>().FirstOrDefault();
        if (declaration is null || Sources.Declarations.GetDeclaredSymbol(declaration) is not { } symbol) { return null; }
        return Types.Nominal(symbol, Sources.Types.GetTypeParameters(symbol.Parts[0]));
    }

    private Dictionary<ResolvedType, ResolvedType> Substitution(ResolvedType? receiver)
    {
        var result = new Dictionary<ResolvedType, ResolvedType>();
        if (receiver?.Declaration is { } symbol)
        {
            var parameters = Sources.Types.GetTypeParameters(symbol.Parts[0]);
            for (int index = 0; index < parameters.Length; index++) { result.Add(parameters[index], receiver.Arguments[index]); }
        }
        return result;
    }

    private ResolvedType Specialize(AstType type, ResolvedType? receiver) => Types.Substitute(Written(type), Substitution(receiver), _cancellation);

    private IEnumerable<MemberBinding> FindMembers(ResolvedType receiver, string name, AstNode location)
    {
        if (string.IsNullOrWhiteSpace(name)) { Defer(location); return []; }
        var pending = new Queue<(ResolvedType Type, int Depth)>();
        var visited = new HashSet<ResolvedType>();
        var found = new List<MemberBinding>();
        pending.Enqueue((receiver, 0));
        int bestDepth = int.MaxValue;
        while (pending.TryDequeue(out var entry))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (entry.Depth > 128) { Report("SOL2290", "Member inheritance search exceeds the depth budget.", location); Defer(location); break; }
            if (entry.Depth > bestDepth || !visited.Add(entry.Type)) { continue; }
            if (entry.Type.Kind == ResolvedTypeKind.Parameter && entry.Type.Parameter is { } identity)
            {
                var parameter = identity.Owner switch
                {
                    AstCallable c => c.TypeParameters.ElementAtOrDefault(identity.Ordinal),
                    AstTypeDeclaration t => t.TypeParameters.ElementAtOrDefault(identity.Ordinal),
                    _ => null,
                };
                if (parameter?.Constraint is not null) { pending.Enqueue((Written(parameter.Constraint), entry.Depth + 1)); }
                foreach (var clause in identity.Owner.Children.Where(n => n.SyntaxKind == NodeKind.WhereClause).SelectMany(n => n.Children))
                {
                    if (clause.Children.Any(n => n.SyntaxKind == NodeKind.Name && n.Origin.Text == parameter?.Name))
                    { foreach (var type in clause.Children.OfType<AstType>()) { pending.Enqueue((Written(type), entry.Depth + 1)); } }
                }
            }
            var members = entry.Type.Declaration?.FindMembers(name).ToArray() ?? [];
            if (members.Length != 0)
            {
                bestDepth = entry.Depth;
                found.AddRange(members.Select(s => new MemberBinding(s, entry.Type)));
            }
            foreach (var parent in Types.GetDirectBases(entry.Type, _cancellation)) { pending.Enqueue((parent, entry.Depth + 1)); }
        }
        return found.Distinct();
    }

    private bool Accessible(DeclarationSymbol symbol, AstNode location)
    {
        var declaration = symbol.Parts[0];
        if (declaration.Modifiers.Contains("private"))
        {
            var owner = ContainingType(declaration)?.Declaration;
            return owner is null ? symbol.ModuleName == Files[location].Root.ModuleName
                : Ancestors(location).OfType<AstTypeDeclaration>().Any(t => Sources.Declarations.GetDeclaredSymbol(t) == owner);
        }
        if (declaration.Modifiers.Contains("protected"))
        {
            var owner = ContainingType(declaration);
            var current = ContainingType(location);
            return owner is not null && current is not null && (current.Declaration == owner.Declaration || Converts(current, owner));
        }
        // One SourceAnalysis owns one distribution; internal cannot cross an external binary boundary here.
        return true;
    }

    private Value SymbolValue(DeclarationSymbol symbol, ResolvedType? receiver, AstNode location, State state, Context context, bool read)
    {
        if (!Accessible(symbol, location)) { Report("SOL2214", "The declaration is inaccessible here.", location); }
        if (symbol.Kind == SymbolKind.Type)
        {
            if (symbol.Arity != 0) { Defer(location); return new(null, Symbol: symbol, IsType: true); }
            var type = Types.Nominal(symbol, []);
            return new(type, Symbol: symbol, Receiver: type, IsType: symbol.Parts[0].SyntaxKind != NodeKind.ObjectDeclaration);
        }
        if (symbol.Parts[0] is AstProperty property)
        {
            ConstructorRead(property, location, state, context, read);
            ResolvedType? type = property.Type is null ? null : Specialize(property.Type, receiver);
            if (type is null && property.Initializer is { } initializer)
            {
                // Cyclic property inference is not resolved by repeatedly walking initializers.
                Defer(location);
                if (initializer is AstAtom { SyntaxKind: NodeKind.LiteralExpression }) { type = Expression(initializer, state, context).Type; }
            }
            if (property.Delegate is not null)
            {
                var contract = Delegate(property);
                if (contract is null) { Defer(location); }
                else if (read) { state.Effects.UnionWith(contract.GetEffects); }
            }
            var getter = property.Accessors.FirstOrDefault(a => a.Name == "get");
            var setter = property.Accessors.FirstOrDefault(a => a.Name == "set");
            if (read && getter?.Modifiers.Contains("private") == true && ContainingType(location)?.Declaration != ContainingType(property)?.Declaration)
            { Report("SOL2214", "The getter is inaccessible here.", location); }
            if (read && getter is not null) { state.Effects.UnionWith(getter.ThrownTypes.Select(t => Specialize(t, receiver))); }
            bool writable = property.IsMutable;
            if (context.Callable is AstConstructor && ContainingType(context.Callable)?.Declaration == ContainingType(property)?.Declaration
                && !state.Fields.Contains(property) && (location is not AstMember || location is AstMember { Receiver: AstAtom { Spelling: "this" } })) { writable = true; }
            if (setter?.Modifiers.Contains("private") == true && ContainingType(location)?.Declaration != ContainingType(property)?.Declaration) { writable = false; }
            if (setter?.Modifiers.Contains("protected") == true
                && (ContainingType(location) is not { } own || ContainingType(property) is not { } owner || !Converts(own, owner))) { writable = false; }
            // A property never masquerades as addressable ref/out storage.
            return new(type, writable, false, Symbol: symbol, Receiver: receiver);
        }
        if (symbol.Parts[0] is AstFunction function)
        {
            var result = function.ReturnType is null ? CheckBody(function).ReturnType is { } inferred
                ? Types.Substitute(inferred, Substitution(receiver), _cancellation) : null : Specialize(function.ReturnType, receiver);
            if (result is null || function.Parameters.Any(p => p.Type is null) || symbol.Arity != 0) { return new(null, Symbol: symbol, Receiver: receiver); }
            return new(Types.GetFunction(function.Parameters.Select(p => p.PassingMode == AstPassingMode.Vararg
                    ? Types.GetArray(Specialize(p.Type!, receiver)) : Specialize(p.Type!, receiver)), result,
                function.Parameters.Select(p => p.PassingMode == AstPassingMode.Vararg ? AstPassingMode.Value : p.PassingMode),
                function.ThrownTypes.Select(t => Specialize(t, receiver))), Symbol: symbol, Receiver: receiver);
        }
        Defer(location); return new(null, Symbol: symbol, Receiver: receiver);
    }

    private Value Member(AstMember member, State state, Context context, bool read)
    {
        if (Qualified(member) is { Length: > 0 } qualified)
        {
            if (qualified.Length != 1) { Report("SOL2212", "Ambiguous qualified value.", member); return new(null); }
            return SymbolValue(qualified[0], null, member, state, context, read);
        }
        var receiver = Expression(member.Receiver, state, context);
        if (receiver.Type is null || member.Name is null) { return new(null); }
        var type = receiver.Type;
        if (type.IsNullable)
        {
            if (!member.IsSafe) { Report("SOL2243", "Nullable receiver requires a check, safe access, or assertion.", member); }
            type = type.Arguments[0];
        }
        if (type.Kind is ResolvedTypeKind.Array or ResolvedTypeKind.FixedArray && member.Name == "length")
        { return new(member.IsSafe ? Types.GetNullable(Types.GetPrimitive("int")) : Types.GetPrimitive("int")); }
        var found = ReceiverMembers(type, member.Name, member, receiver.IsType).ToArray();
        if (found.Length == 0)
        {
            var primary = type.Declaration?.Parts.OfType<AstTypeDeclaration>().SelectMany(t => t.PrimaryConstructor?.Parameters ?? [])
                .FirstOrDefault(p => p.IsProperty && p.Name == member.Name);
            if (primary?.Type is not null)
            {
                var primaryType = Specialize(primary.Type, type);
                if (primary.GroupOrigin.Element.Text.StartsWith("private", StringComparison.Ordinal)
                    && ContainingType(member)?.Declaration != type.Declaration) { Report("SOL2214", "The constructor property is private.", member); }
                return new(member.IsSafe ? Types.GetNullable(primaryType) : primaryType, primary.IsMutable, false, Receiver: type);
            }
            Report("SOL2215", "No such member: " + member.Name, member); return new(null);
        }
        if (found.Length != 1)
        {
            if (found.Any(m => m.Symbol.Kind != SymbolKind.Function)) { Report("SOL2212", "Ambiguous member.", member); }
            return new(null);
        }
        var value = SymbolValue(found[0].Symbol, found[0].DeclaringType, member, state, context, read);
        return member.IsSafe && value.Type is not null ? value with { Type = Types.GetNullable(value.Type), Writable = false, Storage = false } : value;
    }

    private IEnumerable<MemberBinding> ReceiverMembers(ResolvedType type, string name, AstNode location, bool typeQualified)
    {
        if (!typeQualified) { return FindMembers(type, name, location); }
        var companion = type.Declaration?.Members.FirstOrDefault(m => m.Parts[0] is AstTypeDeclaration { IsCompanion: true });
        return companion is null ? [] : FindMembers(Types.Nominal(companion, []), name, location);
    }

    private ImmutableArray<DeclarationSymbol>? Qualified(AstMember member)
    {
        if (string.IsNullOrWhiteSpace(member.Name)) { return null; }
        var pieces = new Stack<string>();
        AstExpression? current = member.Receiver;
        while (current is AstMember { IsSafe: false, Name: not null } part) { pieces.Push(part.Name); current = part.Receiver; }
        if (current is not AstAtom root || root.SyntaxKind != NodeKind.NameExpression || string.IsNullOrWhiteSpace(root.Spelling)
            || !Sources.Lexical.Lookup(root, root.Spelling).IsMissing) { return null; }
        pieces.Push(root.Spelling);
        var candidates = Sources.Declarations.LookupQualified(Files[member].Root, string.Join('.', pieces), member.Name ?? "").Candidates;
        return candidates.IsEmpty ? null : candidates;
    }

    private void ValidateOverrides()
    {
        foreach (var function in Files.Keys.OfType<AstFunction>())
        {
            _cancellation.ThrowIfCancellationRequested();
            var owner = ContainingType(function);
            if (owner is null) { continue; }
            var inherited = Types.GetDirectBases(owner, _cancellation).SelectMany(t => FindMembers(t, function.Name ?? "", function))
                .Where(m => m.Symbol.Parts[0] is AstFunction f && f.Parameters.Length == function.Parameters.Length)
                .Where(m => SameParameters(function, (AstFunction)m.Symbol.Parts[0], m.DeclaringType)).Distinct().ToArray();
            if (function.Modifiers.Contains("override") && inherited.Length == 0) { Report("SOL2230", "Override has no matching base member.", function); }
            foreach (var member in inherited)
            {
                var original = (AstFunction)member.Symbol.Parts[0];
                if (!function.Modifiers.Contains("override")) { Report("SOL2231", "An inherited signature requires explicit override.", function); }
                if (original.Modifiers.Contains("final") || !(original.Modifiers.Contains("open") || original.Modifiers.Contains("abstract")
                    || original.Modifiers.Contains("override") || ContainingType(original)?.Declaration?.Parts[0].SyntaxKind == NodeKind.InterfaceDeclaration))
                { Report("SOL2232", "The inherited member cannot be overridden.", function); }
                if (AccessRank(function) < AccessRank(original)) { Report("SOL2233", "Override cannot reduce accessibility.", function); }
                if (function.ReturnType is not null && original.ReturnType is not null
                    && !Converts(Written(function.ReturnType), Specialize(original.ReturnType, member.DeclaringType)))
                { Report("SOL2234", "Override return type is incompatible.", function); }
                var budget = original.ThrownTypes.Select(t => Specialize(t, member.DeclaringType)).ToArray();
                if (function.ThrownTypes.Select(Written).Where(IsChecked).Any(t => !budget.Any(b => Converts(t, b))))
                { Report("SOL2235", "Override cannot widen checked effects.", function); }
            }
        }
    }

    private bool SameParameters(AstFunction left, AstFunction right, ResolvedType receiver)
    {
        if (left.TypeParameters.Count() != right.TypeParameters.Count()) { return false; }
        var substitution = Substitution(receiver);
        var a = Sources.Types.GetTypeParameters(left); var b = Sources.Types.GetTypeParameters(right);
        for (int index = 0; index < b.Length; index++) { substitution[b[index]] = a[index]; }
        return left.Parameters.Zip(right.Parameters).All(pair => pair.First.Type is not null && pair.Second.Type is not null
            && pair.First.PassingMode == pair.Second.PassingMode && Written(pair.First.Type)
                == Types.Substitute(Written(pair.Second.Type), substitution, _cancellation));
    }
    private static int AccessRank(AstDeclaration declaration) => declaration.Modifiers.Contains("private") ? 0
        : declaration.Modifiers.Contains("protected") ? 1 : declaration.Modifiers.Contains("internal") ? 2 : 3;
}
