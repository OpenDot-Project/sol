// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AstExpressionTests
{
    [Fact]
    public void OversizedPositionalIndexDoesNotTurnALambdaIntoAHeaderlessForm()
    {
        var ast = AstTestSources.Parse("val callback = { $999999999999999999999999 }");
        var lambda = Assert.IsType<AstLambda>(Assert.Single(ast.Root.Declarations.OfType<AstProperty>()).Initializer);
        Assert.Equal(AstLambdaForm.Positional, lambda.Form);
        Assert.True(lambda.HasUnrepresentablePosition);
        Assert.Empty(lambda.Positions);
    }

    [Theory]
    [InlineData("a + b", "+")]
    [InlineData("a * b", "*")]
    [InlineData("a == b", "==")]
    [InlineData("a === b", "===")]
    [InlineData("a && b", "&&")]
    [InlineData("a || b", "||")]
    [InlineData("a ?: b", "?:")]
    [InlineData("a >> b", ">>")]
    [InlineData("a as int", "as")]
    [InlineData("a as? int", "as?")]
    [InlineData("a is int", "is")]
    [InlineData("a !is int", "!is")]
    [InlineData("-a", "-")]
    [InlineData("!a", "!")]
    [InlineData("a!!", "!!")]
    [InlineData("++a", "++")]
    [InlineData("a++", "++")]
    public void OperatorsExcludeCommentsAndOperandTokens(string expression, string operation)
    {
        var ast = AstTestSources.Parse("val value = " + expression);
        var node = Assert.IsType<AstOperation>(Assert.Single(ast.Root.Declarations.OfType<AstProperty>()).Initializer);
        Assert.Equal(operation, node.Operation);
        Assert.Equal(operation is "&&" or "||" or "?:", node.IsShortCircuit);
    }

    [Fact]
    public void PostfixAndPrefixPositionsRemainDistinct()
    {
        var ast = AstTestSources.Parse("val prefix = ++value\nval postfix = value++");
        var operations = ast.Root.Declarations.OfType<AstProperty>().Select(p => Assert.IsType<AstOperation>(p.Initializer)).ToArray();
        Assert.False(operations[0].IsPostfix);
        Assert.True(operations[1].IsPostfix);
    }

    [Fact]
    public void SafeMemberRetainsReceiverAndMemberName()
    {
        var ast = AstTestSources.Parse("val x = source?.length\nval y = source.length");
        var members = ast.Root.Declarations.OfType<AstProperty>().Select(p => Assert.IsType<AstMember>(p.Initializer)).ToArray();
        Assert.True(members[0].IsSafe);
        Assert.False(members[1].IsSafe);
        Assert.Equal("source", Assert.IsType<AstAtom>(members[0].Receiver).Spelling);
        Assert.Equal("length", members[0].Name);
    }

    [Fact]
    public void CallsRetainArgumentOrderNamesAndPassingModes()
    {
        var ast = AstTestSources.Parse("val x = f(1, named = ref location, out other, created = out var result, *rest) { 4 }");
        var call = Assert.IsType<AstCall>(Assert.Single(ast.Root.Declarations.OfType<AstProperty>()).Initializer);
        Assert.Equal(6, call.Arguments.Length);
        Assert.Equal(new[] { AstArgumentMode.Value, AstArgumentMode.Ref, AstArgumentMode.Out,
            AstArgumentMode.OutVariable, AstArgumentMode.Spread, AstArgumentMode.Value }, call.Arguments.Select(a => a.Mode));
        Assert.Equal("named", call.Arguments[1].Name);
        Assert.Equal("location", call.Arguments[1].Value.Origin.Text);
        Assert.Equal("created", call.Arguments[3].Name);
        Assert.Equal(NodeKind.OutVariable, call.Arguments[3].Value.SyntaxKind);
        Assert.IsType<AstLambda>(call.Arguments[^1].Value);
    }

    [Fact]
    public void GenericCallTargetKeepsExplicitTypeArguments()
    {
        var ast = AstTestSources.Parse("val value = identity<Box<int>>(source)");
        var call = Assert.IsType<AstCall>(Assert.Single(ast.Root.Declarations.OfType<AstProperty>()).Initializer);
        var generic = Assert.IsType<AstGenericApplication>(call.Target);
        Assert.Equal("identity", generic.Target!.Origin.Text);
        Assert.Equal("Box<int>", Assert.Single(generic.TypeArguments).Shape.CanonicalName);
    }

    [Theory]
    [InlineData("(value)", true, 1)]
    [InlineData("(value,)", false, 1)]
    [InlineData("()", false, 0)]
    [InlineData("(a,b)", false, 2)]
    [InlineData("[a,b]", false, 2)]
    public void GroupingTupleAndArrayRemainDistinguishable(string text, bool grouping, int count)
    {
        var ast = AstTestSources.Parse("val x = " + text);
        var sequence = Assert.IsType<AstSequence>(Assert.Single(ast.Root.Declarations.OfType<AstProperty>()).Initializer);
        Assert.Equal(grouping, sequence.IsGrouping);
        Assert.Equal(count, sequence.Elements.Count());
    }

    [Fact]
    public void PositionalLambdaDoesNotCaptureNestedLambdaPositionsAsItsArity()
    {
        var ast = AstTestSources.Parse("val callback = { val nested = { $5 }; $0 + $1 }");
        var lambda = Assert.IsType<AstLambda>(Assert.Single(ast.Root.Declarations.OfType<AstProperty>()).Initializer);
        Assert.Equal(AstLambdaForm.Positional, lambda.Form);
        Assert.Equal(new[] { 0, 1 }, lambda.Positions);
        Assert.Equal(2, lambda.MinimumPositionalArity);
        var nested = Assert.Single(lambda.Descendants().OfType<AstLambda>());
        Assert.Equal(new[] { 5 }, nested.Positions);
        Assert.Equal(6, nested.MinimumPositionalArity);
    }

    [Fact]
    public void NamedHeaderAndHeaderlessLambdaRemainDifferent()
    {
        var ast = AstTestSources.Parse("val named = { x, y -> x + y }\nval empty = { -> 42 }\nval bare = { 42 }");
        var lambdas = ast.Root.Declarations.OfType<AstProperty>().Select(p => Assert.IsType<AstLambda>(p.Initializer)).ToArray();
        Assert.Equal(AstLambdaForm.Named, lambdas[0].Form);
        Assert.Equal(2, lambdas[0].Parameters.Count());
        Assert.Equal(AstLambdaForm.Named, lambdas[1].Form);
        Assert.Empty(lambdas[1].Parameters);
        Assert.Equal(AstLambdaForm.Headerless, lambdas[2].Form);
        Assert.Empty(lambdas[2].Positions);
    }

    [Fact]
    public void LabelledReturnIsNotAnOuterFunctionReturn()
    {
        var ast = AstTestSources.Parse("val callback = done@ { return@done 42 }");
        var labelled = Assert.IsType<AstLabelledExpression>(Assert.Single(ast.Root.Declarations.OfType<AstProperty>()).Initializer);
        Assert.Equal("done", labelled.Label);
        var returned = Assert.Single(labelled.Lambda!.Statements.OfType<AstReturn>());
        Assert.Equal("done", returned.Label);
        Assert.Equal("42", returned.Value!.Origin.Text);
    }

    [Fact]
    public void IfAndForExposeStructuredControlFlowWithoutConstructingCFG()
    {
        var ast = AstTestSources.Parse("func f() { if ready { use() } else { stop() }; for {}; for ready {}; for var i = 0; i < 4; i += 1 {}; for val x in values {} }");
        var conditional = Assert.Single(ast.Root.Descendants().OfType<AstIf>());
        Assert.Equal("ready", conditional.Condition!.Origin.Text);
        Assert.NotNull(conditional.Then);
        Assert.IsType<AstBlock>(conditional.Else);
        var loops = ast.Root.Descendants().OfType<AstFor>().ToArray();
        Assert.Equal(new[] { AstForForm.Infinite, AstForForm.Condition, AstForForm.ThreeClause, AstForForm.Each }, loops.Select(l => l.Form));
        Assert.Equal("ready", loops[1].Condition!.Origin.Text);
        Assert.NotNull(loops[2].Initializer);
        Assert.Equal("+=", loops[2].Update!.Operation);
        Assert.Equal("x", loops[3].Binding!.Name);
        Assert.Equal("values", loops[3].Iterable!.Origin.Text);
    }

    [Fact]
    public void SwitchPatternsAndDefaultStayInSourceOrder()
    {
        var ast = AstTestSources.Parse("func f() { switch value { case Result.Value(x): use(x); case is String: use(); default: stop() } }");
        var statement = Assert.Single(ast.Root.Descendants().OfType<AstSwitch>());
        var cases = statement.Cases.ToArray();
        Assert.Equal(3, cases.Length);
        Assert.Equal(NodeKind.PayloadPattern, cases[0].Pattern!.SyntaxKind);
        Assert.Equal(NodeKind.TypePattern, cases[1].Pattern!.SyntaxKind);
        Assert.True(cases[2].IsDefault);
        Assert.Null(cases[2].Pattern);
        Assert.Single(cases[2].Statements);
    }

    [Fact]
    public void TryResultHasIndependentCatchAndFinallyBodies()
    {
        var ast = AstTestSources.Parse("val value = try { load() } catch (failure: IOException) { recover() } finally { close() }");
        var expression = Assert.IsType<AstTry>(Assert.Single(ast.Root.Declarations.OfType<AstProperty>()).Initializer);
        Assert.True(expression.IsValue);
        Assert.NotNull(expression.Body);
        Assert.NotNull(expression.Finally);
        var handler = Assert.Single(expression.Catches);
        Assert.Equal("failure", handler.Name);
        Assert.Equal("IOException", handler.ExceptionType!.Shape.CanonicalName);
        Assert.NotNull(handler.Body);
    }
}
