// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class IndirectFunctionValueCallMatrixTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\n";

    private static BodyAnalysis Analyze(string body)
    {
        var session = AstTestSources.Session("module app\n" + body, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void IndirectCallAcceptsItsExactFunctionTypeArity()
    {
        var analysis = Analyze("func combine(left: int, right: int): int = left + right\n"
            + "func use() { val callback: (int, int) -> int = combine; val result = callback(1, 2) }");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        Assert.True(analysis.IsComplete);
    }

    [Theory]
    [InlineData("callback(1)")]
    [InlineData("callback(1, 2, 3)")]
    public void IndirectCallRejectsEveryNonExactArity(string invocation)
    {
        var analysis = Analyze("func combine(left: int, right: int): int = left + right\n"
            + "func use() { val callback: (int, int) -> int = combine; val result = " + invocation + " }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
    }

    [Fact]
    public void IndirectCallsRejectNamedArgumentsEvenWhenTheirNamesMatchTheTargetFunction()
    {
        var analysis = Analyze("func combine(left: int, right: int): int = left + right\n"
            + "func use() { val callback: (int, int) -> int = combine; "
            + "val result = callback(right = 2, left = 1) }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
    }

    [Theory]
    [InlineData("change(value)")]
    [InlineData("change(out value)")]
    [InlineData("initializeCallback(value)")]
    [InlineData("initializeCallback(ref value)")]
    public void IndirectRefAndOutCallsRequireTheirExactArgumentMarkers(string invocation)
    {
        var analysis = Analyze("func increment(ref value: int): () { value += 1 }\n"
            + "func initialize(out value: int): () { value = 42 }\n"
            + "func use() { val change: (ref int) -> () = increment; "
            + "val initializeCallback: (out int) -> () = initialize; var value = 0; "
            + invocation + " }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2244");
    }

    [Fact]
    public void IndirectCallPreservesTheSourceOrderOfArgumentEvaluation()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("func first(): int = 1\n"
            + "func second(): int = 2\n"
            + "func combine(left: int, right: int): int = left + right\n"
            + "object Main { func main(args: []String): () { "
            + "val callback: (int, int) -> int = combine; val result = callback(first(), second()) } }")],
            TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var program = compilation.Program.Program;
        var main = Assert.Single(program.Functions, function => function.Name == "main");
        var indirect = Assert.Single(main.Body!.Statements.OfType<FirDeclare>()
            .Select(declaration => declaration.Initializer).OfType<FirIndirectCall>());
        var evaluatedTargets = indirect.Arguments.Select(argument =>
            program.Functions[Assert.IsType<FirCall>(argument.Value).Target].Name);

        Assert.Equal(new[] { "first", "second" }, evaluatedTargets);
        Assert.Equal(new[] { 0, 1 }, indirect.Arguments.Select(argument => argument.Parameter));
    }

    [Fact]
    public void AnonymousVarargFunctionValueTakesOneFixedArraySlotEvenForAnEmptyArray()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("object Main { func main(args: []String): () { "
            + "val callback: ([]int) -> int = func(vararg values) { values.length }; "
            + "val result = callback([]) } }")], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var program = compilation.Program.Program;
        var anonymous = Assert.Single(program.Functions, function => function.AnonymousFunctionSource is not null);
        var parameter = Assert.Single(anonymous.Parameters);
        var call = Assert.Single(program.Functions.Single(function => function.Name == "main").Body!
            .Statements.OfType<FirDeclare>().Select(declaration => declaration.Initializer).OfType<FirIndirectCall>());
        var argument = Assert.Single(call.Arguments);
        var array = Assert.IsType<FirArrayLiteral>(argument.Value);

        Assert.Equal(AstPassingMode.Value, parameter.Mode);
        Assert.Equal(ResolvedTypeKind.Array, parameter.Type.Kind);
        Assert.Equal("int", Assert.Single(parameter.Type.Arguments).Name);
        Assert.Equal(parameter.Type, call.Function.Type.Arguments[0]);
        Assert.Empty(array.Elements);
        Assert.Equal(parameter.Type, array.Type);
    }

    [Fact]
    public void IndirectCallReportsAnIncompatibleValueArgumentAtTheArgument()
    {
        var analysis = Analyze("func identity(value: int): int = value\n"
            + "func use() { val callback: (int) -> int = identity; val result = callback(\"wrong\") }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
    }
}
