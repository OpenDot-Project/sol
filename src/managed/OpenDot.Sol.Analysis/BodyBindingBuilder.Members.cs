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
        var open = Types.Nominal(symbol, Sources.Types.GetTypeParameters(symbol.Parts[0]));
        return _bodySubstitution.Count == 0 ? open : Types.Substitute(open, _bodySubstitution, _cancellation);
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
        var result = MemberResolver.Resolve(receiver, name, location);
        if (result.Status == MemberLookupStatus.LimitExceeded)
        {
            Report("SOL2290", result.Reason ?? "Member inheritance search exceeded its budget.", location);
            Defer(location);
            return [];
        }
        return InterfaceMethodCandidates(receiver, name, location,
            result.Candidates.Select(candidate => new MemberBinding(candidate.Symbol, candidate.DeclaringType)));
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
            return owner is not null && current is not null && IsSameOrDerivedDeclaration(current, owner, location);
        }
        // One SourceAnalysis owns one distribution; internal cannot cross an external binary boundary here.
        return true;
    }

    private bool IsSameOrDerivedDeclaration(ResolvedType candidate, ResolvedType required, AstNode location)
    {
        if (candidate.Declaration == required.Declaration) { return true; }
        var pending = new Queue<(ResolvedType Type, int Depth)>();
        var visited = new HashSet<int>();
        pending.Enqueue((candidate, 0));
        while (pending.TryDequeue(out var current))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (current.Depth >= 128)
            {
                Defer(location);
                return false;
            }
            if (!visited.Add(current.Type.Id)) { continue; }
            if (visited.Count > 4096)
            {
                Defer(location);
                return false;
            }
            foreach (var parent in Types.GetDirectBases(current.Type, _cancellation))
            {
                if (parent.Declaration == required.Declaration) { return true; }
                pending.Enqueue((parent, current.Depth + 1));
            }
        }
        return false;
    }

    private bool ProtectedReceiverAllowed(ResolvedType? receiver, ResolvedType? owner,
        AstNode location)
    {
        var current = ContainingType(location);
        if (current is null || owner is null || !IsSameOrDerivedDeclaration(current, owner, location))
        { return false; }
        if (current.Declaration == owner.Declaration) { return true; }
        if (location is AstMember { Receiver: AstAtom { Spelling: "super" } } || IsQualifiedSuperUse(location)) { return true; }
        return receiver is not null && Converts(receiver, current);
    }

    private bool AccessorAccessible(AstAccessor accessor, AstProperty property,
        ResolvedType? accessReceiver, AstNode location, bool declarationAccessible)
    {
        if (!declarationAccessible) { return false; }
        var owner = ContainingType(property);
        if (accessor.Modifiers.Contains("private"))
        {
            return owner is not null && Ancestors(location).OfType<AstTypeDeclaration>()
                .Any(type => Sources.Declarations.GetDeclaredSymbol(type) == owner.Declaration);
        }
        if (accessor.Modifiers.Contains("protected"))
        {
            return ProtectedReceiverAllowed(accessReceiver, owner, location);
        }
        // An accessor with no modifier inherits its property's access. Internal/public access is
        // already bounded by declarationAccessible and this source analysis's distribution.
        return true;
    }

    private static bool HasGroupModifier(AstParameter parameter, string modifier) =>
        parameter.GroupOrigin.Element.Children.Any(child =>
            child.Kind == NodeKind.Modifier && child.Text == modifier);

    private bool IsWriteTarget(AstNode location) =>
        Ancestors(location).FirstOrDefault() is AstAssignment assignment
        && ReferenceEquals(assignment.Location, location);

    private Value SymbolValue(DeclarationSymbol symbol, ResolvedType? receiver, AstNode location,
        State state, Context context, bool read, ResolvedType? accessReceiver = null,
        ResolvedType? expected = null)
    {
        bool declarationAccessible = Accessible(symbol, location);
        if (!declarationAccessible) { Report("SOL2214", "The declaration is inaccessible here.", location); }
        if (symbol.Kind == SymbolKind.Property && symbol.Parts[0].Modifiers.Contains("protected")
            && accessReceiver is not null
            && !ProtectedReceiverAllowed(accessReceiver ?? receiver, ContainingType(symbol.Parts[0]), location)
            && declarationAccessible)
        {
            Report("SOL2214", "Protected access requires a receiver of the current derived type.", location);
            declarationAccessible = false;
        }
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
            var delegateContract = property.Delegate is null ? null : Delegate(property);
            if (property.Delegate is not null && delegateContract is null) { Defer(location); }
            var accessors = property.Accessors.ToArray();
            var getter = accessors.FirstOrDefault(a => a.Name == "get");
            var setter = accessors.FirstOrDefault(a => a.Name is "set" or "init");
            bool implicitAccessors = accessors.Length == 0;
            bool hasGetter = getter is not null || implicitAccessors;
            bool hasSetter = setter is not null || implicitAccessors && property.IsMutable;
            if (read && !hasGetter)
            {
                Defer(location);
                return new(null, Symbol: symbol, Receiver: receiver);
            }
            bool getterAccessible = getter is null || AccessorAccessible(getter, property,
                accessReceiver ?? receiver, location, declarationAccessible);
            if (read && getter is not null && !getterAccessible && declarationAccessible)
            { Report("SOL2214", "The getter is inaccessible here.", location); }
            if (read && getter is not null && getterAccessible)
            {
                var getterEffects = getter.ThrownTypes.Select(t => Specialize(t, receiver)).ToArray();
                RecordPotentialCallEdges(state, getterEffects);
                state.Effects.UnionWith(getterEffects);
            }
            if (read && getterAccessible && delegateContract is not null)
            {
                RecordPotentialCallEdges(state, delegateContract.GetEffects);
                state.Effects.UnionWith(delegateContract.GetEffects);
            }

            bool setterAccessible = setter is null || AccessorAccessible(setter, property,
                accessReceiver ?? receiver, location, declarationAccessible);
            if (IsWriteTarget(location) && setter is not null && !setterAccessible && declarationAccessible)
            { Report("SOL2214", "The setter is inaccessible here.", location); }
            bool writable = property.IsMutable && hasSetter && setterAccessible;
            if (context.Callable is AstConstructor && ContainingType(context.Callable)?.Declaration == ContainingType(property)?.Declaration
                && !state.Fields.Contains(property) && (location is not AstMember || location is AstMember { Receiver: AstAtom { Spelling: "this" } })) { writable = true; }
            if (!setterAccessible) { writable = false; }
            // A property never masquerades as addressable ref/out storage.
            return new(type, writable, false, Symbol: symbol, Receiver: receiver);
        }
        if (symbol.Parts[0] is AstFunction function)
        {
            var owner = Ancestors(function).OfType<AstTypeDeclaration>().FirstOrDefault();
            if (owner is not null && (owner.SyntaxKind != NodeKind.ObjectDeclaration
                || !GenericMethodSourcePolicy.IsSupportedStatelessObjectMethod(function, Sources)))
            {
                // Instance methods and stateful singleton methods need a receiver environment;
                // never publish their unbound signature as an ordinary code pointer.
                Defer(location);
                return new(null, Symbol: symbol, Receiver: receiver);
            }
            if (function.TypeParameters.Any())
            {
                return OverloadedFunctionValue(location, [symbol], expected);
            }
            var result = function.ReturnType is null ? CheckBody(function).ReturnType is { } inferred
                ? Types.Substitute(inferred, Substitution(receiver), _cancellation) : null : Specialize(function.ReturnType, receiver);
            if (result is null || function.Parameters.Any(p => p.Type is null) || symbol.Arity != 0) { return new(null, Symbol: symbol, Receiver: receiver); }
            var functionType = Types.GetFunction(function.Parameters.Select(p => p.PassingMode == AstPassingMode.Vararg
                    ? Types.GetArray(Specialize(p.Type!, receiver)) : Specialize(p.Type!, receiver)), result,
                function.Parameters.Select(p => p.PassingMode == AstPassingMode.Vararg ? AstPassingMode.Value : p.PassingMode),
                function.ThrownTypes.Select(t => Specialize(t, receiver)));
            ValidateFunctionEffects(functionType, location);
            return new(functionType, Symbol: symbol, Receiver: receiver);
        }
        Defer(location); return new(null, Symbol: symbol, Receiver: receiver);
    }

    private Value Member(AstMember member, State state, Context context, bool read, ResolvedType? expected = null)
    {
        if (Qualified(member) is { Length: > 0 } qualified)
        {
            if (qualified.Length != 1)
            {
                if (qualified.All(symbol => symbol.Kind == SymbolKind.Function))
                { return OverloadedFunctionValue(member, qualified, expected); }
                Report("SOL2212", "Ambiguous qualified value.", member);
                return new(null);
            }
            return SymbolValue(qualified[0], null, member, state, context, read, expected: expected);
        }
        var receiver = Expression(member.Receiver, state, context);
        if (receiver.Type is null || member.Name is null) { return new(null); }
        var type = receiver.Type;
        if (type.IsNullable)
        {
            if (!member.IsSafe) { Report("SOL2243", "Nullable receiver requires a check, safe access, or assertion.", member); }
            type = type.Arguments[0];
        }
        if (InterfacePropertyAccess(type, member.Name, member, state, context, read) is { } interfaceProperty)
        { return member.IsSafe && interfaceProperty.Type is not null ? interfaceProperty with
            { Type = Types.GetNullable(interfaceProperty.Type), Writable = false, Storage = false } : interfaceProperty; }
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
                bool accessible = true;
                if (HasGroupModifier(primary, "private") && ContainingType(member)?.Declaration != type.Declaration)
                {
                    Report("SOL2214", "The constructor property is private.", member);
                    accessible = false;
                }
                if (HasGroupModifier(primary, "protected")
                    && !ProtectedReceiverAllowed(type, type, member))
                {
                    Report("SOL2214", "Protected access requires a receiver of the current derived type.", member);
                    accessible = false;
                }
                return new(member.IsSafe ? Types.GetNullable(primaryType) : primaryType,
                    primary.IsMutable && accessible, false, Receiver: type);
            }
            Report("SOL2215", "No such member: " + member.Name, member); return new(null);
        }
        if (found.Length != 1)
        {
            if (found.Any(m => m.Symbol.Kind != SymbolKind.Function)) { Report("SOL2212", "Ambiguous member.", member); }
            else if (read)
            {
                var overloadedValue = OverloadedFunctionValue(member,
                    found.Select(candidate => candidate.Symbol).ToImmutableArray(), expected);
                return member.IsSafe && overloadedValue.Type is not null
                    ? overloadedValue with { Type = Types.GetNullable(overloadedValue.Type), Writable = false, Storage = false }
                    : overloadedValue;
            }
            return new(null);
        }
        var value = SymbolValue(found[0].Symbol, found[0].DeclaringType, member, state, context,
            read, accessReceiver: type, expected: expected);
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
                foreach (var pair in function.Parameters.Zip(original.Parameters))
                {
                    if (pair.First.IsEscaping != pair.Second.IsEscaping)
                    { Report("SOL2265", "An override must preserve each parameter's @escaping contract.", function); }
                }
                if (original.Modifiers.Contains("final") || !(original.Modifiers.Contains("open") || original.Modifiers.Contains("abstract")
                    || original.Modifiers.Contains("override") || ContainingType(original)?.Declaration?.Parts[0].SyntaxKind == NodeKind.InterfaceDeclaration))
                { Report("SOL2232", "The inherited member cannot be overridden.", function); }
                if (AccessRank(function) < AccessRank(original)) { Report("SOL2233", "Override cannot reduce accessibility.", function); }
                var overrideSubstitution = OverrideSubstitution(function, original, member.DeclaringType);
                if (function.ReturnType is not null && original.ReturnType is not null
                    && !Converts(Written(function.ReturnType), Types.Substitute(Written(original.ReturnType),
                        overrideSubstitution, _cancellation)))
                { Report("SOL2234", "Override return type is incompatible.", function); }
                var budget = original.ThrownTypes.Select(t => Types.Substitute(Written(t), overrideSubstitution, _cancellation)).ToArray();
                if (function.ThrownTypes.Select(Written).Where(IsChecked).Any(t => !budget.Any(b => Converts(t, b))))
                { Report("SOL2235", "Override cannot widen checked effects.", function); }
            }
        }
    }

    private bool SameParameters(AstFunction left, AstFunction right, ResolvedType receiver)
    {
        if (left.TypeParameters.Count() != right.TypeParameters.Count()) { return false; }
        var substitution = OverrideSubstitution(left, right, receiver);
        return left.Parameters.Zip(right.Parameters).All(pair => pair.First.Type is not null && pair.Second.Type is not null
            && pair.First.PassingMode == pair.Second.PassingMode && Written(pair.First.Type)
                == Types.Substitute(Written(pair.Second.Type), substitution, _cancellation));
    }

    private Dictionary<ResolvedType, ResolvedType> OverrideSubstitution(AstFunction overriding,
        AstFunction inherited, ResolvedType receiver)
    {
        var substitution = Substitution(receiver);
        var overridingParameters = Sources.Types.GetTypeParameters(overriding);
        var inheritedParameters = Sources.Types.GetTypeParameters(inherited);
        if (overridingParameters.Length != inheritedParameters.Length) { return substitution; }
        for (int index = 0; index < inheritedParameters.Length; index++)
        { substitution[inheritedParameters[index]] = overridingParameters[index]; }
        return substitution;
    }

    private static int AccessRank(AstDeclaration declaration) => declaration.Modifiers.Contains("private") ? 0
        : declaration.Modifiers.Contains("protected") ? 1 : declaration.Modifiers.Contains("internal") ? 2 : 3;
}
