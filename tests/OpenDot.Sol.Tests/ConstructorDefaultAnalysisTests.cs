// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class ConstructorDefaultAnalysisTests
{
    [Fact]
    public void RejectedLambdaOverloadDoesNotPublishConstructorDefaultBindings()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class C { constructor(value: int, extra: int = 1) {}"
            + " constructor(value: bool, extra: bool = false) {} }\n"
            + "func pick(callback: (int) -> C): int = 1;\n"
            + "func pick(callback: (bool) -> C): int = 2;\n"
            + "func use(): int = pick({ value -> val box = C(value); value + 1; box })");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
        var defaults = Assert.Single(analysis.ConstructionDefaults.Values, view => view.Site is AstCall);
        Assert.Equal("int", defaults.Constructor.Parameters[0].Type!.Origin.Text);
        Assert.Equal(BodyBindingStatus.Bound, defaults.Status);
    }

    [Fact]
    public void SelectedDefaultsRetainClosedBindingsWithoutPollutingOrdinaryExpressions()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Box<T>(val first: T, val second: T = first) {}\n"
            + "func one(): Box<int> = Box<int>(1)\nfunc two(): Box<bool> = Box<bool>(true, false)\n"
            + "func three(): Box<bool> = Box<bool>(true)");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
        var defaults = analysis.ConstructionDefaults.Values.Where(view => view.Site is AstCall).ToArray();
        Assert.Equal(2, defaults.Length);
        var integer = Assert.Single(defaults, view => view.Owner.Arguments[0].Name == "int");
        var boolean = Assert.Single(defaults, view => view.Owner.Arguments[0].Name == "bool");
        Assert.Equal([1], integer.Parameters); Assert.Equal([1], boolean.Parameters);
        var expression = Assert.IsType<AstExpression>(integer.Constructor.Parameters[1].DefaultValue, exactMatch: false);
        Assert.Same(analysis.Types.GetPrimitive("int"), integer.Expressions[expression].Type);
        Assert.Same(analysis.Types.GetPrimitive("bool"), boolean.Expressions[expression].Type);
        Assert.Equal(BodyBindingStatus.Bound, integer.Status); Assert.True(integer.CanCompleteNormally);
        Assert.Empty(integer.Diagnostics); Assert.Empty(integer.UnverifiedNodes);
        Assert.Null(analysis.GetExpression(expression));
    }

    [Fact]
    public void ConstructorViewKeepsBaseDefaultAndImplicitBaseSelections()
    {
        var analysis = GenericMemberProofTestSource.Analyze("open class Base<T>(val first: T, val second: T = first) {}\n"
            + "class Derived: Base<int>(3) {}\nopen class Empty {}\nclass Child: Empty {}");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
        var derived = analysis.Types.Nominal(analysis.Sources.Declarations.Modules["app"].Declarations.Single(s => s.Name == "Derived"), []);
        var body = analysis.GetConstructorBody(derived, null, TestContext.Current.CancellationToken);
        Assert.True(body.IsComplete);
        var defaults = Assert.Single(body.Defaults.Values, view => view.Owner.Declaration!.Name == "Base");
        Assert.Equal([1], defaults.Parameters);
        var expression = Assert.IsType<AstExpression>(defaults.Constructor.Parameters[1].DefaultValue, exactMatch: false);
        Assert.Same(analysis.Types.GetPrimitive("int"), defaults.Expressions[expression].Type);
        var child = analysis.Types.Nominal(analysis.Sources.Declarations.Modules["app"].Declarations.Single(s => s.Name == "Child"), []);
        var childBody = analysis.GetConstructorBody(child, null, TestContext.Current.CancellationToken);
        Assert.True(childBody.IsComplete);
        var edge = Assert.Single(childBody.Edges.Values, proof => proof.Selection.Owner.Declaration!.Name == "Empty");
        Assert.Null(edge.Selection.Constructor); Assert.Empty(edge.Selection.Arguments); Assert.Empty(edge.Selection.Defaults);
        Assert.False(edge.Selection.IsThisDelegation); Assert.Equal(BodyBindingStatus.Bound, edge.Status);
    }
}
