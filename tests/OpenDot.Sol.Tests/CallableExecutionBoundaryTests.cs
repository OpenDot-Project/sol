// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class CallableExecutionBoundaryTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\nopen class IOException: Exception {}\n";

    [Fact]
    public void NestedLocalFunctionAndLambdaReturnsDoNotJoinTheOuterAnonymousResult()
    {
        var (analysis, anonymousFunctions, lambdas, functions) = Analyze("func use(flag: bool) { "
            + "val outer = func() { func nested(): String { return \"named\" }; "
            + "val nestedLambda: () -> String = inner@ { return@inner \"lambda\" }; "
            + "if flag { return 1 }; 2 } }");
        var outer = Assert.Single(anonymousFunctions);
        var nestedLambda = Assert.Single(lambdas);
        var nestedFunction = Assert.Single(functions, function => function.Name == "nested");
        var outerType = Assert.IsType<ResolvedType>(analysis.GetExpression(outer)?.Type);
        var labelledLambda = Assert.Single(analysis.Expressions, binding => binding.Expression is AstLabelledExpression
            labelled && ReferenceEquals(labelled.Lambda, nestedLambda));
        var lambdaType = Assert.IsType<ResolvedType>(labelledLambda.Type);
        var localBody = Assert.IsType<BodySummary>(analysis.GetBody(nestedFunction));

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(analysis.IsComplete, Diagnostics(analysis) + " Unverified: "
            + string.Join(", ", analysis.UnverifiedNodes.Select(node => node.Origin.Text)));
        Assert.Equal("int", outerType.Arguments[^1].Name);
        Assert.Equal("std.core.String", lambdaType.Arguments[^1].Name);
        Assert.Equal("std.core.String", Assert.IsType<ResolvedType>(localBody.ReturnType).Name);
    }

    [Fact]
    public void NestedCallableEffectsStayOnTheirOwnSignatures()
    {
        var (analysis, anonymousFunctions, lambdas, functions) = Analyze("func fail(): int throws IOException = 1\n"
            + "func use() { val outer = func(): int { "
            + "func nested(): int throws IOException { fail() }; "
            + "val nestedLambda: () -> int throws IOException = { -> fail() }; 1 } }");
        var outer = Assert.Single(anonymousFunctions);
        var nestedLambda = Assert.Single(lambdas);
        var nestedFunction = Assert.Single(functions, function => function.Name == "nested");
        var outerType = Assert.IsType<ResolvedType>(analysis.GetExpression(outer)?.Type);
        var lambdaType = Assert.IsType<ResolvedType>(analysis.GetExpression(nestedLambda)?.Type);
        var localBody = Assert.IsType<BodySummary>(analysis.GetBody(nestedFunction));

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(analysis.IsComplete, Diagnostics(analysis) + " Unverified: "
            + string.Join(", ", analysis.UnverifiedNodes.Select(node => node.Origin.Text)));
        Assert.Empty(outerType.Effects);
        Assert.Contains(lambdaType.Effects, effect => effect.Declaration?.Name == "IOException");
        Assert.Contains(localBody.EscapingEffects, effect => effect.Declaration?.Name == "IOException");
    }

    [Fact]
    public void NestedCallableAssignmentsCannotSatisfyAnEnclosingOutParameter()
    {
        var (analysis, anonymousFunctions, lambdas, functions) = Analyze("func use() { "
            + "val namedOwner = func(out namedResult: int): int { "
            + "func assignNamed(): () { namedResult = 1 }; 2 }; "
            + "val lambdaOwner = func(out lambdaResult: int): int { "
            + "val assignLambda: () -> () = { -> lambdaResult = 1 }; 2 }; "
            + "val anonymousOwner = func(out anonymousResult: int): int { "
            + "val assignAnonymous = func(): () { anonymousResult = 1 }; 2 } }");
        var owners = anonymousFunctions.Where(function => function.Parameters.Any(parameter =>
            parameter.PassingMode == AstPassingMode.Out)).ToArray();
        var nestedFunction = Assert.Single(functions, function => function.Name == "assignNamed");
        var lambda = Assert.Single(lambdas);
        var nestedAnonymous = Assert.Single(anonymousFunctions, function => function.Parameters.IsEmpty);

        Assert.Equal(3, owners.Length);
        Assert.All(owners, owner =>
        {
            var summary = Assert.IsType<AnonymousFunctionBodySummary>(analysis.GetAnonymousFunctionBody(owner));
            Assert.False(Assert.Single(summary.OutParameters).AssignedOnEveryNormalExit);
        });
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2242");
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");

        Assert.IsType<BodySummary>(analysis.GetBody(nestedFunction));
        Assert.IsType<ResolvedType>(analysis.GetExpression(lambda)?.Type);
        Assert.IsType<AnonymousFunctionBodySummary>(analysis.GetAnonymousFunctionBody(nestedAnonymous));
    }

    [Fact]
    public void NestedCallableBodiesDoNotMakeTheirDeclarationCatchable()
    {
        var (analysis, anonymousFunctions, lambdas, functions) = Analyze("func use() { "
            + "val namedOwner = func(): int { try { func nested(): int { 10 / 2 } } "
            + "catch (error: RuntimeException) {}; 1 }; "
            + "val lambdaOwner = func(): int { try { val nested: () -> int = { -> 10 / 2 } } "
            + "catch (error: RuntimeException) {}; 1 }; "
            + "val anonymousOwner = func(): int { try { val nested = func(): int { 10 / 2 } } "
            + "catch (error: RuntimeException) {}; 1 } }");
        var nestedFunction = Assert.Single(functions, function => function.Name == "nested");
        var lambda = Assert.Single(lambdas);
        var nestedAnonymous = anonymousFunctions.Last();

        Assert.Equal(4, anonymousFunctions.Length);
        Assert.Equal(3, analysis.Diagnostics.Count(diagnostic => diagnostic.Code == "SOL2255"));
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code is "SOL2240" or "SOL2242");
        Assert.Equal("int", Assert.IsType<ResolvedType>(analysis.GetBody(nestedFunction)?.ReturnType).Name);
        Assert.Equal("int", Assert.IsType<ResolvedType>(analysis.GetExpression(lambda)?.Type).Arguments[^1].Name);
        Assert.Equal("int", Assert.IsType<ResolvedType>(analysis.GetExpression(nestedAnonymous)?.Type).Arguments[^1].Name);
    }

    private static (BodyAnalysis Analysis, AstAnonymousFunction[] AnonymousFunctions,
        AstLambda[] Lambdas, AstFunction[] Functions) Analyze(string source)
    {
        var session = AstTestSources.Session("module app\n" + source, Core);
        var declarations = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(declarations.HasErrors,
            string.Join('\n', declarations.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var nodes = session.Files.SelectMany(file => file.Root.Descendants()).ToArray();
        return (BodyAnalysis.Create(declarations, TestContext.Current.CancellationToken),
            nodes.OfType<AstAnonymousFunction>().OrderBy(function => function.Origin.Span.Start).ToArray(),
            nodes.OfType<AstLambda>().OrderBy(lambda => lambda.Origin.Span.Start).ToArray(),
            nodes.OfType<AstFunction>().OrderBy(function => function.Origin.Span.Start).ToArray());
    }

    private static string Diagnostics(BodyAnalysis analysis) => string.Join('\n',
        analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message));
}
