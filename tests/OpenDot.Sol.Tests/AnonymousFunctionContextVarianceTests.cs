// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionContextVarianceTests
{
    private const string Core = "module std.core\n"
        + "open class Object {}\nopen class Animal: Object {}\nclass Dog: Animal {}\n"
        + "class Text: Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\nopen class IOException: Exception {}\n"
        + "open class OtherException: Exception {}\n";

    [Fact]
    public void ContextAllowsContravariantParametersAndCovariantResults()
    {
        var analysis = Analyze("func use() { val callback: (Dog) -> Animal = "
            + "func(value: Object): Dog { for {} } }");
        var function = AnonymousFunction(analysis);
        var actual = Assert.IsType<ResolvedType>(analysis.GetExpression(function)?.Type);
        var declaration = analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstProperty>().Single(property => property.Name == "callback");
        var expected = analysis.Sources.Types.GetType(declaration.Type!);

        Assert.Equal("std.core.Object", actual.Arguments[0].Name);
        Assert.Equal("std.core.Dog", actual.Arguments[^1].Name);
        Assert.Equal(IntrinsicConversion.Reference,
            analysis.Types.ClassifyConversion(actual, expected, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2220");
    }

    [Fact]
    public void RefAndOutParameterModesAndTypesRemainInvariant()
    {
        var exact = Analyze("func use() { val byRef: (ref int) -> () = func(ref value: int) { }; "
            + "val byOut: (out int) -> () = func(out value: int) { value = 1 } }");
        var exactFunctions = exact.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstAnonymousFunction>().OrderBy(function => function.Origin.Span.Start).ToArray();

        Assert.False(exact.HasErrors, Diagnostics(exact));
        Assert.Equal(new[] { AstPassingMode.Ref, AstPassingMode.Out }, exactFunctions
            .Select(function => Assert.IsType<ResolvedType>(exact.GetExpression(function)?.Type).ParameterModes.Single()));
        Assert.True(exact.IsComplete);

        var changedMode = Analyze("func use() { val callback: (ref int) -> () = func(value: int) { } }");
        Assert.Contains(changedMode.Diagnostics, diagnostic => diagnostic.Code == "SOL2220");

        var changedStorage = Analyze("func use() { val callback: (ref Text) -> () = func(ref value: Object) { } }");
        Assert.Contains(changedStorage.Diagnostics, diagnostic => diagnostic.Code == "SOL2220");
    }

    [Fact]
    public void NullableFunctionResultsUseTheOrdinaryCovariantConversionRules()
    {
        var accepted = Analyze("func use() { val callback: () -> Animal? = func(): Dog? { null } }");
        var acceptedFunction = AnonymousFunction(accepted);
        var acceptedType = Assert.IsType<ResolvedType>(accepted.GetExpression(acceptedFunction)?.Type);
        var acceptedDeclaration = accepted.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstProperty>().Single(property => property.Name == "callback");
        var acceptedExpected = accepted.Sources.Types.GetType(acceptedDeclaration.Type!);

        Assert.False(accepted.HasErrors, Diagnostics(accepted));
        Assert.Equal(IntrinsicConversion.Reference,
            accepted.Types.ClassifyConversion(acceptedType, acceptedExpected, TestContext.Current.CancellationToken));

        var rejected = Analyze("func use() { val callback: () -> Animal = func(): Dog? { null } }");
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
    }

    [Fact]
    public void ContextualFunctionEffectsActAsACheckedExceptionBudget()
    {
        var accepted = Analyze("func fail(): int throws IOException = 1\n"
            + "func use() { val callback: () -> int throws IOException = func() { fail() } }");
        var acceptedFunction = AnonymousFunction(accepted);
        var acceptedType = Assert.IsType<ResolvedType>(accepted.GetExpression(acceptedFunction)?.Type);

        Assert.False(accepted.HasErrors, Diagnostics(accepted));
        Assert.Contains(acceptedType.Effects, effect => effect.Declaration?.Name == "IOException");
        Assert.True(accepted.IsComplete);

        var rejected = Analyze("func fail(): int throws IOException = 1\n"
            + "func use() { val callback: () -> int = func() { fail() } }");
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Code == "SOL2253");
    }

    [Fact]
    public void ExplicitThrowsCannotWidenTheExpectedFunctionEffectBudget()
    {
        var analysis = Analyze("func fail(): int throws IOException = 1\n"
            + "func use() { val callback: () -> int throws OtherException = "
            + "func(): int throws OtherException { fail() } }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2250");
    }

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session("module app\n" + source, Core);
        var declarations = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(declarations.HasErrors,
            string.Join('\n', declarations.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(declarations, TestContext.Current.CancellationToken);
    }

    private static AstAnonymousFunction AnonymousFunction(BodyAnalysis analysis) =>
        Assert.Single(analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstAnonymousFunction>());

    private static string Diagnostics(BodyAnalysis analysis) => string.Join('\n', analysis.Diagnostics
        .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message));
}
