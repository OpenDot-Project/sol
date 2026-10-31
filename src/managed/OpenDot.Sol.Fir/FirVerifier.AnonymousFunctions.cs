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
        private void CheckAnonymousFunctionSourceContract(AstAnonymousFunction function)
        {
            if (!ReferenceEquals(function.Origin.Element, _function.Origin.Element)
                || _function.Name != "<anonymous-function>"
                || _function.RuntimeFunction != FirRuntimeFunction.None || _function.ExportName is not null
                || !_function.SpecializationArguments.IsEmpty)
            { Fail("Synthetic anonymous-function identity or metadata is inconsistent.", _function.Origin); }
            if (program.Functions.Count(candidate => ReferenceEquals(candidate.AnonymousFunctionSource, function)) != 1)
            { Fail("Each lowered anonymous function must have exactly one synthetic FIR function.", function.Origin); }

            ExpressionBinding? binding;
            try { binding = program.Analysis.GetExpression(function); }
            catch (System.ArgumentException)
            {
                Fail("Synthetic anonymous function is not owned by this source analysis.", _function.Origin);
                return;
            }
            if (binding?.Status != BodyBindingStatus.Bound
                || binding.Type is not { Kind: ResolvedTypeKind.Function } sourceType)
            { Fail("Synthetic anonymous function has no completed source function type.", _function.Origin); return; }

            var signature = Substitute(sourceType);
            if (!Supported(signature) || ContainsParameter(signature)
                || signature.ParameterModes.Length + 1 != signature.Arguments.Length)
            { Fail("Synthetic anonymous-function signature is open or outside the supported native ABI.", _function.Origin); return; }
            if (_function.Parameters.Length != signature.ParameterModes.Length
                || _function.ReturnType != signature.Arguments[^1]
                || !_function.Effects.SequenceEqual(signature.Effects))
            { Fail("Synthetic anonymous-function signature differs from source analysis.", _function.Origin); }

            var file = program.Analysis.Sources.Declarations.Files.SingleOrDefault(candidate =>
                ReferenceEquals(candidate.Psi, function.Origin.Element.Snapshot));
            if (file is null)
            { Fail("Synthetic anonymous function has no source file in this program.", _function.Origin); return; }
            if (FunctionValueNestingDepth(function, file) >= MaximumNativeFunctionValueNesting)
            { Fail("Synthetic anonymous function exceeds the 128-level source nesting budget.", function.Origin); }

            var parents = new System.Collections.Generic.List<AstNode>();
            for (var parent = file.GetParent(function); parent is not null; parent = file.GetParent(parent))
            { parents.Add(parent); }
            var parentFunctions = parents.OfType<AstFunction>().ToArray();
            var parentType = parents.OfType<AstTypeDeclaration>().FirstOrDefault();
            if (parentFunctions.Length != 1 || parentFunctions[0].TypeParameters.Any()
                || parentType is not null && (program.Analysis.Sources.Types.GetTypeParameters(parentType).Length != 0
                    || !GenericMethodSourcePolicy.IsSupportedStatelessObjectMethod(parentFunctions[0],
                        program.Analysis.Sources)))
            { Fail("Synthetic anonymous function is not in a closed receiver-free source function.", _function.Origin); }

            if (!IsCaptureFree(function, file))
            { Fail("Synthetic anonymous function captures local, receiver, or declaration-owned storage.", function.Origin); }

            if (function.Parameters.Length != signature.ParameterModes.Length)
            { Fail("Anonymous-function parameter count differs from its function type.", function.Origin); }
            for (int index = 0; index < Math.Min(function.Parameters.Length, _function.Parameters.Length); index++)
            {
                var source = function.Parameters[index];
                var target = _function.Parameters[index];
                var sourceMode = source.PassingMode == AstPassingMode.Vararg
                    ? AstPassingMode.Value : source.PassingMode;
                var declaredElementType = source.PassingMode == AstPassingMode.Vararg
                    ? signature.Arguments[index].Kind == ResolvedTypeKind.Array
                        ? signature.Arguments[index].Arguments[0] : null
                    : signature.Arguments[index];
                if (source.DefaultValue is not null
                    || declaredElementType is null
                    || target.Id != index || sourceMode != signature.ParameterModes[index]
                    || target.Mode != signature.ParameterModes[index]
                    || target.Type != signature.Arguments[index] || !target.IsParameter
                    || target.IsEscaping != source.IsEscaping
                    || target.Name != source.Name
                    || !ReferenceEquals(target.Origin.Element, source.NameOrigin.Element))
                { Fail("Anonymous-function parameter slot differs from its source contract.", source.NameOrigin); }
                if (source.Type is { } written
                    && program.Analysis.Sources.Types.GetType(written) != declaredElementType)
                { Fail("Anonymous-function parameter type differs from its source function type.", written.Origin); }
            }

            ValidateNestedFunctionEffects(signature, function.Origin);
            ValidateEffects(_function.Effects, _function.Origin);
            if (program.Analysis.UnverifiedNodes.Any(node => function.Descendants().Contains(node)))
            { Fail("Synthetic anonymous function contains source obligations without proof.", function.Origin); }
        }

        private bool IsCaptureFree(AstAnonymousFunction function, AstSnapshot file)
        {
            var lexical = program.Analysis.Sources.Lexical;
            var scopes = lexical.Scopes;
            var scope = scopes.SingleOrDefault(candidate => ReferenceEquals(candidate.Owner, function));
            if (scope is null || !lexical.GetAnonymousFunctionCaptures(function).IsEmpty
                || !lexical.GetAnonymousFunctionReceiverCaptures(function).IsEmpty)
            { return false; }

            foreach (var node in AnonymousFunctionOwnedNodes(function, file))
            {
                if (node is AstFunction)
                { return false; }
                if (node is AstAnonymousFunction nested && !ReferenceEquals(nested, function)) { continue; }
                if (node is not AstAtom atom) { continue; }
                if (atom.Spelling is "this" or "super") { return false; }
                if (atom.SyntaxKind != NodeKind.NameExpression || atom.Spelling == "default") { continue; }

                var binding = lexical.GetBinding(atom);
                if (binding.CapturesOuterLocal || binding.Lookup.Locals.Any(local =>
                    !IsScopeWithin(local.ScopeId, scope.Id, scopes))
                    || binding.Lookup.Locals.Any(local => local.Kind == LocalSymbolKind.Function)
                    || binding.Lookup.Declarations.Scope == LookupScope.Member)
                { return false; }
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

        private static System.Collections.Generic.IEnumerable<AstNode> AnonymousFunctionOwnedNodes(
            AstAnonymousFunction function, AstSnapshot file)
        {
            foreach (var node in function.Descendants())
            {
                bool nestedCallableBody = false;
                for (AstNode? parent = file.GetParent(node); parent is not null; parent = file.GetParent(parent))
                {
                    if (ReferenceEquals(parent, function)) { break; }
                    if (parent is AstAnonymousFunction or AstLambda or AstFunction)
                    { nestedCallableBody = true; break; }
                }
                if (!nestedCallableBody) { yield return node; }
            }
        }
    }
}
