// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Psi;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class EscapingAnnotationAstTests
{
    [Fact]
    public void UnqualifiedEscapingAnnotationRemainsOwnedByItsParameterGroup()
    {
        var ast = AstTestSources.Parse("@test\nfunc consume(@escaping callback: () -> int) { callback() }");
        var function = Assert.Single(ast.Root.Declarations.OfType<AstFunction>());
        var parameter = Assert.Single(function.Parameters);
        var group = Assert.IsType<PsiParameterGroup>(parameter.GroupOrigin.Element);
        var annotation = Assert.Single(group.Children.OfType<PsiAnnotation>());

        Assert.Equal("callback", parameter.Name);
        Assert.Equal("escaping", annotation.Name);
        Assert.Null(annotation.Target);
        Assert.Empty(annotation.Arguments);
        Assert.Equal("@escaping callback: () -> int", group.Text);
        Assert.Equal("test", Assert.IsType<PsiAnnotation>(Assert.Single(function.Annotations).Origin.Element).Name);
        Assert.Empty(ast.Psi.Syntax.Diagnostics);
    }

    [Fact]
    public void ExplicitParameterTargetIsRetainedWithoutBecomingAFunctionAnnotation()
    {
        var ast = AstTestSources.Parse("func consume(@parameter: escaping callback: () -> int) {}");
        var function = Assert.Single(ast.Root.Declarations.OfType<AstFunction>());
        var parameter = Assert.Single(function.Parameters);
        var group = Assert.IsType<PsiParameterGroup>(parameter.GroupOrigin.Element);
        var annotation = Assert.Single(group.Children.OfType<PsiAnnotation>());

        Assert.Equal("parameter", annotation.Target);
        Assert.Equal("escaping", annotation.Name);
        Assert.Empty(function.Annotations);
        Assert.Equal("callback", parameter.Name);
    }

    [Fact]
    public void GroupedNamesShareTheSameEscapingAnnotationOrigin()
    {
        var ast = AstTestSources.Parse("func consume(@escaping first, second: () -> int) {}");
        var function = Assert.Single(ast.Root.Declarations.OfType<AstFunction>());
        var parameters = function.Parameters.ToArray();
        var first = Assert.IsType<PsiParameterGroup>(parameters[0].GroupOrigin.Element);
        var second = Assert.IsType<PsiParameterGroup>(parameters[1].GroupOrigin.Element);

        Assert.Equal(new[] { "first", "second" }, parameters.Select(parameter => parameter.Name));
        Assert.Same(first, second);
        Assert.Equal("escaping", Assert.Single(first.Children.OfType<PsiAnnotation>()).Name);
        Assert.Equal("first, second", string.Join(", ", first.Names.Select(name => name.Text)));
    }

    [Fact]
    public void SeparateParameterGroupsKeepIndependentEscapeAnnotations()
    {
        var ast = AstTestSources.Parse("func consume(@escaping stored: () -> int, immediate: () -> int) {}");
        var function = Assert.Single(ast.Root.Declarations.OfType<AstFunction>());
        var parameters = function.Parameters.ToArray();

        Assert.Equal(2, parameters.Length);
        Assert.Equal("escaping", Assert.Single(Assert.IsType<PsiParameterGroup>(
            parameters[0].GroupOrigin.Element).Children.OfType<PsiAnnotation>()).Name);
        Assert.Empty(Assert.IsType<PsiParameterGroup>(parameters[1].GroupOrigin.Element)
            .Children.OfType<PsiAnnotation>());
        Assert.NotSame(parameters[0].GroupOrigin.Element, parameters[1].GroupOrigin.Element);
    }

    [Fact]
    public void RepeatedAnnotationSyntaxIsPreservedForSemanticValidation()
    {
        var ast = AstTestSources.Parse("func consume(@escaping @escaping callback: () -> int) {}");
        var parameter = Assert.Single(Assert.Single(ast.Root.Declarations.OfType<AstFunction>()).Parameters);
        var group = Assert.IsType<PsiParameterGroup>(parameter.GroupOrigin.Element);

        Assert.Equal(new[] { "escaping", "escaping" }, group.Children.OfType<PsiAnnotation>()
            .Select(annotation => annotation.Name));
        Assert.Empty(ast.Psi.Syntax.Diagnostics);
    }

    [Fact]
    public void ConstantArgumentSyntaxIsPreservedForSemanticValidation()
    {
        var ast = AstTestSources.Parse("func consume(@escaping(\"reason\") callback: () -> int) {}");
        var parameter = Assert.Single(Assert.Single(ast.Root.Declarations.OfType<AstFunction>()).Parameters);
        var annotation = Assert.Single(Assert.IsType<PsiParameterGroup>(parameter.GroupOrigin.Element)
            .Children.OfType<PsiAnnotation>());

        Assert.Equal("escaping", annotation.Name);
        Assert.Equal("\"reason\"", Assert.Single(annotation.Arguments).Text);
        Assert.Empty(ast.Psi.Syntax.Diagnostics);
    }
}
