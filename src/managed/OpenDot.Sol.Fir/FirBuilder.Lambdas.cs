// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Fir;

internal sealed partial class FirBuilder
{
    private FirExpression LambdaReference(AstLambda lambda, ResolvedType type)
    {
        if (!CanLowerLambda(lambda, type))
        {
            Unsupported(lambda, "Lambda capture, parent context, or function ABI is outside the verified native subset");
            return new FirConstant("()", Empty, lambda.Origin);
        }

        if (!_lambdaFunctions.TryGetValue(lambda, out int target))
        {
            target = _nextFunctionId++;
            _lambdaFunctions.Add(lambda, target);
            _builtFunctions.Add(target, BuildLambdaFunction(lambda, target, type));
        }
        return new FirFunctionReference(target, type, lambda.Origin);
    }

    private bool CanLowerLambda(AstLambda lambda, ResolvedType type)
    {
        if (type.Kind != ResolvedTypeKind.Function || type.Arguments.IsEmpty
            || !FirVerifier.Supported(type) || lambda.HasUnrepresentablePosition
            || lambda.Form == AstLambdaForm.Positional && lambda.Positions.Any(position => position < 0))
        { return false; }
        if (ContextualLambdaType(lambda) is { } expected && Specialize(expected) != type)
        { return false; }

        // Closed top-level functions and receiver-free methods on stateless objects have no
        // hidden activation record that a lifted code pointer would need to retain.
        if (_current is not AstFunction currentFunction || currentFunction.TypeParameters.Any()
            || Ancestors(currentFunction).OfType<AstFunction>().Any())
        { return false; }
        var owner = GenericMethodSourcePolicy.DirectOwner(currentFunction, _analysis.Sources);
        if (owner is not null && (owner.TypeParameters.Any()
            || !GenericMethodSourcePolicy.IsSupportedStatelessObjectMethod(currentFunction, _analysis.Sources)))
        { return false; }

        var lambdaScope = _analysis.Sources.Lexical.Scopes
            .SingleOrDefault(scope => ReferenceEquals(scope.Owner, lambda));
        if (lambdaScope is null) { return false; }

        foreach (var atom in lambda.Descendants().OfType<AstAtom>())
        {
            if (!ReferenceEquals(NearestLambda(atom), lambda)) { continue; }
            if (atom.Spelling is "this" or "super") { return false; }
            if (atom.SyntaxKind != NodeKind.NameExpression) { continue; }

            var binding = _analysis.Sources.Lexical.GetBinding(atom);
            if (binding.CapturesOuterLocal || binding.Lookup.Locals.Any(local =>
                !IsWithinScope(local.ScopeId, lambdaScope.Id)))
            { return false; }
            if (binding.Lookup.Locals.Any(local => local.Kind == LocalSymbolKind.Function))
            { return false; }
            if (binding.Lookup.Declarations.Candidates.SelectMany(symbol => symbol.Parts)
                .OfType<AstDeclaration>().Any(declaration =>
                    Ancestors(declaration).OfType<AstTypeDeclaration>().Any()))
            { return false; }
        }

        // The first FIR lambda slice does not introduce another callable ABI or a generic caller
        // identity. The nested lambda itself may still be lifted if it has no captures.
        foreach (var nestedFunction in lambda.Descendants().Where(node =>
            node.SyntaxKind == NodeKind.AnonymousFunctionExpression || node is AstFunction))
        { return false; }

        foreach (var call in lambda.Descendants().OfType<AstCall>())
        {
            if (!ReferenceEquals(NearestLambda(call), lambda)) { continue; }
            var binding = BoundCall(call);
            if (binding?.Target is AstFunction target && target.TypeParameters.Any()
                && !HasClosedNativeTypeArguments(target, binding.TypeArguments))
            { return false; }
        }
        foreach (var expression in lambda.Descendants().OfType<AstExpression>())
        {
            if (!ReferenceEquals(NearestLambda(expression), lambda)) { continue; }
            var binding = BoundExpression(expression);
            if (binding?.FunctionValueTarget is { } target && target.TypeParameters.Any()
                && !HasClosedNativeTypeArguments(target, binding.FunctionValueTypeArguments))
            { return false; }
        }

        return true;
    }

    private FirFunction BuildLambdaFunction(AstLambda lambda, int id, ResolvedType type)
    {
        var savedSlots = _slots.ToArray();
        var savedLambda = _activeLambda;
        var savedParameters = _activeLambdaParameters;
        _slots.Clear();
        _activeLambda = lambda;
        try
        {
            var parameters = ImmutableArray.CreateBuilder<FirSlot>(type.ParameterModes.Length);
            var namedParameters = lambda.Parameters.ToArray();
            var lambdaScope = _analysis.Sources.Lexical.Scopes.Single(scope =>
                ReferenceEquals(scope.Owner, lambda));
            var locals = lambdaScope.Symbols.Where(local => local.Kind == LocalSymbolKind.Parameter).ToArray();

            for (int index = 0; index < type.ParameterModes.Length; index++)
            {
                string name;
                AstOrigin origin;
                LocalSymbol? local = null;
                if (lambda.Form == AstLambdaForm.Named)
                {
                    var parameter = namedParameters[index];
                    var nameNode = parameter.Children.First(node => node.SyntaxKind == NodeKind.Name);
                    name = nameNode.Origin.Text;
                    origin = nameNode.Origin;
                    local = locals.SingleOrDefault(candidate =>
                        ReferenceEquals(candidate.Origin.Element, nameNode.Origin.Element));
                }
                else if (lambda.Form == AstLambdaForm.Positional)
                {
                    name = "$" + index;
                    origin = lambda.Origin;
                }
                else
                {
                    Unsupported(lambda, "A headerless lambda cannot have function parameters");
                    name = "$" + index;
                    origin = lambda.Origin;
                }

                var mode = type.ParameterModes[index];
                var slot = new FirSlot(index, name, type.Arguments[index],
                    local?.IsMutable == true || mode is AstPassingMode.Ref or AstPassingMode.Out,
                    mode, true, origin);
                parameters.Add(slot);
                if (local is not null) { _slots.Add(local, slot); }
            }

            _activeLambdaParameters = parameters.ToImmutable();
            var result = type.Arguments[^1];
            var body = LambdaBlock(lambda, result);
            return new(id, "<lambda>", _activeLambdaParameters,
                _activeLambdaParameters.Concat(_slots.Values).DistinctBy(slot => slot.Id)
                    .OrderBy(slot => slot.Id).ToImmutableArray(), result, body,
                FirRuntimeFunction.None, type.Effects, lambda.Origin)
            { LambdaSource = lambda };
        }
        finally
        {
            _slots.Clear();
            foreach (var pair in savedSlots) { _slots.Add(pair.Key, pair.Value); }
            _activeLambda = savedLambda;
            _activeLambdaParameters = savedParameters;
        }
    }

    private FirBlock LambdaBlock(AstLambda lambda, ResolvedType resultType)
    {
        var statements = lambda.Statements.ToArray();
        var lowered = ImmutableArray.CreateBuilder<FirStatement>();
        for (int index = 0; index < statements.Length; index++)
        {
            var source = statements[index];
            bool tail = index == statements.Length - 1 && resultType != Empty
                && source is AstExpressionStatement { Expression: not null };
            if (tail && source is AstExpressionStatement { Expression: { } expression })
            { lowered.Add(new FirReturn(Expression(expression), source.Origin)); }
            else if (Statement(source) is { } statement) { lowered.Add(statement); }
        }

        bool explicitFinalReturn = statements.LastOrDefault() is AstReturn;
        if (resultType == Empty && !explicitFinalReturn)
        { lowered.Add(new FirReturn(null, lambda.Origin)); }
        return new(lowered.ToImmutable(), lambda.Origin);
    }

    private bool IsWithinScope(int scopeId, int ancestorId)
    {
        var scopes = _analysis.Sources.Lexical.Scopes;
        for (int? current = scopeId; current is not null; current = scopes[current.Value].ParentId)
        { if (current.Value == ancestorId) { return true; } }
        return false;
    }

    private bool HasClosedNativeTypeArguments(AstFunction target, ImmutableArray<ResolvedType> arguments)
    {
        if (arguments.IsDefault || arguments.Length != target.TypeParameters.Count()) { return false; }
        foreach (var argument in arguments)
        {
            if (!SupportedArgument(Specialize(argument), out _)) { return false; }
        }
        return true;
    }

    private ResolvedType? ContextualLambdaType(AstLambda lambda)
    {
        foreach (var call in _analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstCall>().Where(call => call.Arguments.Any(argument => ReferenceEquals(argument.Value, lambda))))
        {
            var selected = BoundCall(call);
            var mapping = selected?.Arguments.FirstOrDefault(candidate => candidate.ArgumentIndex >= 0
                && candidate.ArgumentIndex < call.Arguments.Length
                && ReferenceEquals(call.Arguments[candidate.ArgumentIndex].Value, lambda));
            if (selected is not null && mapping is not null
                && mapping.ParameterIndex >= 0 && mapping.ParameterIndex < selected.ParameterTypes.Length
                && mapping.ParameterIndex < selected.Target.Parameters.Length)
            {
                var expected = selected.ParameterTypes[mapping.ParameterIndex];
                return selected.Target.Parameters[mapping.ParameterIndex].PassingMode == AstPassingMode.Vararg
                    && expected.Kind == ResolvedTypeKind.Array ? expected.Arguments[0] : expected;
            }
            if (call.Target is { } target && BoundExpression(target)?.Type is
                { Kind: ResolvedTypeKind.Function } callable)
            {
                int index = -1;
                for (int candidate = 0; candidate < call.Arguments.Length; candidate++)
                {
                    if (ReferenceEquals(call.Arguments[candidate].Value, lambda)) { index = candidate; break; }
                }
                if (index >= 0 && index < callable.ParameterModes.Length) { return callable.Arguments[index]; }
            }
        }

        var file = _files[lambda];
        int arrayDepth = 0;
        for (AstNode? parent = file.GetParent(lambda); parent is not null; parent = file.GetParent(parent))
        {
            if (parent is AstSequence { SyntaxKind: NodeKind.ArrayExpression }) { arrayDepth++; }
            if (parent is AstLambda enclosing)
            {
                if (!IsWithinNode(lambda, enclosing.TailExpression, file)) { return null; }
                if (BoundExpression(enclosing)?.Type is not { Kind: ResolvedTypeKind.Function } enclosingType)
                { return null; }
                var result = enclosingType.Arguments[^1];
                for (int depth = 0; depth < arrayDepth; depth++)
                {
                    if (result.Kind is not (ResolvedTypeKind.Array or ResolvedTypeKind.FixedArray)) { return null; }
                    result = result.Arguments[0];
                }
                return result;
            }
            if (parent is AstProperty property && property.Type is not null)
            {
                var expected = _analysis.Sources.Types.GetType(property.Type);
                for (AstNode? child = lambda; child is not null && !ReferenceEquals(child, property);
                    child = file.GetParent(child))
                {
                    if (child.SyntaxKind == NodeKind.ArrayExpression
                        && expected.Kind is ResolvedTypeKind.Array or ResolvedTypeKind.FixedArray)
                    { expected = expected.Arguments[0]; }
                }
                return expected;
            }
            if (parent is AstReturn)
            {
                var callable = Ancestors(parent).OfType<AstFunction>().FirstOrDefault();
                if (callable?.ReturnType is { } returnType)
                { return _analysis.Sources.Types.GetType(returnType); }
            }
            if (parent is AstFunction function && function.ReturnType is { } functionReturn
                && IsWithinNode(lambda, function.Body.TailExpression, file))
            { return _analysis.Sources.Types.GetType(functionReturn); }
        }
        return null;
    }

    private static bool IsWithinNode(AstNode node, AstNode? ancestor, AstSnapshot file)
    {
        if (ancestor is null) { return false; }
        for (AstNode? current = node; current is not null; current = file.GetParent(current))
        { if (ReferenceEquals(current, ancestor)) { return true; } }
        return false;
    }

    private AstLambda? NearestLambda(AstNode node) => Ancestors(node).OfType<AstLambda>().FirstOrDefault();
}
