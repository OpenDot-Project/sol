// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;

namespace OpenDot.Sol.Ast;

public sealed partial class AstSnapshot
{
    private static ImmutableArray<AstParameter> Parameters(ImmutableArray<AstNode> children)
    {
        var parameters = ImmutableArray.CreateBuilder<AstParameter>();
        var list = children.FirstOrDefault(n => n.SyntaxKind == NodeKind.ParameterList);
        if (list is null)
        {
            return [];
        }
        foreach (var group in list.Children)
        {
            if (group.Origin.Element is not PsiParameterGroup psi)
            {
                continue;
            }
            var type = group.Children.OfType<AstType>().FirstOrDefault();
            var value = group.Children.FirstOrDefault(n => ReferenceEquals(n.Origin.Element, psi.DefaultValue));
            var mode = psi.PassingMode switch
            {
                ParameterPassingMode.Ref => AstPassingMode.Ref,
                ParameterPassingMode.Out => AstPassingMode.Out,
                ParameterPassingMode.Vararg => AstPassingMode.Vararg,
                _ => AstPassingMode.Value,
            };
            foreach (var name in psi.Names)
            {
                parameters.Add(new(name.Text, type, mode, value, psi.DeclaresProperty,
                    psi.Children.Any(n => n.Kind == NodeKind.Modifier && n.Text == "var"),
                    new(name), group.Origin)
                {
                    Annotations = group.Children.Where(child => child.SyntaxKind == NodeKind.Annotation)
                        .ToImmutableArray(),
                });
            }
        }
        return parameters.ToImmutable();
    }

    private static ImmutableArray<AstArgument> Arguments(ImmutableArray<AstNode> children)
    {
        var arguments = ImmutableArray.CreateBuilder<AstArgument>();
        var list = children.FirstOrDefault(n => n.SyntaxKind == NodeKind.ArgumentList);
        if (list is not null)
        {
            foreach (var argument in list.Children)
            {
                string? name = argument.Children.FirstOrDefault(n => n.SyntaxKind == NodeKind.Name)?.Origin.Text;
                var value = argument.Children.LastOrDefault();
                if (value is null) { continue; }
                var mode = value.SyntaxKind switch
                {
                    NodeKind.RefArgument => AstArgumentMode.Ref,
                    NodeKind.OutArgument => AstArgumentMode.Out,
                    NodeKind.OutVariable => AstArgumentMode.OutVariable,
                    NodeKind.SpreadArgument => AstArgumentMode.Spread,
                    _ => AstArgumentMode.Value,
                };
                if (mode is AstArgumentMode.Ref or AstArgumentMode.Out or AstArgumentMode.Spread)
                {
                    value = value.Children.FirstOrDefault() ?? value;
                }
                arguments.Add(new(name, mode, value, argument.Origin));
            }
        }
        foreach (var lambda in children.OfType<AstLambda>())
        {
            arguments.Add(new(null, AstArgumentMode.Value, lambda, lambda.Origin, IsTrailingLambda: true));
        }
        return arguments.ToImmutable();
    }

    private static ImmutableArray<AstImportItem> ImportItems(AstOrigin origin)
    {
        var names = origin.Element.Children.SkipWhile(n => n.Kind != NodeKind.QualifiedName)
            .Skip(1).Where(n => n.Kind == NodeKind.Name).ToArray();
        var result = ImmutableArray.CreateBuilder<AstImportItem>();
        for (int index = 0; index < names.Length; index++)
        {
            var local = names[index];
            bool renamed = index + 1 < names.Length && origin.Element.Tokens.Any(t =>
                t.Kind == SyntaxKind.EqualsToken && t.Span.Start >= local.Span.End
                && t.Span.End <= names[index + 1].Span.Start);
            string original = renamed ? names[++index].Text : local.Text;
            result.Add(new(local.Text, original, new(local)));
        }
        return result.ToImmutable();
    }
}
