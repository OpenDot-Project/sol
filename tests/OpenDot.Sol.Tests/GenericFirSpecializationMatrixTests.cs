// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericFirSpecializationMatrixTests
{
    private static FirCompilation Compile(string source) => FirCompilation.Create(
        [AstTestSources.Parse(source)], TestContext.Current.CancellationToken);

    private static VerifiedFirProgram Valid(string source)
    {
        var compilation = Compile(source);
        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        return compilation.Program;
    }

    [Fact]
    public void RepeatedCallsReuseOneClosedGenericInstance()
    {
        var program = Valid("""
            func <T> identity(value: T): T = value
            object Main { func main(args: []String): () {
                val first = identity<int>(7)
                val second = identity<int>(8)
            } }
            """);
        var instance = Assert.Single(program.Program.Functions,
            function => function.Name == "identity" && !function.SpecializationArguments.IsEmpty);
        Assert.Equal("int", Assert.Single(instance.SpecializationArguments).Name);

        var calls = Calls(Assert.Single(program.Program.Functions, function => function.Name == "main").Body!);
        var identityCalls = calls.Where(call => program.Program.Functions[call.Target].Name == "identity").ToArray();
        Assert.Equal(2, identityCalls.Length);
        Assert.All(identityCalls, call => Assert.Equal(instance.Id, call.Target));
        Assert.All(identityCalls, call => Assert.Equal(instance.SpecializationArguments, call.TypeArguments));
    }

    [Fact]
    public void OrderedTypeArgumentsCreateDistinctSpecializations()
    {
        var program = Valid("""
            func <T, U> first(value: T, ignored: U): T = value
            object Main { func main(args: []String): () {
                val left = first<int, String>(1, "text")
                val right = first<String, int>("text", 1)
            } }
            """);
        var instances = program.Program.Functions.Where(function =>
            function.Name == "first" && !function.SpecializationArguments.IsEmpty).ToArray();
        Assert.Equal(2, instances.Length);
        Assert.Contains(instances, instance => instance.SpecializationArguments.Select(type => type.Name)
            .SequenceEqual(["int", "std.core.String"]));
        Assert.Contains(instances, instance => instance.SpecializationArguments.Select(type => type.Name)
            .SequenceEqual(["std.core.String", "int"]));
        Assert.NotEqual(instances[0].Id, instances[1].Id);

        var calls = Calls(Assert.Single(program.Program.Functions, function => function.Name == "main").Body!)
            .Where(call => program.Program.Functions[call.Target].Name == "first").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call => Assert.Equal(program.Program.Functions[call.Target].SpecializationArguments,
            call.TypeArguments));
    }

    [Fact]
    public void SourceDeclarationIdentitySeparatesSameNameAndSameArguments()
    {
        var program = Valid("""
            func <T> select(value: T): T = value
            func <T> select(value: T, fallback: T): T = value
            object Main { func main(args: []String): () {
                val one = select<int>(1)
                val two = select<int>(2, 3)
            } }
            """);
        var instances = program.Program.Functions.Where(function =>
            function.Name == "select" && !function.SpecializationArguments.IsEmpty).ToArray();

        Assert.Equal(2, instances.Length);
        Assert.Equal(instances[0].SpecializationArguments, instances[1].SpecializationArguments);
        Assert.NotSame(instances[0].Origin.Element, instances[1].Origin.Element);
        Assert.NotEqual(instances[0].Id, instances[1].Id);

        var calls = Calls(Assert.Single(program.Program.Functions, function => function.Name == "main").Body!)
            .Where(call => program.Program.Functions[call.Target].Name == "select").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.NotEqual(calls[0].Target, calls[1].Target);
    }

    [Fact]
    public void CallerSubstitutionClosesNestedGenericRelay()
    {
        var program = Valid("""
            func <T> identity(value: T): T = value
            func <U> relay(value: U): U = identity<U>(value)
            object Main { func main(args: []String): () {
                val result = relay<int>(7)
            } }
            """);
        var relay = Assert.Single(program.Program.Functions, function =>
            function.Name == "relay" && !function.SpecializationArguments.IsEmpty);
        var identity = Assert.Single(program.Program.Functions, function =>
            function.Name == "identity" && !function.SpecializationArguments.IsEmpty);
        Assert.Equal("int", Assert.Single(relay.SpecializationArguments).Name);
        Assert.Equal("int", Assert.Single(identity.SpecializationArguments).Name);

        var nested = Assert.Single(Calls(relay.Body!), call =>
            program.Program.Functions[call.Target].Name == "identity");
        Assert.Equal(identity.Id, nested.Target);
        Assert.Equal(identity.SpecializationArguments, nested.TypeArguments);
    }

    [Fact]
    public void OneDimensionalArrayCanBeAClosedGenericTypeArgument()
    {
        var program = Valid("""
            func <T> identity(value: T): T = value
            object Main { func main(args: []String): () {
                val values = identity<[]int>([1, 2])
            } }
            """);
        var instance = Assert.Single(program.Program.Functions, function =>
            function.Name == "identity" && !function.SpecializationArguments.IsEmpty);
        var argument = Assert.Single(instance.SpecializationArguments);

        Assert.Equal(ResolvedTypeKind.Array, argument.Kind);
        Assert.Equal("int", Assert.Single(argument.Arguments).Name);
        Assert.Equal(argument, instance.ReturnType);
        Assert.Equal(argument, Assert.Single(instance.Parameters).Type);
    }

    [Fact]
    public void NestedArrayTypeArgumentKeepsItsClosedElementLayers()
    {
        var compilation = Compile("""
            func <T> identity(value: T): T = value
            object Main { func main(args: []String): () {
                val nested: [][]int = [[1]]
                val result = identity<[][]int>(nested)
            } }
            """);

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var instance = Assert.Single(compilation.Program.Program.Functions,
            function => function.Name == "identity" && !function.SpecializationArguments.IsEmpty);
        var nested = Assert.Single(instance.SpecializationArguments);
        Assert.Equal(ResolvedTypeKind.Array, nested.Kind);
        var inner = Assert.Single(nested.Arguments);
        Assert.Equal(ResolvedTypeKind.Array, inner.Kind);
        Assert.Equal("int", Assert.Single(inner.Arguments).Name);
        Assert.Equal(nested, instance.ReturnType);
    }

    [Fact]
    public void UnresolvedTypeArgumentDoesNotReachFir()
    {
        var compilation = Compile("""
            func <T> identity(value: T): T = value
            object Main { func main(args: []String): () {
                val result = identity<Missing>(1)
            } }
            """);

        Assert.Null(compilation.Program);
        Assert.NotEmpty(compilation.Diagnostics);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2402");
    }

    [Fact]
    public void VerifierRejectsAnOpenTypeArgumentOnAnInstance()
    {
        var program = Valid("""
            func <T> identity(value: T): T = value
            object Main { func main(args: []String): () {
                val result = identity<int>(7)
            } }
            """);
        var instance = Assert.Single(program.Program.Functions,
            function => function.Name == "identity" && !function.SpecializationArguments.IsEmpty);
        var source = program.Program.Analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<OpenDot.Sol.Ast.AstFunction>().Single(function => function.Name == "identity");
        var openParameter = Assert.Single(program.Program.Analysis.Sources.Types.GetTypeParameters(source));
        var altered = instance with { SpecializationArguments = [openParameter] };

        var result = FirVerifier.Verify(Replace(program.Program, altered), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402");
    }

    [Fact]
    public void GenericInspectionJsonIsStableAcrossFreshCompilations()
    {
        const string source = """
            func <T> identity(value: T): T = value
            func <U> relay(value: U): U = identity<U>(value)
            object Main { func main(args: []String): () {
                val result = relay<int>(7)
            } }
            """;
        var first = FirJson.Serialize(Valid(source), TestContext.Current.CancellationToken);
        var second = FirJson.Serialize(Valid(source), TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.True(first.Contains("\"schema\": 5", StringComparison.Ordinal));
        Assert.False(first.Contains("C:\\Users", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsChangedGenericCallTypeArguments()
    {
        var program = Valid("""
            func <T> identity(value: T): T = value
            object Main { func main(args: []String): () {
                val result = identity<int>(7)
            } }
            """);
        var main = Assert.Single(program.Program.Functions, function => function.Name == "main");
        var declaration = Assert.Single(main.Body!.Statements.OfType<FirDeclare>());
        var call = Assert.IsType<FirCall>(declaration.Initializer);
        var stringType = Assert.Single(program.Program.Analysis.Types.Types,
            type => type.Declaration is { ModuleName: "std.core", Name: "String" });
        var alteredCall = call with { TypeArguments = [stringType] };
        var alteredMain = main with
        {
            Body = main.Body with
            {
                Statements = main.Body.Statements.SetItem(0, declaration with { Initializer = alteredCall }),
            },
        };

        var result = FirVerifier.Verify(Replace(program.Program, alteredMain), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402");
    }

    [Fact]
    public void VerifierRejectsCallRetargetedToAnotherGenericDeclaration()
    {
        var program = Valid("""
            func <T> identity(value: T): T = value
            func <T> copy(value: T): T = value
            object Main { func main(args: []String): () {
                val first = identity<int>(1)
                val second = copy<int>(2)
            } }
            """);
        var main = Assert.Single(program.Program.Functions, function => function.Name == "main");
        var declarations = main.Body!.Statements.OfType<FirDeclare>().ToArray();
        var identityCall = Assert.IsType<FirCall>(declarations[0].Initializer);
        var copyCall = Assert.IsType<FirCall>(declarations[1].Initializer);
        var alteredCall = identityCall with { Target = copyCall.Target };
        var alteredMain = main with
        {
            Body = main.Body with
            {
                Statements = main.Body.Statements.SetItem(0,
                    declarations[0] with { Initializer = alteredCall }),
            },
        };

        var result = FirVerifier.Verify(Replace(program.Program, alteredMain), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("target differs from overload resolution", StringComparison.Ordinal));
    }

    private static IEnumerable<FirCall> Calls(FirNode node)
    {
        switch (node)
        {
            case FirCall call:
                yield return call;
                foreach (var argument in call.Arguments)
                { foreach (var nested in Calls(argument.Value)) { yield return nested; } }
                break;
            case FirBlock block:
                foreach (var statement in block.Statements)
                { foreach (var nested in Calls(statement)) { yield return nested; } }
                break;
            case FirDeclare { Initializer: { } declareInitializer }:
                foreach (var nested in Calls(declareInitializer)) { yield return nested; }
                break;
            case FirStore store:
                foreach (var nested in Calls(store.Value)) { yield return nested; }
                break;
            case FirEvaluate evaluate:
                foreach (var nested in Calls(evaluate.Value)) { yield return nested; }
                break;
            case FirReturn { Value: { } value }:
                foreach (var nested in Calls(value)) { yield return nested; }
                break;
            case FirIf conditional:
                foreach (var nested in Calls(conditional.Condition)) { yield return nested; }
                foreach (var nested in Calls(conditional.Then)) { yield return nested; }
                if (conditional.Else is { } otherwise)
                { foreach (var nested in Calls(otherwise)) { yield return nested; } }
                break;
            case FirLoop loop:
                if (loop.Initializer is { } initializer)
                { foreach (var nested in Calls(initializer)) { yield return nested; } }
                if (loop.Condition is { } condition)
                { foreach (var nested in Calls(condition)) { yield return nested; } }
                foreach (var nested in Calls(loop.Body)) { yield return nested; }
                if (loop.Update is { } update)
                { foreach (var nested in Calls(update)) { yield return nested; } }
                break;
            case FirArrayForEach loop:
                foreach (var nested in Calls(loop.Array)) { yield return nested; }
                foreach (var nested in Calls(loop.Body)) { yield return nested; }
                break;
            case FirUnary unary:
                foreach (var nested in Calls(unary.Value)) { yield return nested; }
                break;
            case FirBinary binary:
                foreach (var nested in Calls(binary.Left)) { yield return nested; }
                foreach (var nested in Calls(binary.Right)) { yield return nested; }
                break;
            case FirArrayLength length:
                foreach (var nested in Calls(length.Array)) { yield return nested; }
                break;
            case FirArrayIndex index:
                foreach (var nested in Calls(index.Array)) { yield return nested; }
                foreach (var nested in Calls(index.Index)) { yield return nested; }
                break;
            case FirArrayLiteral array:
                foreach (var item in array.Elements)
                { foreach (var nested in Calls(item)) { yield return nested; } }
                break;
            case FirIntrinsic intrinsic:
                foreach (var argument in intrinsic.Arguments)
                { foreach (var nested in Calls(argument)) { yield return nested; } }
                break;
        }
    }

    private static FirProgram Replace(FirProgram program, FirFunction replacement)
        => program with { Functions = program.Functions.SetItem(replacement.Id, replacement) };
}
