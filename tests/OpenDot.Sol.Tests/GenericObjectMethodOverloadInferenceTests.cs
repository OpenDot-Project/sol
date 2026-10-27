// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericObjectMethodOverloadInferenceTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\nopen class RuntimeException: Exception {}";

    private static BodyAnalysis Analyze(string source)
    {
        var declarations = AstTestSources.Session(new[] { "module app\n" + source, Core });
        var sources = SourceAnalysis.Create(declarations, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Code + " " + d.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void EqualRankGenericAndConcreteMethodsSelectTheNonGenericDeclaration()
    {
        var analysis = Analyze("class Text {}\nobject Utility {\n"
            + "func choose(value: Text): Text = value\n"
            + "func <T> choose(value: T): T = value\n"
            + "}\nfunc use(value: Text): Text = Utility.choose(value)");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = Assert.Single(analysis.Calls);
        Assert.Equal("choose", call.Target.Name);
        Assert.Empty(analysis.Sources.Types.GetTypeParameters(call.Target));
        Assert.Empty(call.TypeArguments);
        Assert.Equal("app.Text", call.ReturnType!.Name);
        Assert.Equal("app.Text", Assert.Single(call.ParameterTypes).Name);
    }

    [Fact]
    public void FirRetainsTheSelectedNonGenericObjectOverload()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("import std.stdio\n"
            + "object Utility { func choose(value: String): String = value\n"
            + "func <T> choose(value: T): T = value }\n"
            + "object Main { func main(args: []String) throws IOException { "
            + "val chosen = Utility.choose(\"concrete\"); println(chosen) } }")], TestContext.Current.CancellationToken);

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var program = compilation.Program.Program;
        var main = Assert.Single(program.Functions, function => function.Name == "main");
        var call = Assert.Single(main.Body!.Statements.OfType<FirDeclare>()
            .Select(declaration => declaration.Initializer).OfType<FirCall>());
        var target = program.Functions[call.Target];
        Assert.Equal("choose", target.Name);
        Assert.Empty(target.SpecializationArguments);
        Assert.Equal("std.core.String", Assert.Single(target.Parameters).Type.Name);
        Assert.Single(program.Functions, function => function.Name == "choose");
    }

    [Fact]
    public void BetterGenericConversionBeatsAnApplicableConcreteOverload()
    {
        var analysis = Analyze("open class Base {}\nclass Child: Base {}\nobject Utility {\n"
            + "func select(value: Base): Base = value\n"
            + "func <T> select(value: T): T = value\n"
            + "}\nfunc use(value: Child): Child = Utility.select(value)");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = Assert.Single(analysis.Calls);
        Assert.Single(analysis.Sources.Types.GetTypeParameters(call.Target));
        Assert.Equal("app.Child", Assert.Single(call.TypeArguments).Name);
        Assert.Equal("app.Child", call.ReturnType!.Name);
    }

    [Fact]
    public void SameNamedGenericMethodsFromDifferentObjectsKeepDeclarationIdentity()
    {
        var analysis = Analyze("object Left { func <T> identity(value: T): T = value }\n"
            + "object Right { func <T> identity(value: T): T = value }\n"
            + "func use(value: int): int { val left = Left.identity(value); Right.identity<int>(left) }");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var calls = analysis.Calls.Where(binding => binding.Target.Name == "identity").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.NotSame(calls[0].Target, calls[1].Target);
        Assert.NotSame(calls[0].Target.Origin.Element, calls[1].Target.Origin.Element);
        Assert.Equal(new[] { "int", "int" }, calls.Select(call => Assert.Single(call.TypeArguments).Name));
    }

    [Fact]
    public void ExplicitAndInferredMethodArgumentsProduceTheSameClosedSignature()
    {
        var analysis = Analyze("object Utility { func <T> identity(value: T): T = value }\n"
            + "func use(value: int): int { val inferred = Utility.identity(value); Utility.identity<int>(inferred) }");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var calls = analysis.Calls.Where(binding => binding.Target.Name == "identity").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call => Assert.Equal("int", Assert.Single(call.TypeArguments).Name));
        Assert.All(calls, call => Assert.Equal("int", Assert.Single(call.ParameterTypes).Name));
        Assert.All(calls, call => Assert.Equal("int", call.ReturnType!.Name));
        Assert.Same(calls[0].Target, calls[1].Target);
    }

    [Fact]
    public void ExpectedResultInfersAParameterThatHasNoArgumentEvidence()
    {
        var analysis = Analyze("class Text {}\nobject Utility { func <T> create(): T throws Exception { throw Exception() } }\n"
            + "func use(): Text throws Exception = Utility.create()");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = Assert.Single(analysis.Calls);
        Assert.Equal("app.Text", Assert.Single(call.TypeArguments).Name);
        Assert.Equal("app.Text", call.ReturnType!.Name);
    }

    [Fact]
    public void ExpectedResultInfersGenericMethodWhenItsOnlyArgumentIsDefaulted()
    {
        var analysis = Analyze("object Utility { func <T> withDefault(value: T = 1): T = value }\n"
            + "func use(): int = Utility.withDefault()");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = Assert.Single(analysis.Calls);
        Assert.Equal("int", Assert.Single(call.TypeArguments).Name);
        Assert.Equal("int", Assert.Single(call.ParameterTypes).Name);
        Assert.Equal("int", call.ReturnType!.Name);
        Assert.Equal(new[] { 0 }, call.DefaultParameters);
    }

    [Fact]
    public void ExpectedResultCannotRepairAnExplicitGenericArgumentMismatch()
    {
        var analysis = Analyze("class Text {}\nobject Utility { func <T> identity(value: T): T = value }\n"
            + "func use(value: int): Text = Utility.identity<Text>(value)");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.Empty(analysis.Calls);
    }

    [Fact]
    public void GenericObjectMethodInfersTypeArgumentsFromTypedAndPositionalLambdas()
    {
        var analysis = Analyze("object Utility {\n"
            + "func <T, R> apply(callback: (T) -> R): R throws Exception { throw Exception() }\n"
            + "func <T, R> relay(seed: T, callback: (T) -> R): R throws Exception { throw Exception() }\n"
            + "}\nfunc use(): () throws Exception {\n"
            + "Utility.apply({ value: String -> value })\n"
            + "Utility.relay(\"seed\", { $0 })\n"
            + "}");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var calls = analysis.Calls.Where(binding => binding.Target.Name is "apply" or "relay")
            .OrderBy(binding => binding.Target.Name).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call => Assert.True(new[] { "std.core.String", "std.core.String" }
            .SequenceEqual(call.TypeArguments.Select(type => type.Name)),
            call.Target.Name + " inferred [" + string.Join(", ", call.TypeArguments.Select(type => type.Kind + ":" + type.Name)) + "]"));
        Assert.All(calls, call => Assert.Equal("std.core.String", call.ReturnType!.Name));
    }

    [Fact]
    public void GenericObjectMethodDoesNotInferFromAnOpenPositionalLambdaSelfReference()
    {
        var analysis = Analyze("object Utility { func <T> consume(callback: (T) -> T): T throws Exception { throw Exception() } }\n"
            + "func use(): () throws Exception { Utility.consume({ $0 }) }");

        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "consume");
    }

    [Fact]
    public void MissingArgumentAndResultEvidenceDoesNotInventMethodTypeArguments()
    {
        var analysis = Analyze("object Utility { func <T> create(): T throws Exception { throw Exception() } }\n"
            + "func use(): () { val result = Utility.create() }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.Empty(analysis.Calls);
    }
}
