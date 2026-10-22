// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

internal sealed partial class AnalysisBuilder
{
    private void ValidateDeclaration(AstDeclaration declaration)
    {
        foreach (var duplicate in declaration.Modifiers.GroupBy(m => m, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            Report("SOL2004", "Duplicate modifier: " + duplicate.Key, declaration);
        }
        if (declaration.Modifiers.Count(m => m is "public" or "private" or "protected" or "internal") > 1)
        {
            Report("SOL2004", "A declaration has conflicting visibility modifiers.", declaration);
        }
        if (declaration.Modifiers.Contains("final") && declaration.Modifiers.Contains("open"))
        {
            Report("SOL2004", "A declaration cannot be both final and open.", declaration);
        }
        if (declaration.IsPartial && declaration.SyntaxKind is not (NodeKind.ClassDeclaration
            or NodeKind.StructDeclaration or NodeKind.RefStructDeclaration or NodeKind.ObjectDeclaration))
        {
            Report("SOL2030", "Partial is allowed only on class, struct, ref struct, and object declarations.", declaration);
        }
        var typeParameters = declaration switch
        {
            AstTypeDeclaration type => type.TypeParameters,
            AstCallable callable => callable.TypeParameters,
            _ => [],
        };
        foreach (var duplicate in typeParameters.GroupBy(p => p.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            Report("SOL2005", "Duplicate type parameter: " + duplicate.Key, duplicate.Last());
        }
        if (declaration is AstCallable function)
        {
            ValidateParameters(function);
        }
        if (declaration is AstTypeDeclaration nominal && nominal.PrimaryConstructor is not null)
        {
            ValidateParameters(nominal.PrimaryConstructor);
        }
        if (declaration is AstProperty property)
        {
            foreach (var duplicate in property.Accessors.GroupBy(a => a.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                Report("SOL2006", "Duplicate property accessor: " + duplicate.Key, duplicate.Last());
            }
            if (!property.IsMutable && property.Accessors.Any(a => a.Name == "set"))
            {
                Report("SOL2006", "A val property cannot declare a set accessor.", property);
            }
        }
    }

    private void ValidateParameters(AstCallable callable)
    {
        foreach (var group in callable.Parameters.GroupBy(p => p.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            Report("SOL2005", "Duplicate parameter name: " + group.Key, callable);
        }
        if (callable.Parameters.Count(p => p.PassingMode == AstPassingMode.Vararg) > 1)
        {
            Report("SOL2005", "Only one vararg parameter is permitted.", callable);
        }
        if (callable.Modifiers.Contains("override") && callable.Parameters.Any(p => p.DefaultValue is not null))
        {
            Report("SOL2135", "An override inherits defaults and cannot declare its own default expressions.", callable);
        }
        for (int index = 0; index < callable.Parameters.Length; index++)
        {
            var parameter = callable.Parameters[index];
            int groupSize = callable.Parameters.Count(p => ReferenceEquals(p.GroupOrigin.Element, parameter.GroupOrigin.Element));
            bool firstInGroup = index == 0 || !ReferenceEquals(callable.Parameters[index - 1].GroupOrigin.Element, parameter.GroupOrigin.Element);
            if (groupSize > 1 && firstInGroup && (parameter.DefaultValue is not null
                || parameter.PassingMode == AstPassingMode.Vararg || parameter.IsProperty))
            {
                Report("SOL2005", "Default, vararg, and constructor property parameters require a single-name group.", callable);
            }
            if (parameter.PassingMode is AstPassingMode.Ref or AstPassingMode.Out && parameter.DefaultValue is not null)
            {
                Report("SOL2005", "A ref/out parameter cannot have a default expression.", callable);
            }
        }
    }

    private void ValidatePartial(ImmutableArray<AstDeclaration> declarations)
    {
        if (declarations.Length < 2) { return; }
        var first = (AstTypeDeclaration)declarations[0];
        bool supported = first.SyntaxKind is NodeKind.ClassDeclaration or NodeKind.StructDeclaration
            or NodeKind.RefStructDeclaration or NodeKind.ObjectDeclaration;
        foreach (var part in declarations.Skip(1).OfType<AstTypeDeclaration>())
        {
            if (!supported || !first.IsPartial || !part.IsPartial)
            {
                Report("SOL2030", "Every part of a repeated type must explicitly be a supported partial type.", part);
            }
            if (part.SyntaxKind != first.SyntaxKind)
            {
                Report("SOL2030", "Partial type kinds do not match.", part);
            }
            if (Visibility(part) != Visibility(first))
            {
                Report("SOL2030", "Partial type visibility does not match.", part);
            }
            var firstModifiers = first.Modifiers.Where(m => m is not ("partial" or "public" or "private" or "internal" or "protected")).Order(StringComparer.Ordinal);
            var partModifiers = part.Modifiers.Where(m => m is not ("partial" or "public" or "private" or "internal" or "protected")).Order(StringComparer.Ordinal);
            if (!firstModifiers.SequenceEqual(partModifiers, StringComparer.Ordinal))
            {
                Report("SOL2030", "Partial type modifiers do not match.", part);
            }
            var firstParameters = first.TypeParameters.Select(p => p.Name + ":" + p.Constraint?.Shape.CanonicalName);
            var partParameters = part.TypeParameters.Select(p => p.Name + ":" + p.Constraint?.Shape.CanonicalName);
            if (!firstParameters.SequenceEqual(partParameters, StringComparer.Ordinal)
                || Constraints(first) != Constraints(part))
            {
                Report("SOL2030", "Partial type parameters or constraints do not match.", part);
            }
        }
        var initialized = declarations.OfType<AstTypeDeclaration>().Where(t => t.HasInitialization).ToArray();
        foreach (var part in initialized.Skip(1))
        {
            Report("SOL2030", "Only one partial type part may contain initialization.", part);
        }
    }

    private static string Visibility(AstDeclaration declaration)
    {
        return declaration.Modifiers.FirstOrDefault(m => m is "public" or "private" or "internal" or "protected") ?? "public";
    }

    private static string Constraints(AstDeclaration declaration)
    {
        var clauses = declaration.Children.Where(n => n.SyntaxKind == NodeKind.WhereClause)
            .SelectMany(n => n.Children).Select(n => string.Concat(n.Origin.Element.Tokens.Select(t =>
                n.Origin.Source.GetText(t.Span)))).Order(StringComparer.Ordinal);
        return string.Join(';', clauses);
    }
}
