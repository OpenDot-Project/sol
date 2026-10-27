// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Fir;

public static partial class FirVerifier
{
    private sealed partial class Checker
    {
        private void CheckLambdaSourceContract(AstLambda lambda)
        {
            if (!ReferenceEquals(lambda.Origin.Element, _function.Origin.Element)
                || _function.Name != "<lambda>" || _function.RuntimeFunction != FirRuntimeFunction.None
                || _function.ExportName is not null || !_function.SpecializationArguments.IsEmpty)
            { Fail("Synthetic lambda identity or metadata is inconsistent.", _function.Origin); }
            if (program.Functions.Count(candidate => ReferenceEquals(candidate.LambdaSource, lambda)) != 1)
            { Fail("Each source lambda must have exactly one synthetic FIR function.", lambda.Origin); }

            ExpressionBinding? binding;
            try { binding = program.Analysis.GetExpression(lambda); }
            catch (ArgumentException)
            {
                Fail("Synthetic lambda is not owned by this source analysis.", _function.Origin);
                return;
            }
            if (binding?.Status != BodyBindingStatus.Bound || binding.Type is not { Kind: ResolvedTypeKind.Function } sourceType)
            { Fail("Synthetic lambda does not have a completed source function type.", _function.Origin); return; }

            var signature = Substitute(sourceType);
            if (!Supported(signature) || ContainsParameter(signature)
                || signature.ParameterModes.Length + 1 != signature.Arguments.Length)
            { Fail("Synthetic lambda signature is open or outside the supported native ABI.", _function.Origin); return; }
            if (ContextualLambdaType(lambda) is { } expected && Substitute(expected) != signature)
            { Fail("Synthetic lambda requires an ABI adapter at its source context.", lambda.Origin); }
            if (_function.Parameters.Length != signature.ParameterModes.Length
                || _function.ReturnType != signature.Arguments[^1]
                || !_function.Effects.SequenceEqual(signature.Effects))
            { Fail("Synthetic lambda function signature differs from source analysis.", _function.Origin); }

            var file = program.Analysis.Sources.Declarations.Files.SingleOrDefault(candidate =>
                ReferenceEquals(candidate.Psi, lambda.Origin.Element.Snapshot));
            if (file is null)
            { Fail("Synthetic lambda has no source file in this program.", _function.Origin); return; }

            AstFunction? parentFunction = null;
            AstTypeDeclaration? parentType = null;
            int parentFunctionCount = 0;
            for (var parent = file.GetParent(lambda); parent is not null; parent = file.GetParent(parent))
            {
                if (parent is AstFunction function)
                {
                    parentFunctionCount++;
                    parentFunction ??= function;
                }
                if (parent is AstTypeDeclaration declaration) { parentType ??= declaration; }
            }
            if (parentFunction is null || parentFunctionCount != 1
                || program.Analysis.Sources.Types.GetTypeParameters(parentFunction).Length != 0
                || parentType is not null && (program.Analysis.Sources.Types.GetTypeParameters(parentType).Length != 0
                    || !GenericMethodSourcePolicy.IsSupportedStatelessObjectMethod(parentFunction,
                        program.Analysis.Sources)))
            { Fail("Synthetic lambda is not in a closed receiver-free source function.", _function.Origin); }

            if (!IsCaptureFree(lambda, file))
            { Fail("Synthetic lambda captures a local, receiver, or declaration-owned member.", _function.Origin); }

            var explicitParameters = lambda.Parameters.ToArray();
            if (lambda.Form == AstLambdaForm.Named)
            {
                if (explicitParameters.Length != signature.ParameterModes.Length)
                { Fail("Named lambda parameter count differs from its contextual signature.", _function.Origin); }
                for (int index = 0; index < Math.Min(explicitParameters.Length, _function.Parameters.Length); index++)
                {
                    var sourceParameter = explicitParameters[index];
                    var name = sourceParameter.Children.FirstOrDefault(node => node.SyntaxKind == NodeKind.Name);
                    if (name is null) { Fail("Named lambda parameter has no source name.", sourceParameter.Origin); continue; }
                    var modeText = sourceParameter.Children.FirstOrDefault(node => node.SyntaxKind == NodeKind.PassingMode)?.Origin.Text;
                    var mode = modeText == "ref" ? AstPassingMode.Ref
                        : modeText == "out" ? AstPassingMode.Out : AstPassingMode.Value;
                    var target = _function.Parameters[index];
                    if (target.Name != name.Origin.Text || target.Mode != mode
                        || !ReferenceEquals(target.Origin.Element, name.Origin.Element))
                    { Fail("Synthetic lambda parameter identity or passing mode differs from source.", target.Origin); }
                    if (sourceParameter.Children.OfType<AstType>().FirstOrDefault() is { } written
                        && program.Analysis.Sources.Types.GetType(written) != target.Type)
                    { Fail("Typed lambda parameter requires an unsupported ABI adapter.", written.Origin); }
                }
            }
            else if (lambda.Form == AstLambdaForm.Positional)
            {
                if (explicitParameters.Length != 0 || signature.ParameterModes.Any(mode => mode != AstPassingMode.Value)
                    || lambda.Positions.Any(position => position < 0 || position >= signature.ParameterModes.Length))
                { Fail("Positional lambda does not match its contextual parameter indices.", lambda.Origin); }
                for (int index = 0; index < _function.Parameters.Length; index++)
                {
                    if (_function.Parameters[index].Name != "$" + index
                        || !ReferenceEquals(_function.Parameters[index].Origin.Element, lambda.Origin.Element))
                    { Fail("Positional lambda parameter slot identity is invalid.", _function.Parameters[index].Origin); }
                }
            }
            else if (explicitParameters.Length != 0 || !signature.ParameterModes.IsEmpty)
            { Fail("Headerless lambda has parameters in its function signature.", lambda.Origin); }

            if (_function.Parameters.Length == signature.ParameterModes.Length)
            {
                for (int index = 0; index < signature.ParameterModes.Length; index++)
                {
                    if (_function.Parameters[index].Id != index
                        || _function.Parameters[index].Type != signature.Arguments[index]
                        || _function.Parameters[index].Mode != signature.ParameterModes[index]
                        || !_function.Parameters[index].IsParameter)
                    { Fail("Synthetic lambda parameter slot differs from its contextual signature.", _function.Origin); }
                }
            }
            ValidateNestedFunctionEffects(signature, lambda.Origin);
            ValidateEffects(_function.Effects, _function.Origin);

            if (program.Analysis.UnverifiedNodes.Any(node => lambda.Descendants().Contains(node)))
            { Fail("Synthetic lambda contains source analysis obligations without proof.", lambda.Origin); }
        }

        private bool IsCaptureFree(AstLambda lambda, AstSnapshot file)
        {
            var scopes = program.Analysis.Sources.Lexical.Scopes;
            var scope = scopes.SingleOrDefault(candidate => ReferenceEquals(candidate.Owner, lambda));
            if (scope is null) { return false; }

            foreach (var node in lambda.Descendants())
            {
                if (node.SyntaxKind == NodeKind.AnonymousFunctionExpression || node is AstFunction)
                { return false; }
                if (node is not AstAtom atom) { continue; }
                AstLambda? nearest = null;
                for (var parent = file.GetParent(atom); parent is not null; parent = file.GetParent(parent))
                {
                    if (parent is AstLambda enclosing) { nearest = enclosing; break; }
                }
                if (!ReferenceEquals(nearest, lambda)) { continue; }
                if (atom.Spelling is "this" or "super") { return false; }
                if (atom.SyntaxKind != NodeKind.NameExpression || atom.Spelling == "default") { continue; }

                var binding = program.Analysis.Sources.Lexical.GetBinding(atom);
                if (binding.CapturesOuterLocal || binding.Lookup.Locals.Any(local =>
                    !IsScopeWithin(local.ScopeId, scope.Id, scopes))
                    || binding.Lookup.Locals.Any(local => local.Kind == LocalSymbolKind.Function))
                { return false; }
                if (binding.Lookup.Declarations.Scope == LookupScope.Member) { return false; }
                foreach (var declaration in binding.Lookup.Declarations.Candidates.SelectMany(symbol => symbol.Parts))
                {
                    var declarationFile = program.Analysis.Sources.Declarations.Files.SingleOrDefault(candidate =>
                        ReferenceEquals(candidate.Psi, declaration.Origin.Element.Snapshot));
                    if (declarationFile is null || Ancestors(declaration, declarationFile)
                        .OfType<AstTypeDeclaration>().Any())
                    { return false; }
                }
            }

            return true;
        }

        private ResolvedType? ContextualLambdaType(AstLambda lambda)
        {
            var files = program.Analysis.Sources.Declarations.Files;
            foreach (var call in files.SelectMany(file => file.Nodes).OfType<AstCall>()
                .Where(call => call.Arguments.Any(argument => ReferenceEquals(argument.Value, lambda))))
            {
                var binding = program.Analysis.Calls.SingleOrDefault(candidate => ReferenceEquals(candidate.Call, call));
                var mapping = binding?.Arguments.FirstOrDefault(candidate => candidate.ArgumentIndex >= 0
                    && candidate.ArgumentIndex < call.Arguments.Length
                    && ReferenceEquals(call.Arguments[candidate.ArgumentIndex].Value, lambda));
                if (binding is not null && mapping is not null && mapping.ParameterIndex >= 0
                    && mapping.ParameterIndex < binding.ParameterTypes.Length
                    && mapping.ParameterIndex < binding.Target.Parameters.Length)
                {
                    var expected = binding.ParameterTypes[mapping.ParameterIndex];
                    return binding.Target.Parameters[mapping.ParameterIndex].PassingMode == AstPassingMode.Vararg
                        && expected.Kind == ResolvedTypeKind.Array ? expected.Arguments[0] : expected;
                }
                if (call.Target is { } target
                    && program.Analysis.GetExpression(target)?.Type is { Kind: ResolvedTypeKind.Function } callable)
                {
                    int index = -1;
                    for (int candidate = 0; candidate < call.Arguments.Length; candidate++)
                    {
                        if (ReferenceEquals(call.Arguments[candidate].Value, lambda)) { index = candidate; break; }
                    }
                    if (index >= 0 && index < callable.ParameterModes.Length) { return callable.Arguments[index]; }
                }
            }

            var file = files.SingleOrDefault(candidate => ReferenceEquals(candidate.Psi, lambda.Origin.Element.Snapshot));
            if (file is null) { return null; }
            int arrayDepth = 0;
            for (AstNode? parent = file.GetParent(lambda); parent is not null; parent = file.GetParent(parent))
            {
                if (parent is AstSequence { SyntaxKind: NodeKind.ArrayExpression }) { arrayDepth++; }
                if (parent is AstLambda enclosing)
                {
                    if (!IsWithinNode(lambda, enclosing.TailExpression, file)) { return null; }
                    if (program.Analysis.GetExpression(enclosing)?.Type is not
                        { Kind: ResolvedTypeKind.Function } enclosingType)
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
                    var expected = program.Analysis.Sources.Types.GetType(property.Type);
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
                    var callable = Ancestors(parent, file).OfType<AstFunction>().FirstOrDefault();
                    if (callable?.ReturnType is { } returnType)
                    { return program.Analysis.Sources.Types.GetType(returnType); }
                }
                if (parent is AstFunction function && function.ReturnType is { } functionReturn
                    && IsWithinNode(lambda, function.Body.TailExpression, file))
                { return program.Analysis.Sources.Types.GetType(functionReturn); }
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

        private static System.Collections.Generic.IEnumerable<AstNode> Ancestors(AstNode node, AstSnapshot file)
        {
            for (var parent = file.GetParent(node); parent is not null; parent = file.GetParent(parent))
            { yield return parent; }
        }

        private static bool IsScopeWithin(int scopeId, int ancestorId, System.Collections.Immutable.ImmutableArray<LexicalScope> scopes)
        {
            for (int? current = scopeId; current is not null; current = scopes[current.Value].ParentId)
            { if (current.Value == ancestorId) { return true; } }
            return false;
        }
    }
}
