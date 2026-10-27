// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericHigherOrderFunctionTests
{
    private static FirCompilation Compile(string source) => FirCompilation.Create(
        [AstTestSources.Parse(source)], TestContext.Current.CancellationToken);

    private static VerifiedFirProgram Valid(string source)
    {
        var compilation = Compile(source);
        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.Empty(compilation.Diagnostics);
        return compilation.Program;
    }

    [Fact]
    public void GenericCallbackMayMapOneClosedInputTypeToAnotherResultType()
    {
        var verified = Valid("func <T, U> invoke(value: T, callback: (T) -> U): U = callback(value)\n"
            + "func measure(value: String): int = 3\n"
            + "object Main { func main(args: []String): () {\n"
            + "val callback: (String) -> int = measure; val result = invoke(\"sol\", callback)\n"
            + "} }");
        var program = verified.Program;
        var invoke = Assert.Single(program.Functions, function => function.Name == "invoke"
            && !function.SpecializationArguments.IsEmpty);

        Assert.Equal(["std.core.String", "int"], invoke.SpecializationArguments.Select(type => type.Name));
        var indirect = Assert.IsType<FirIndirectCall>(Assert.IsType<FirReturn>(
            Assert.Single(invoke.Body!.Statements)).Value);
        Assert.Equal(ResolvedTypeKind.Function, indirect.Function.Type.Kind);
        Assert.Equal("std.core.String", indirect.Function.Type.Arguments[0].Name);
        Assert.Equal("int", indirect.Type.Name);
        Assert.Equal(AstPassingMode.Value, Assert.Single(indirect.Arguments).Mode);
        Assert.Equal("std.core.String", Assert.Single(indirect.Arguments).Value.Type.Name);
    }

    [Fact]
    public void GenericCallbackSignatureSurvivesASecondGenericRelay()
    {
        var verified = Valid("func <T, U> invoke(value: T, callback: (T) -> U): U = callback(value)\n"
            + "func <A, B> relay(value: A, callback: (A) -> B): B = invoke(value, callback)\n"
            + "func measure(value: String): int = 3\n"
            + "object Main { func main(args: []String): () {\n"
            + "val callback: (String) -> int = measure; val result = relay(\"sol\", callback)\n"
            + "} }");
        var functions = verified.Program.Functions;
        var relay = Assert.Single(functions, function => function.Name == "relay"
            && !function.SpecializationArguments.IsEmpty);
        var invoke = Assert.Single(functions, function => function.Name == "invoke"
            && !function.SpecializationArguments.IsEmpty);

        Assert.Equal(["std.core.String", "int"], relay.SpecializationArguments.Select(type => type.Name));
        Assert.Equal(relay.SpecializationArguments, invoke.SpecializationArguments);
        var forwarded = Assert.IsType<FirCall>(Assert.IsType<FirReturn>(Assert.Single(relay.Body!.Statements)).Value);
        Assert.Equal(invoke.Id, forwarded.Target);
        Assert.Equal(invoke.SpecializationArguments, forwarded.TypeArguments);
        Assert.Equal(ResolvedTypeKind.Function, forwarded.Arguments[1].Value.Type.Kind);
        var indirect = Assert.IsType<FirIndirectCall>(Assert.IsType<FirReturn>(Assert.Single(invoke.Body!.Statements)).Value);
        Assert.Equal("int", indirect.Type.Name);
    }

    [Fact]
    public void FunctionTypeCanBeAClosedGenericArgumentAndPassThroughResult()
    {
        var verified = Valid("func <F> preserve(callback: F): F = callback\n"
            + "func identity(value: String): String = value\n"
            + "object Main { func main(args: []String): () {\n"
            + "val callback: (String) -> String = identity; val preserved = preserve(callback); val result = preserved(\"sol\")\n"
            + "} }");
        var functions = verified.Program.Functions;
        var preserve = Assert.Single(functions, function => function.Name == "preserve"
            && !function.SpecializationArguments.IsEmpty);
        var closedArgument = Assert.Single(preserve.SpecializationArguments);
        Assert.Equal(ResolvedTypeKind.Function, closedArgument.Kind);
        Assert.Equal(closedArgument, Assert.Single(preserve.Parameters).Type);
        Assert.Equal(closedArgument, preserve.ReturnType);
        Assert.IsType<FirRead>(Assert.IsType<FirReturn>(Assert.Single(preserve.Body!.Statements)).Value);

        var main = Assert.Single(functions, function => function.Name == "main");
        var indirect = Assert.Single(main.Body!.Statements.OfType<FirDeclare>()
            .Select(declaration => declaration.Initializer).OfType<FirIndirectCall>());
        Assert.Equal(closedArgument, indirect.Function.Type);
        Assert.Equal("std.core.String", indirect.Arguments.Single().Value.Type.Name);
    }

    [Fact]
    public void GenericNominalCheckedEffectRemainsOutsideVerifiedNativeTypeSurface()
    {
        var compilation = Compile("class Failure: Exception {}\n"
            + "func <T, E: Throwable> invoke(callback: () -> T throws E): T throws E = callback()\n"
            + "func read(): String throws Failure { throw Failure() }\n"
            + "object Main { func main(args: []String): () throws Failure { val result = invoke(read) } }");

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2401");
    }

    [Fact]
    public void IncompatibleCallbackParameterShapeDoesNotProduceVerifiedFir()
    {
        var compilation = Compile("func invoke(callback: (int) -> int): int = callback(1)\n"
            + "func text(value: String): int = 1\n"
            + "object Main { func main(args: []String): () { val callback: (int) -> int = text } }");

        Assert.Null(compilation.Program);
        Assert.NotEmpty(compilation.Diagnostics);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2401");
    }
}
