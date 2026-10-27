// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Fir;

public static partial class FirVerifier
{
    private sealed partial class Checker
    {
        private Dictionary<object, AstSnapshot> _genericMethodFiles = new(ReferenceEqualityComparer.Instance);
        private Dictionary<AstFunction, GenericMethodOwnerIdentity?> _genericMethodOwnerCache = new(ReferenceEqualityComparer.Instance);
        private Dictionary<object, AstCall> _genericMethodSourceCalls = new(ReferenceEqualityComparer.Instance);
        private Dictionary<AstFunction, Dictionary<object, BodyCallBinding>> _genericMethodCallsByCaller = new(ReferenceEqualityComparer.Instance);

        private sealed record GenericMethodOwnerIdentity(AstTypeDeclaration Declaration,
            DeclarationSymbol Symbol, DeclarationSymbol MethodSymbol, bool IsCanonical);

        private sealed record GenericMethodOwnerInstance(int FunctionIndex, FirFunction Function,
            AstFunction Source, GenericMethodOwnerIdentity Owner);

        private sealed record GenericMethodInstanceKey(int OwnerSymbolId, object MethodOrigin, string TypeIds);

        private sealed class GenericMethodInstanceKeyComparer : IEqualityComparer<GenericMethodInstanceKey>
        {
            public bool Equals(GenericMethodInstanceKey? left, GenericMethodInstanceKey? right)
                => ReferenceEquals(left, right) || left is not null && right is not null
                    && left.OwnerSymbolId == right.OwnerSymbolId
                    && ReferenceEquals(left.MethodOrigin, right.MethodOrigin)
                    && left.TypeIds == right.TypeIds;

            public int GetHashCode(GenericMethodInstanceKey key)
                => HashCode.Combine(key.OwnerSymbolId, RuntimeHelpers.GetHashCode(key.MethodOrigin), key.TypeIds);
        }

        private void CheckGenericMethodOwnerInstanceGraph()
        {
            var sources = new Dictionary<object, AstFunction>(ReferenceEqualityComparer.Instance);
            _genericMethodFiles = new(ReferenceEqualityComparer.Instance);
            _genericMethodOwnerCache = new(ReferenceEqualityComparer.Instance);
            _genericMethodSourceCalls = new(ReferenceEqualityComparer.Instance);
            _genericMethodCallsByCaller = new(ReferenceEqualityComparer.Instance);
            foreach (var file in program.Analysis.Sources.Declarations.Files)
            {
                _genericMethodFiles.Add(file.Psi, file);
                foreach (var function in file.Nodes.OfType<AstFunction>())
                { sources.TryAdd(function.Origin.Element, function); }
                foreach (var call in file.Nodes.OfType<AstCall>())
                { _genericMethodSourceCalls.TryAdd(call.Origin.Element, call); }
            }
            IndexGenericMethodCallerBindings();
            var instances = new Dictionary<int, GenericMethodOwnerInstance>();
            var identities = new HashSet<GenericMethodInstanceKey>(new GenericMethodInstanceKeyComparer());

            for (int functionIndex = 0; functionIndex < program.Functions.Length; functionIndex++)
            {
                cancellation.ThrowIfCancellationRequested();
                var function = program.Functions[functionIndex];
                if (!sources.TryGetValue(function.Origin.Element, out var source)
                    || !program.Analysis.Sources.Types.GetTypeParameters(source).Any()) { continue; }
                var owner = GetGenericMethodOwner(source);
                if (owner is null) { continue; }

                if (!owner.IsCanonical)
                { Fail("Generic method owner instance is detached from its canonical declaration symbol.", function.Origin); }
                var typeParameters = program.Analysis.Sources.Types.GetTypeParameters(source);
                var arguments = function.SpecializationArguments;
                if (arguments.IsDefaultOrEmpty || arguments.Length != typeParameters.Length
                    || arguments.Any(argument => argument is null))
                {
                    Fail("Generic method owner graph contains an open or incomplete method instance.", function.Origin);
                    continue;
                }

                string typeIds = string.Join(",", arguments.Select(argument => argument.Id.ToString(CultureInfo.InvariantCulture)));
                var identity = new GenericMethodInstanceKey(owner.Symbol.Id, source.Origin.Element, typeIds);
                if (!identities.Add(identity))
                { Fail("Generic method owner graph contains a duplicate closed method instance.", function.Origin); }
                instances[functionIndex] = new(functionIndex, function, source, owner);
            }

            var incoming = new HashSet<int>();
            for (int callerIndex = 0; callerIndex < program.Functions.Length; callerIndex++)
            {
                cancellation.ThrowIfCancellationRequested();
                var caller = program.Functions[callerIndex];
                if (caller.Body is null || !sources.TryGetValue(caller.Origin.Element, out var callerSource)) { continue; }
                var sourceCalls = GetGenericMethodCallerBindings(caller, callerSource);
                if (sourceCalls is null) { continue; }

                foreach (var call in EnumerateGenericMethodCalls(caller.Body))
                {
                    if (call.Target < 0 || call.Target >= program.Functions.Length) { continue; }
                    var target = program.Functions[call.Target];
                    if (!sources.TryGetValue(target.Origin.Element, out var targetSource)
                        || !program.Analysis.Sources.Types.GetTypeParameters(targetSource).Any()) { continue; }
                    var targetOwner = GetGenericMethodOwner(targetSource);
                    if (targetOwner is null) { continue; }

                    if (!instances.TryGetValue(call.Target, out var instance))
                    {
                        Fail("Generic method call edge does not target a registered closed owner instance.", call.Origin);
                        continue;
                    }
                    incoming.Add(call.Target);
                    if (!targetOwner.IsCanonical || !ReferenceEquals(instance.Owner.Symbol, targetOwner.Symbol))
                    { Fail("Generic method call edge crosses its source owner's canonical identity.", call.Origin); }

                    if (!sourceCalls.TryGetValue(call.Origin.Element, out var binding))
                    { Fail("Generic method owner edge has no caller-instance source binding.", call.Origin); continue; }
                    if (binding.Target is not AstFunction selected || !ReferenceEquals(selected.Origin.Element, targetSource.Origin.Element))
                    { Fail("Generic method owner edge differs from the source-selected method declaration.", call.Origin); }

                    var selectedOwner = binding.Target is AstFunction selectedMethod
                        ? GetGenericMethodOwner(selectedMethod) : null;
                    if (selectedOwner is null || !selectedOwner.IsCanonical
                        || !ReferenceEquals(selectedOwner.Symbol, instance.Owner.Symbol)
                        || binding.Receiver is not { Kind: ResolvedTypeKind.Nominal } receiver
                        || !ReferenceEquals(receiver.Declaration, instance.Owner.Symbol))
                    { Fail("Generic method owner edge receiver differs from its specialized target owner.", call.Origin); }

                    if (call.TypeArguments.IsDefault
                        || !call.TypeArguments.SequenceEqual(binding.TypeArguments)
                        || !call.TypeArguments.SequenceEqual(instance.Function.SpecializationArguments))
                    { Fail("Generic method owner edge type arguments differ from its closed FIR instance.", call.Origin); }

                    if (!MatchesGenericMethodOwnerCall(call.Origin.Element, callerSource, targetSource, instance.Owner))
                    { Fail("Generic method owner edge is not a direct same-owner or singleton-qualified call.", call.Origin); }
                }
            }

            foreach (var instance in instances.Values)
            {
                if (!incoming.Contains(instance.FunctionIndex))
                { Fail("Generic method owner instance is orphaned from every source-bound FIR call edge.", instance.Function.Origin); }
            }
        }

        private Dictionary<object, BodyCallBinding>? GetGenericMethodCallerBindings(FirFunction caller, AstFunction source)
        {
            if (!caller.SpecializationArguments.IsEmpty)
            {
                try
                {
                    var view = program.Analysis.GetGenericBody(source, caller.SpecializationArguments, cancellation);
                    var specialized = new Dictionary<object, BodyCallBinding>(ReferenceEqualityComparer.Instance);
                    foreach (var binding in view.Calls)
                    {
                        if (!specialized.TryAdd(binding.Call.Origin.Element, binding))
                        { Fail("Generic method caller source view has duplicate call identities.", binding.Call.Origin); }
                    }
                    return specialized;
                }
                catch (Exception error) when (error is ArgumentException or TypeSpecializationException)
                {
                    Fail("Generic method owner graph could not obtain the caller's closed source view.", caller.Origin);
                    return null;
                }
            }

            return _genericMethodCallsByCaller.TryGetValue(source, out var indexed)
                ? indexed : new Dictionary<object, BodyCallBinding>(ReferenceEqualityComparer.Instance);
        }

        private void IndexGenericMethodCallerBindings()
        {
            foreach (var binding in program.Analysis.Calls)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!_genericMethodFiles.TryGetValue(binding.Call.Origin.Element.Snapshot, out var file)) { continue; }
                var caller = ContainingFunction(file, binding.Call);
                if (caller is null) { continue; }
                if (!_genericMethodCallsByCaller.TryGetValue(caller, out var bindings))
                {
                    bindings = new(ReferenceEqualityComparer.Instance);
                    _genericMethodCallsByCaller.Add(caller, bindings);
                }
                if (!bindings.TryAdd(binding.Call.Origin.Element, binding))
                { Fail("Generic method caller source view has duplicate call identities.", binding.Call.Origin); }
            }
        }

        private static AstFunction? ContainingFunction(AstSnapshot file, AstCall call)
        {
            for (var parent = file.GetParent(call); parent is not null; parent = file.GetParent(parent))
            { if (parent is AstFunction function) { return function; } }
            return null;
        }

        private GenericMethodOwnerIdentity? GetGenericMethodOwner(AstFunction method)
        {
            if (_genericMethodOwnerCache.TryGetValue(method, out var cached)) { return cached; }
            _genericMethodFiles.TryGetValue(method.Origin.Element.Snapshot, out var file);
            var owner = file?.GetParent(method);
            while (owner is not null && owner is not AstTypeDeclaration)
            { owner = file!.GetParent(owner); }
            if (file is null || owner is not AstTypeDeclaration declaration)
            {
                _genericMethodOwnerCache.Add(method, null);
                return null;
            }

            var ownerSymbol = program.Analysis.Sources.Declarations.GetDeclaredSymbol(declaration);
            var methodSymbol = program.Analysis.Sources.Declarations.GetDeclaredSymbol(method);
            bool nestedOwner = false;
            for (var parent = file.GetParent(declaration); parent is not null; parent = file.GetParent(parent))
            { nestedOwner |= parent is AstTypeDeclaration; }

            bool validParts = ownerSymbol is not null && !ownerSymbol.Parts.IsEmpty
                && ownerSymbol.Parts.All(part => part is AstTypeDeclaration type
                    && type.SyntaxKind == NodeKind.ObjectDeclaration && type.Arity == 0
                    && ReferenceEquals(program.Analysis.Sources.Declarations.GetDeclaredSymbol(type), ownerSymbol));
            bool canonical = ownerSymbol is { Kind: SymbolKind.Type, Arity: 0 }
                && methodSymbol is { Kind: SymbolKind.Function }
                && methodSymbol.Arity == program.Analysis.Sources.Types.GetTypeParameters(method).Length
                && ownerSymbol.Members.Any(member => ReferenceEquals(member, methodSymbol))
                && methodSymbol.Parts.Count(part => ReferenceEquals(part, method)) == 1
                && declaration.Members.Any(member => ReferenceEquals(member, method))
                && ownerSymbol.Parts.Any(part => ReferenceEquals(part, declaration))
                && validParts && !nestedOwner;
            var result = ownerSymbol is null || methodSymbol is null
                ? null : new GenericMethodOwnerIdentity(declaration, ownerSymbol, methodSymbol, canonical);
            _genericMethodOwnerCache.Add(method, result);
            return result;
        }

        private bool MatchesGenericMethodOwnerCall(object callOrigin, AstFunction caller,
            AstFunction target, GenericMethodOwnerIdentity targetOwner)
        {
            if (!_genericMethodSourceCalls.TryGetValue(callOrigin, out var call) || call.Target is null) { return false; }
            var targetExpression = call.Target is AstGenericApplication application ? application.Target : call.Target;
            if (targetExpression is AstAtom atom)
            {
                var callerOwner = GetGenericMethodOwner(caller);
                return atom.Spelling == target.Name && callerOwner is { IsCanonical: true }
                    && ReferenceEquals(callerOwner.Symbol, targetOwner.Symbol)
                    && !program.Analysis.Sources.Lexical.Lookup(atom, atom.Spelling).IsLocal;
            }
            if (targetExpression is not AstMember { IsSafe: false, Name: var name, Receiver: { } receiver }
                || name != target.Name) { return false; }

            string? firstQualifier = null;
            var current = receiver;
            while (current is AstMember { IsSafe: false, Name: { } memberName, Receiver: { } parent })
            {
                firstQualifier ??= memberName;
                current = parent;
            }
            return current is AstAtom { SyntaxKind: NodeKind.NameExpression } root
                && !program.Analysis.Sources.Lexical.Lookup(root, root.Spelling).IsLocal
                && (firstQualifier ?? root.Spelling) == targetOwner.Declaration.Name;
        }

        private IEnumerable<FirCall> EnumerateGenericMethodCalls(FirNode root)
        {
            var pending = new Stack<FirNode>();
            var visited = new HashSet<FirNode>(ReferenceEqualityComparer.Instance);
            pending.Push(root);
            while (pending.TryPop(out var node))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!visited.Add(node)) { continue; }
                if (visited.Count > 16_000_000)
                {
                    Fail("Generic method owner graph exceeded its 16,000,000-node verification limit.", node.Origin);
                    yield break;
                }
                if (node is FirCall call)
                {
                    yield return call;
                    foreach (var argument in call.Arguments) { pending.Push(argument.Value); }
                    continue;
                }
                switch (node)
                {
                    case FirBlock block: foreach (var statement in block.Statements) { pending.Push(statement); } break;
                    case FirDeclare { Initializer: { } declaredInitializer }: pending.Push(declaredInitializer); break;
                    case FirStore store: pending.Push(store.Value); break;
                    case FirEvaluate evaluate: pending.Push(evaluate.Value); break;
                    case FirReturn { Value: { } value }: pending.Push(value); break;
                    case FirIf conditional:
                        pending.Push(conditional.Condition); pending.Push(conditional.Then);
                        if (conditional.Else is { } otherwise) { pending.Push(otherwise); }
                        break;
                    case FirLoop loop:
                        if (loop.Initializer is { } loopInitializer) { pending.Push(loopInitializer); }
                        if (loop.Condition is { } condition) { pending.Push(condition); }
                        pending.Push(loop.Body);
                        if (loop.Update is { } update) { pending.Push(update); }
                        break;
                    case FirArrayForEach loop: pending.Push(loop.Array); pending.Push(loop.Body); break;
                    case FirIntrinsic intrinsic: foreach (var argument in intrinsic.Arguments) { pending.Push(argument); } break;
                    case FirUnary unary: pending.Push(unary.Value); break;
                    case FirBinary binary: pending.Push(binary.Left); pending.Push(binary.Right); break;
                    case FirArrayLength length: pending.Push(length.Array); break;
                    case FirArrayIndex index: pending.Push(index.Array); pending.Push(index.Index); break;
                    case FirArrayLiteral array: foreach (var element in array.Elements) { pending.Push(element); } break;
                }
            }
        }
    }
}
