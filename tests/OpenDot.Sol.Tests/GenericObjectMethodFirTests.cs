// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericObjectMethodFirTests
{
    private static FirCompilation Compile(string declarations, string body) => FirCompilation.Create([
        AstTestSources.Parse("import std.stdio\n" + declarations
            + "\nobject Main { func main(args: []String) throws IOException { " + body + " } }")],
        TestContext.Current.CancellationToken);

    [Fact]
    public void DirectSingletonMethodAndSameOwnerRelayProduceClosedInstances()
    {
        var compilation = Compile(
            "object Utility {\n"
                + "    func <T> identity(value: T): T = value\n"
                + "    func <T> relay(value: T): T = identity<T>(value)\n"
                + "}",
            "println(test.Utility.relay<int>(42))");

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var functions = compilation.Program.Program.Functions;
        var relay = Assert.Single(functions, function => function.Name == "relay");
        Assert.Equal("int", Assert.Single(relay.SpecializationArguments).Name);
        var nestedCall = Assert.Single(Calls(relay.Body!), call => functions[call.Target].Name == "identity");
        var identity = functions[nestedCall.Target];
        Assert.Equal("int", Assert.Single(identity.SpecializationArguments).Name);
        Assert.Equal("int", nestedCall.TypeArguments.Single().Name);
    }

    [Fact]
    public void SameMethodNamesInDifferentObjectsRemainDistinctSpecializations()
    {
        var compilation = Compile(
            "object Left { func <T> identity(value: T): T = value }\n"
                + "object Right { func <T> identity(value: T): T = value }",
            "val left = Left.identity<int>(1); val right = Right.identity<int>(2); println(left + right)");

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var functions = compilation.Program.Program.Functions;
        var instances = functions.Where(function => function.Name == "identity").ToArray();
        Assert.Equal(2, instances.Length);
        Assert.NotSame(instances[0].Origin.Element, instances[1].Origin.Element);
        var calls = Calls(functions.Single(function => function.Name == "main").Body!)
            .Where(call => functions[call.Target].Name == "identity").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal(2, calls.Select(call => call.Target).Distinct().Count());
    }

    [Fact]
    public void GenericObjectMethodDefaultsUseSourceOrderAndEarlierCapturedValues()
    {
        var compilation = Compile(
            "object Utility { func <T> cascade(first: T, second: T = first, third: T = second): T = third }\n"
                + "func mark(value: int): int throws IOException { println(value); value }",
            "println(Utility.cascade<int>(third = mark(30), first = mark(10)))");

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var functions = compilation.Program.Program.Functions;
        var main = Assert.Single(functions, function => function.Name == "main");
        var call = Assert.Single(Calls(main.Body!), candidate => functions[candidate.Target].Name == "cascade");
        Assert.Equal(new[] { 2, 0, 1 }, call.Arguments.Select(argument => argument.Parameter));
        var defaultValue = Assert.IsType<FirCallValueReference>(call.Arguments[2].Value);
        Assert.Equal(1, defaultValue.EvaluationIndex);
        Assert.Equal("int", defaultValue.Type.Name);
    }

    [Fact]
    public void ExpectedResultCanCloseAGenericMethodWhoseArgumentsAreDefaulted()
    {
        var compilation = Compile(
            "object Utility { func <T> withDefault(value: T = 1): T = value }",
            "val result: int = Utility.withDefault(); println(result)");

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var functions = compilation.Program.Program.Functions;
        var main = Assert.Single(functions, function => function.Name == "main");
        var call = Assert.Single(Calls(main.Body!), candidate => functions[candidate.Target].Name == "withDefault");
        Assert.Equal("int", Assert.Single(call.TypeArguments).Name);
        Assert.Equal("int", functions[call.Target].ReturnType.Name);
        Assert.IsType<FirConstant>(Assert.Single(call.Arguments).Value);
    }

    [Fact]
    public void GenericObjectVarargsPackScalarsAndRepresentAnEmptyPack()
    {
        var compilation = Compile(
            "object Utility { func <T> count(vararg values: T): int = values.length }",
            "val three = Utility.count<int>(1, 2, 3); val none = Utility.count<int>(); println(three); println(none)");

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var functions = compilation.Program.Program.Functions;
        var main = Assert.Single(functions, function => function.Name == "main");
        var calls = Calls(main.Body!).Where(call => functions[call.Target].Name == "count").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal(3, Assert.IsType<FirArrayLiteral>(Assert.Single(calls[0].Arguments).Value).Elements.Length);
        Assert.Empty(Assert.IsType<FirArrayLiteral>(Assert.Single(calls[1].Arguments).Value).Elements);
    }

    [Fact]
    public void ArrayValueCanBeOneGenericObjectVarargElementWithoutSpread()
    {
        var compilation = Compile(
            "object Utility { func <T> first(vararg values: T): T = values[0] }",
            "println(Utility.first(args)[0])");

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var functions = compilation.Program.Program.Functions;
        var main = Assert.Single(functions, function => function.Name == "main");
        var call = Assert.Single(Calls(main.Body!), candidate => functions[candidate.Target].Name == "first");
        var method = functions[call.Target];
        var typeArgument = Assert.Single(method.SpecializationArguments);
        Assert.Equal(ResolvedTypeKind.Array, typeArgument.Kind);
        Assert.Equal("std.core.String", Assert.Single(typeArgument.Arguments).Name);
        var parameter = Assert.Single(method.Parameters);
        Assert.Equal(ResolvedTypeKind.Array, parameter.Type.Kind);
        Assert.Equal(ResolvedTypeKind.Array, Assert.Single(parameter.Type.Arguments).Kind);
        var packed = Assert.IsType<FirArrayLiteral>(Assert.Single(call.Arguments).Value);
        Assert.Single(packed.Elements);
    }

    [Fact]
    public void GenericObjectVarargsPreserveNamedArrayAndSpreadSourceMappings()
    {
        var compilation = Compile(
            "object Utility { func <T> count(vararg values: T): int = values.length }",
            "val named = Utility.count<String>(values = args); "
                + "val spread = Utility.count<String>(*args); println(named); println(spread)");

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var functions = compilation.Program.Program.Functions;
        var main = Assert.Single(functions, function => function.Name == "main");
        var calls = Calls(main.Body!).Where(call => functions[call.Target].Name == "count").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call => Assert.Equal("std.core.String", Assert.Single(call.TypeArguments).Name));
        Assert.All(calls, call => Assert.Equal(ResolvedTypeKind.Array, Assert.Single(call.Arguments).Value.Type.Kind));
        Assert.All(calls, call => Assert.IsType<FirRead>(Assert.Single(call.Arguments).Value));
    }

    [Fact]
    public void MutuallyRecursiveMethodsShareOneClosedInstancePerOwnerAndTypeArgument()
    {
        var compilation = Compile(
            "object Utility {\n"
                + "    func <T> first(value: T): T = second<T>(value)\n"
                + "    func <T> second(value: T): T = first<T>(value)\n"
                + "}",
            "println(Utility.first<int>(42))");

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var functions = compilation.Program.Program.Functions;
        var first = Assert.Single(functions, function => function.Name == "first");
        var second = Assert.Single(functions, function => function.Name == "second");
        Assert.Equal("int", Assert.Single(first.SpecializationArguments).Name);
        Assert.Equal("int", Assert.Single(second.SpecializationArguments).Name);
        var firstTarget = Assert.Single(Calls(first.Body!), call => functions[call.Target].Name == "second");
        var secondTarget = Assert.Single(Calls(second.Body!), call => functions[call.Target].Name == "first");
        Assert.Equal(second.Id, firstTarget.Target);
        Assert.Equal(first.Id, secondTarget.Target);
    }

    [Fact]
    public void GenericObjectMethodConstraintsAreCheckedForTheClosedInstance()
    {
        var compilation = Compile(
            "object Utility { func <T> requireObject(value: T): T where T: Object = value }",
            "println(Utility.requireObject<String>(\"bounded\"))");

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var instance = Assert.Single(compilation.Program.Program.Functions,
            function => function.Name == "requireObject");
        Assert.Equal("std.core.String", Assert.Single(instance.SpecializationArguments).Name);
        Assert.Equal("std.core.String", Assert.Single(instance.Parameters).Type.Name);
    }

    [Fact]
    public void PartialObjectMethodIdentityIsSharedAcrossSourceFiles()
    {
        var first = AstTestSources.Parse("partial object Utility { func <T> identity(value: T): T = value }", "app", "first.sol");
        var second = AstTestSources.Parse("partial object Utility { func <T> relay(value: T): T = identity<T>(value) }", "app", "second.sol");
        var entry = AstTestSources.Parse("import std.stdio\nobject Main { func main(args: []String) throws IOException { println(Utility.relay<String>(\"partial\")) } }", "app", "main.sol");
        var compilation = FirCompilation.Create([first, second, entry], TestContext.Current.CancellationToken);

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var functions = compilation.Program.Program.Functions;
        var relay = Assert.Single(functions, function => function.Name == "relay");
        var nestedCall = Assert.Single(Calls(relay.Body!), call => functions[call.Target].Name == "identity");
        Assert.Equal("std.core.String", Assert.Single(functions[nestedCall.Target].SpecializationArguments).Name);
    }

    [Fact]
    public void StatefulObjectMethodsStayOutsideNativeFir()
    {
        var compilation = Compile(
            "object Stateful { val count = 0; func <T> identity(value: T): T = value }",
            "println(Stateful.identity<int>(42))");

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400"
            && diagnostic.Message.Contains("stateless-object", System.StringComparison.Ordinal));
    }

    [Fact]
    public void LocalObjectAliasIsNotTreatedAsAReceiverFreeSingletonQualifier()
    {
        var compilation = Compile(
            "object Utility { func <T> identity(value: T): T = value }",
            "val alias = Utility; println(alias.identity<int>(42))");

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400"
            && diagnostic.Message.Contains("direct, verified singleton reference", System.StringComparison.Ordinal));
    }

    private static IEnumerable<FirCall> Calls(FirNode node)
    {
        if (node is FirCall call) { yield return call; }
        foreach (var child in Children(node))
        {
            foreach (var nested in Calls(child)) { yield return nested; }
        }
    }

    private static IEnumerable<FirNode> Children(FirNode node) => node switch
    {
        FirBlock block => block.Statements,
        FirCall call => call.Arguments.Select(argument => (FirNode)argument.Value),
        FirIntrinsic intrinsic => intrinsic.Arguments,
        FirUnary unary => [unary.Value],
        FirBinary binary => [binary.Left, binary.Right],
        FirArrayLength length => [length.Array],
        FirArrayIndex index => [index.Array, index.Index],
        FirArrayLiteral array => array.Elements,
        FirDeclare declaration when declaration.Initializer is { } initializer => [initializer],
        FirStore store => [store.Value],
        FirEvaluate evaluate => [evaluate.Value],
        FirReturn ret when ret.Value is { } value => [value],
        FirIf conditional => conditional.Else is { } otherwise
            ? [conditional.Condition, conditional.Then, otherwise] : [conditional.Condition, conditional.Then],
        FirLoop loop => LoopChildren(loop),
        FirArrayForEach loop => [loop.Array, loop.Body],
        _ => [],
    };

    private static IEnumerable<FirNode> LoopChildren(FirLoop loop)
    {
        if (loop.Initializer is { } initializer) { yield return initializer; }
        if (loop.Condition is { } condition) { yield return condition; }
        yield return loop.Body;
        if (loop.Update is { } update) { yield return update; }
    }
}
