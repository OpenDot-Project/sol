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
    private static bool LambdaShape(AstLambda lambda, ResolvedType expected)
    {
        if (expected.Kind != ResolvedTypeKind.Function) { return false; }
        int count = expected.ParameterModes.Length;
        if (lambda.Form == AstLambdaForm.Named) { return lambda.Parameters.Count() == count; }
        if (lambda.Form == AstLambdaForm.Positional)
        { return !lambda.HasUnrepresentablePosition && lambda.MinimumPositionalArity <= count && expected.ParameterModes.All(m => m == AstPassingMode.Value); }
        return count == 0;
    }

    private Value Lambda(AstLambda lambda, State outer, Context parent, ResolvedType? expected)
    {
        if (expected is not null && (expected.Kind != ResolvedTypeKind.Function || !LambdaShape(lambda, expected)))
        { Report("SOL2220", "Lambda parameters do not match the expected function type.", lambda); return new(null); }
        if (lambda.Form == AstLambdaForm.Positional && expected is null)
        { Report("SOL2220", "Positional lambdas require an expected function type.", lambda); return new(null); }
        var state = outer.Clone(); state.Effects.Clear();
        var types = new List<ResolvedType>();
        var modes = new List<AstPassingMode>();
        var parameters = lambda.Parameters.ToArray();
        for (int index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var written = parameter.Children.OfType<AstType>().FirstOrDefault();
            var type = written is null ? expected?.Arguments[index] : Written(written);
            var passing = parameter.Children.FirstOrDefault(n => n.SyntaxKind == NodeKind.PassingMode)?.Origin.Text;
            var mode = passing == "ref" ? AstPassingMode.Ref : passing == "out" ? AstPassingMode.Out : AstPassingMode.Value;
            if (type is null) { Report("SOL2220", "An uncontextualized lambda parameter needs an explicit type.", parameter); return new(null); }
            if (expected is not null && (type != expected.Arguments[index] || mode != expected.ParameterModes[index]))
            { Report("SOL2220", "Lambda parameter type or mode differs from the expected signature.", parameter); }
            types.Add(type); modes.Add(mode);
            var name = parameter.Children.FirstOrDefault(n => n.SyntaxKind == NodeKind.Name)?.Origin.Text;
            foreach (var local in OwnLocals(lambda).Where(l => l.Name == name))
            { _localTypes[local] = type; if (mode != AstPassingMode.Out) { state.Assigned.Add(local); } }
        }
        if (lambda.Form == AstLambdaForm.Positional && expected is not null)
        { types.AddRange(expected.Arguments.Take(expected.ParameterModes.Length)); modes.AddRange(expected.ParameterModes); }
        foreach (var reference in lambda.Descendants().OfType<AstAtom>().Where(a => a.SyntaxKind == NodeKind.NameExpression))
        {
            if (Ancestors(reference).OfType<AstLambda>().FirstOrDefault() != lambda) { continue; }
            var lookup = Sources.Lexical.Lookup(reference, reference.Spelling);
            foreach (var local in lookup.Locals.Where(l => !OwnLocals(lambda).Contains(l)))
            {
                var type = LocalType(local);
                var callable = parent.Callable;
                bool byref = callable?.Parameters.Any(p => p.PassingMode is AstPassingMode.Ref or AstPassingMode.Out
                    && ReferenceEquals(p.NameOrigin.Element, local.Origin.Element)) == true;
                if (byref || type is not null && MayContainBorrow(type)) { Report("SOL2262", "A closure cannot capture ref/out storage or a ref-like value.", reference); }
            }
        }
        var exits = new List<State>();
        var resultType = expected?.Arguments[^1];
        var context = new Context(null, resultType, lambda, expected, Exits: exits);
        var result = Statements(lambda.Statements, state, context, true);
        resultType ??= result.Type;
        foreach (var exit in exits.Append(state).Where(s => s.Reachable))
        {
            for (int index = 0; index < parameters.Length; index++)
            {
                if (modes[index] != AstPassingMode.Out) { continue; }
                var name = parameters[index].Children.FirstOrDefault(n => n.SyntaxKind == NodeKind.Name)?.Origin.Text;
                if (OwnLocals(lambda).Any(l => l.Name == name && !exit.Assigned.Contains(l)))
                { Report("SOL2242", "Out lambda parameter must be assigned on every normal exit.", lambda); }
            }
        }
        var effects = state.Effects.Where(IsChecked).ToArray();
        if (expected is not null && effects.Any(e => !expected.Effects.Any(b => Converts(e, b))))
        { Report("SOL2253", "Lambda checked effects exceed the expected function contract.", lambda); }
        if (resultType is null) { Defer(lambda); return new(null); }
        return new(Types.GetFunction(types, resultType, modes, expected?.Effects ?? effects.ToImmutableArray()));
    }
}
