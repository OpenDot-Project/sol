// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Psi;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private const string EscapingAnnotationName = "escaping";

    private void ValidateEscapingParameterAnnotations(AstNode owner, IEnumerable<AstParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            _cancellation.ThrowIfCancellationRequested();
            var annotations = parameter.Annotations.Where(annotation => annotation.Origin.Element
                    is PsiAnnotation { Name: EscapingAnnotationName })
                .ToArray();
            if (annotations.Length == 0) { continue; }

            if (annotations.Length > 1)
            {
                Report("SOL2263", "A parameter may declare @escaping only once.", annotations[1]);
                continue;
            }

            var annotation = (PsiAnnotation)annotations[0].Origin.Element;
            if (annotation.Target is not (null or "parameter"))
            {
                Report("SOL2263", "@escaping is valid only on a parameter target.", annotations[0]);
                continue;
            }
            if (annotation.Arguments.Any())
            {
                Report("SOL2263", "@escaping does not accept annotation arguments.", annotations[0]);
                continue;
            }
            if (parameter.PassingMode != AstPassingMode.Value
                || parameter.Type is null || ExpectedLambdaFunction(Written(parameter.Type)) is null)
            {
                Report("SOL2263", "@escaping requires a by-value function-typed parameter.", annotations[0]);
            }
        }
    }

    private bool IsFunctionValueParameter(AstParameter parameter)
        => parameter.PassingMode == AstPassingMode.Value && parameter.Type is not null
            && ExpectedLambdaFunction(Written(parameter.Type)) is not null;

    private bool ReportEscapingClosureArguments(AstCall call, IEnumerable<Target> targets)
    {
        int before = Diagnostics.Count;
        foreach (var target in targets)
        {
            _cancellation.ThrowIfCancellationRequested();
            var mapping = CallArgumentMapper.Map(call, target.Callable, Sources.Defaults.GetDefaults(target.Callable),
                _cancellation);
            if (mapping.Error is not null) { continue; }
            foreach (var entry in mapping.Arguments)
            {
                if (entry.ParameterIndex >= target.Callable.Parameters.Length
                    || !target.Callable.Parameters[entry.ParameterIndex].IsEscaping) { continue; }
                AstExpression? expression = call.Arguments[entry.ArgumentIndex].Value as AstExpression;
                while (expression is AstSequence { IsGrouping: true } grouping
                    && grouping.Elements.Take(2).ToArray() is [var child]) { expression = child; }
                AstNode? closure = expression switch
                {
                    AstLabelledExpression labelled => labelled.Lambda,
                    AstLambda or AstAnonymousFunction => expression,
                    _ => null,
                };
                if (closure is null || IsProvenNonEscaping(closure)) { continue; }
                var sourceCallable = NearestCallableOwner(call);
                switch (closure)
                {
                    case AstLambda lambda:
                        foreach (var capture in Sources.Lexical.GetLambdaCaptures(lambda))
                        {
                            bool byref = sourceCallable is not null && IsBorrowedLocal(capture.Local, sourceCallable);
                            var type = LocalType(capture.Local);
                            if (!byref && (type is null || !MayContainBorrow(type))) { continue; }
                            foreach (var reference in capture.References)
                            { Report("SOL2262", "An escaping closure cannot capture ref/out storage or a ref-like value.", reference); }
                        }
                        var lambdaReceivers = Sources.Lexical.GetLambdaReceiverCaptures(lambda);
                        CheckRefLikeReceiverCaptures(lambda, lambdaReceivers.Select(receiver => receiver.Owner),
                            lambdaReceivers.Select(receiver => receiver.References));
                        break;
                    case AstAnonymousFunction function:
                        foreach (var capture in Sources.Lexical.GetAnonymousFunctionCaptures(function))
                        {
                            bool byref = sourceCallable is not null && IsBorrowedLocal(capture.Local, sourceCallable);
                            var type = LocalType(capture.Local);
                            if (!byref && (type is null || !MayContainBorrow(type))) { continue; }
                            foreach (var reference in capture.References)
                            { Report("SOL2262", "An escaping closure cannot capture ref/out storage or a ref-like value.", reference); }
                        }
                        var anonymousReceivers = Sources.Lexical.GetAnonymousFunctionReceiverCaptures(function);
                        CheckRefLikeReceiverCaptures(function, anonymousReceivers.Select(receiver => receiver.Owner),
                            anonymousReceivers.Select(receiver => receiver.References));
                        break;
                }
            }
        }
        return Diagnostics.Count != before;
    }

    private static bool IsBorrowedLocal(LocalSymbol local, AstNode callable)
    {
        if (local.PassingMode is AstPassingMode.Ref or AstPassingMode.Out) { return true; }
        return callable switch
        {
            AstCallable declaration => declaration.Parameters.Any(parameter =>
                parameter.PassingMode is AstPassingMode.Ref or AstPassingMode.Out
                && ReferenceEquals(parameter.NameOrigin.Element, local.Origin.Element)),
            AstAnonymousFunction function => function.Parameters.Any(parameter =>
                parameter.PassingMode is AstPassingMode.Ref or AstPassingMode.Out
                && ReferenceEquals(parameter.NameOrigin.Element, local.Origin.Element)),
            _ => false,
        };
    }

    private void ValidateNonescapingFunctionParameterUses()
    {
        IEqualityComparer<AstAtom> atomComparer = ReferenceEqualityComparer.Instance;
        var bindings = Sources.Lexical.Bindings.ToDictionary(binding => binding.Reference, atomComparer);
        var allLocals = Sources.Lexical.Scopes.SelectMany(scope => scope.Symbols).ToArray();
        foreach (var callable in Files.Keys.OfType<AstCallable>())
        {
            ValidateNonescapingFunctionParameters(callable, callable.Parameters, allLocals, bindings);
        }
        foreach (var function in Files.Keys.OfType<AstAnonymousFunction>())
        {
            ValidateNonescapingFunctionParameters(function, function.Parameters, allLocals, bindings);
        }
    }

    private void ValidateNonescapingFunctionParameters(AstNode owner, IEnumerable<AstParameter> parameters,
        LocalSymbol[] allLocals, Dictionary<AstAtom, NameBinding> bindings)
    {
        _cancellation.ThrowIfCancellationRequested();
        var ownerLocals = allLocals.Where(local => Sources.Lexical.Scopes.Any(scope =>
            ReferenceEquals(scope.Owner, owner) && scope.Symbols.Contains(local,
                ReferenceEqualityComparer.Instance))).ToArray();
        foreach (var parameter in parameters.Where(parameter => !parameter.IsEscaping
            && IsFunctionValueParameter(parameter)))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (parameter.IsProperty)
            {
                Report("SOL2264", "A function-valued constructor property is escaping and must declare @escaping.",
                    ParameterGroupNode(parameter));
                continue;
            }
            var parameterLocal = ownerLocals.FirstOrDefault(local => local.Kind == LocalSymbolKind.Parameter
                && ReferenceEquals(local.Origin.Element, parameter.NameOrigin.Element));
            if (parameterLocal is null) { Defer(owner); continue; }
            ValidateNonescapingParameterUses(owner, parameter, parameterLocal, allLocals, bindings);
        }
    }

    private void ValidateNonescapingParameterUses(AstNode owner, AstParameter parameter,
        LocalSymbol parameterLocal, LocalSymbol[] allLocals, Dictionary<AstAtom, NameBinding> bindings)
    {
        var aliases = new HashSet<LocalSymbol>(ReferenceEqualityComparer.Instance) { parameterLocal };
        var aliasValueReferences = new HashSet<AstAtom>(ReferenceEqualityComparer.Instance);
        bool changed;
        do
        {
            changed = false;
            foreach (var property in Files.Keys.OfType<AstProperty>().Where(property =>
                ReferenceEquals(NearestCallableOwner(property), owner) && property.Initializer is not null))
            {
                if (property.BindingNames.Count() != 1
                    || !TryReferencedLocal(property.Initializer!, bindings, out var source)
                    || !aliases.Contains(source)) { continue; }
                var target = allLocals.FirstOrDefault(local => local.Kind == LocalSymbolKind.Variable
                    && local.Name == property.BindingNames.Single()
                    && ReferenceEquals(local.Origin.Element, property.Origin.Element));
                if (target is null) { continue; }
                if (aliases.Add(target)) { changed = true; }
                if (TrySourceAtom(property.Initializer!, bindings, out var atom)) { aliasValueReferences.Add(atom); }
            }

            foreach (var assignment in Files.Keys.OfType<AstAssignment>().Where(assignment =>
                assignment.Operation == "=" && assignment.Location is not null && assignment.Value is not null
                && ReferenceEquals(NearestCallableOwner(assignment), owner)))
            {
                if (!TryReferencedLocal(assignment.Value!, bindings, out var source) || !aliases.Contains(source)
                    || !TryReferencedLocal(assignment.Location!, bindings, out var target)
                    || target.Kind != LocalSymbolKind.Variable) { continue; }
                if (aliases.Add(target)) { changed = true; }
                if (TrySourceAtom(assignment.Value!, bindings, out var atom)) { aliasValueReferences.Add(atom); }
            }
        }
        while (changed);

        foreach (var binding in bindings.Values.Where(binding => binding.Lookup.Locals.Any(aliases.Contains)))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (FindEnclosingCallableBoundary(binding.Reference, owner, Files[binding.Reference]) is { } nestedBoundary
                && !((nestedBoundary is AstLambda or AstAnonymousFunction) && IsProvenNonEscaping(nestedBoundary)))
            {
                Report("SOL2264", "A function parameter without @escaping cannot be captured by a callable that may escape.",
                    binding.Reference);
                continue;
            }
            if (aliasValueReferences.Contains(binding.Reference)) { continue; }
            if (IsDefinitelyOverwrittenAliasAtUse(binding.Reference, owner, aliases, bindings)) { continue; }
            if (IsWriteOnlyLocalReference(binding.Reference, Files[binding.Reference])) { continue; }
            if (IsDirectInvocation(binding.Reference, Files[binding.Reference])) { continue; }
            if (IsNullComparisonUse(binding.Reference, Files[binding.Reference])) { continue; }
            if (TryCallArgument(binding.Reference, Files[binding.Reference], out var call, out int argumentIndex))
            {
                if (Calls.TryGetValue(call, out var callBinding))
                {
                    var mapping = callBinding.Arguments.FirstOrDefault(argument => argument.ArgumentIndex == argumentIndex);
                    if (mapping is not null && mapping.ParameterIndex >= 0
                        && mapping.ParameterIndex < callBinding.Target.Parameters.Length
                        && !callBinding.Target.Parameters[mapping.ParameterIndex].IsEscaping
                        && IsFunctionValueParameter(callBinding.Target.Parameters[mapping.ParameterIndex]))
                    { continue; }
                }
                Report("SOL2264", "A nonescaping function parameter cannot be passed to an unknown or escaping consumer.", binding.Reference);
                continue;
            }

            Report("SOL2264", "A function parameter without @escaping cannot be returned, stored outside a local alias, or captured by an escaping closure.", binding.Reference);
        }
    }

    private bool IsDefinitelyOverwrittenAliasAtUse(AstAtom reference, AstNode owner,
        HashSet<LocalSymbol> aliases, Dictionary<AstAtom, NameBinding> bindings)
    {
        if (!bindings.TryGetValue(reference, out var useBinding)) { return false; }
        var file = Files[reference];
        var block = NearestOwnedBlock(reference, owner, file);
        if (block is null) { return false; }
        var useStatement = DirectChildOfBlock(reference, block, file);
        int useIndex = StatementIndex(block, useStatement);
        if (useStatement is null || useIndex < 0) { return false; }

        foreach (var local in useBinding.Lookup.Locals.Where(aliases.Contains))
        {
            if (MayStillAliasAfter(block, useIndex, local, true, owner, aliases, bindings, file))
            { return false; }
        }
        return useBinding.Lookup.Locals.Any(aliases.Contains);
    }

    private bool MayStillAliasAfter(AstBlock block, int statementLimit, LocalSymbol local, bool mayAlias,
        AstNode owner, HashSet<LocalSymbol> aliases, Dictionary<AstAtom, NameBinding> bindings,
        AstSnapshot file)
    {
        for (int index = 0; index < statementLimit; index++)
        {
            _cancellation.ThrowIfCancellationRequested();
            mayAlias = AliasStateAfter(block.Statements[index], local, mayAlias, owner, aliases, bindings, file);
        }
        return mayAlias;
    }

    private bool AliasStateAfter(AstNode statement, LocalSymbol local, bool mayAlias, AstNode owner,
        HashSet<LocalSymbol> aliases, Dictionary<AstAtom, NameBinding> bindings, AstSnapshot file)
    {
        if (statement is AstAssignment assignment && assignment.Operation == "="
            && assignment.Location is { } location && assignment.Value is { } value
            && ReferenceEquals(NearestCallableOwner(assignment), owner)
            && TryReferencedLocal(location, bindings, out var assigned)
            && ReferenceEquals(assigned, local))
        { return ReferencesAnyAlias(value, aliases, bindings); }

        if (statement is AstProperty property && property.Initializer is { } initializer
            && PropertyLocal(property, aliases) is { } declared && ReferenceEquals(declared, local))
        { return ReferencesAnyAlias(initializer, aliases, bindings); }

        if (statement is not AstIf conditional || conditional.Then is not { } thenBlock)
        { return mayAlias; }

        bool thenMayAlias = MayStillAliasAfter(thenBlock, thenBlock.Statements.Length, local,
            mayAlias, owner, aliases, bindings, file);
        bool elseMayAlias = conditional.Else switch
        {
            AstBlock elseBlock => MayStillAliasAfter(elseBlock, elseBlock.Statements.Length, local,
                mayAlias, owner, aliases, bindings, file),
            AstIf elseIf => AliasStateAfter(elseIf, local, mayAlias, owner, aliases, bindings, file),
            _ => mayAlias,
        };
        return thenMayAlias || elseMayAlias;
    }

    private static LocalSymbol? PropertyLocal(AstProperty property, HashSet<LocalSymbol> aliases)
    {
        if (property.BindingNames.Count() != 1) { return null; }
        string name = property.BindingNames.Single();
        return aliases.FirstOrDefault(local => local.Kind == LocalSymbolKind.Variable && local.Name == name
            && local.Origin.Span.Start >= property.Origin.Span.Start
            && local.Origin.Span.End <= property.Origin.Span.End);
    }

    private static bool ReferencesAnyAlias(AstNode expression, HashSet<LocalSymbol> aliases,
        Dictionary<AstAtom, NameBinding> bindings) => (expression is AstAtom root
            && bindings.TryGetValue(root, out var rootBinding)
            && rootBinding.Lookup.Locals.Any(aliases.Contains))
            || expression.Descendants().OfType<AstAtom>().Any(atom =>
            bindings.TryGetValue(atom, out var binding) && binding.Lookup.Locals.Any(aliases.Contains));

    private static AstBlock? NearestOwnedBlock(AstNode node, AstNode owner, AstSnapshot file)
    {
        for (AstNode? parent = file.GetParent(node); parent is not null; parent = file.GetParent(parent))
        {
            if (ReferenceEquals(parent, owner)) { return null; }
            if (parent is AstCallable or AstLambda or AstAnonymousFunction) { return null; }
            if (parent is AstBlock block) { return block; }
        }
        return null;
    }

    private static AstNode? DirectChildOfBlock(AstNode node, AstBlock block, AstSnapshot file)
    {
        AstNode current = node;
        for (AstNode? parent = file.GetParent(current); parent is not null; parent = file.GetParent(current))
        {
            if (ReferenceEquals(parent, block)) { return current; }
            current = parent;
        }
        return null;
    }

    private static int StatementIndex(AstBlock block, AstNode? statement)
    {
        if (statement is null) { return -1; }
        int index = 0;
        foreach (var candidate in block.Statements)
        {
            if (ReferenceEquals(candidate, statement)) { return index; }
            index++;
        }
        return -1;
    }

    private AstNode? NearestCallableOwner(AstNode node) => Ancestors(node)
        .FirstOrDefault(ancestor => ancestor is AstCallable or AstAnonymousFunction);

    private AstNode ParameterGroupNode(AstParameter parameter) => Files.Keys.FirstOrDefault(node =>
        ReferenceEquals(node.Origin.Element, parameter.GroupOrigin.Element))
        ?? (AstNode?)parameter.Type ?? Files.Values.First().Root;

    private static bool TrySourceAtom(AstExpression expression, Dictionary<AstAtom, NameBinding> bindings,
        out AstAtom atom)
    {
        var unwrapped = UnwrapTransparentExpression(expression);
        if (unwrapped is AstAtom candidate && bindings.ContainsKey(candidate))
        { atom = candidate; return true; }
        atom = null!;
        return false;
    }

    private static bool TryReferencedLocal(AstExpression expression, Dictionary<AstAtom, NameBinding> bindings,
        out LocalSymbol local)
    {
        if (TrySourceAtom(expression, bindings, out var atom)
            && bindings[atom].Lookup.Locals.SingleOrDefault() is { } found)
        { local = found; return true; }
        local = null!;
        return false;
    }

    private static AstExpression? UnwrapTransparentExpression(AstNode expression)
    {
        while (true)
        {
            if (expression is AstStructure { Children.Length: 1 } structure
                && structure.Children[0] is { } child)
            { expression = child; continue; }
            if (expression is AstOperation { Operation: "!!" } assertion
                && assertion.Operands.Take(2).ToArray() is [var asserted])
            { expression = asserted; continue; }
            if (expression is AstSequence { IsGrouping: true } grouping
                && grouping.Elements.Take(2).ToArray() is [var grouped])
            { expression = grouped; continue; }
            return expression as AstExpression;
        }
    }

    private bool IsNullComparisonUse(AstAtom reference, AstSnapshot file)
    {
        AstNode value = reference;
        for (AstNode? parent = file.GetParent(value); parent is not null; parent = file.GetParent(parent))
        {
            if (IsTransparentWrapper(parent, value))
            { value = parent; continue; }
            if (parent is not AstOperation { Operation: "==" or "!=" } operation) { return false; }
            return operation.Operands.Any(operand => UnwrapTransparentExpression(operand) is { } unwrapped
                    && IsNullLiteral(unwrapped))
                && operation.Operands.Any(operand => IsTransparentValue(operand, reference, file));
        }
        return false;
    }

    private static bool IsTransparentValue(AstNode expression, AstNode value, AstSnapshot file)
    {
        while (!ReferenceEquals(value, expression))
        {
            var parent = file.GetParent(value);
            if (parent is null) { return false; }
            if (IsTransparentWrapper(parent, value))
            { value = parent; continue; }
            return false;
        }
        return true;
    }

    private static bool IsTransparentWrapper(AstNode parent, AstNode child)
    {
        if (parent is AstStructure) { return true; }
        if (parent is AstSequence { IsGrouping: true } grouping
            && grouping.Elements.Take(2).ToArray() is [var grouped])
        { return ReferenceEquals(grouped, child); }
        return parent is AstOperation { Operation: "!!" } assertion
            && assertion.Operands.Take(2).ToArray() is [var asserted]
            && ReferenceEquals(asserted, child);
    }

    private static bool IsDirectInvocation(AstAtom reference, AstSnapshot file)
    {
        for (AstNode? parent = file.GetParent(reference); parent is not null; parent = file.GetParent(parent))
        {
            if (parent is AstCall call)
            { return call.Target is { } target && IsTransparentValue(target, reference, file); }
            if (parent is AstCallable or AstLambda or AstAnonymousFunction) { return false; }
        }
        return false;
    }

    private static bool IsWriteOnlyLocalReference(AstAtom reference, AstSnapshot file)
    {
        for (AstNode? parent = file.GetParent(reference); parent is not null; parent = file.GetParent(parent))
        {
            if (parent is AstAssignment assignment)
            { return assignment.Operation == "=" && assignment.Location is { } location
                    && IsTransparentValue(location, reference, file); }
            if (parent is AstCallable or AstLambda or AstAnonymousFunction) { return false; }
        }
        return false;
    }

    private static bool TryCallArgument(AstAtom reference, AstSnapshot file, out AstCall call, out int argumentIndex)
    {
        for (AstNode? parent = file.GetParent(reference); parent is not null; parent = file.GetParent(parent))
        {
            if (parent is AstCall candidate)
            {
                for (int index = 0; index < candidate.Arguments.Length; index++)
                {
                    if (IsTransparentValue(candidate.Arguments[index].Value, reference, file))
                    { call = candidate; argumentIndex = index; return true; }
                }
                break;
            }
            if (parent is AstCallable or AstLambda or AstAnonymousFunction) { break; }
        }
        call = null!;
        argumentIndex = -1;
        return false;
    }

    private AstNode? FindEnclosingCallableBoundary(AstAtom reference, AstNode owner, AstSnapshot file)
    {
        for (AstNode? parent = file.GetParent(reference); parent is not null; parent = file.GetParent(parent))
        {
            if (parent is AstLambda or AstAnonymousFunction) { return parent; }
            if (parent is AstCallable callable)
            { return ReferenceEquals(callable, owner) ? null : callable; }
        }
        return null;
    }
}
