// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericFirInstanceRebindingTests
{
    private static FirCompilation Compile(string body, string declarations) => FirCompilation.Create([
        AstTestSources.Parse("import std.stdio\n" + declarations
            + "\nobject Main { func main(args: []String) throws IOException { " + body + " } }")],
        TestContext.Current.CancellationToken);

    [Fact]
    public void OperatorsAndOverloadSelectionAreReboundPerClosedType()
    {
        var compilation = Compile(
            "println(add<int>(20, 22)); println(both<bool>(true, false)); "
                + "println(dispatch<int>(1)); println(dispatch<String>(\"x\"))",
            "func <T> add(left, right: T): T = left + right\n"
                + "func <T> both(left, right: T): bool = left && right\n"
                + "func choose(value: int): String = \"integer\"\n"
                + "func choose(value: String): String = \"string\"\n"
                + "func <T> dispatch(value: T): String = choose(value)");

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var functions = compilation.Program.Program.Functions;
        var add = Assert.Single(functions, function => function.Name == "add");
        var addReturn = Assert.IsType<FirReturn>(Assert.Single(add.Body!.Statements));
        Assert.Equal("int", Assert.IsType<FirBinary>(addReturn.Value).Type.Name);
        var both = Assert.Single(functions, function => function.Name == "both");
        var bothReturn = Assert.IsType<FirReturn>(Assert.Single(both.Body!.Statements));
        Assert.Equal("bool", Assert.IsType<FirBinary>(bothReturn.Value).Type.Name);

        var dispatches = functions.Where(function => function.Name == "dispatch").ToArray();
        Assert.Equal(2, dispatches.Length);
        Assert.Contains(dispatches, function => Assert.Single(function.SpecializationArguments).Name == "int");
        Assert.Contains(dispatches, function => Assert.Single(function.SpecializationArguments).Name == "std.core.String");
        foreach (var dispatch in dispatches)
        {
            var result = Assert.IsType<FirReturn>(Assert.Single(dispatch.Body!.Statements));
            var call = Assert.IsType<FirCall>(result.Value);
            var target = functions[call.Target];
            Assert.Equal(Assert.Single(dispatch.SpecializationArguments), Assert.Single(target.Parameters).Type);
        }
    }

    [Fact]
    public void ForeachFlowAndCheckedEffectsAreRecomputedForEachArrayInstance()
    {
        var compilation = Compile(
            "println(firstOr<int>([1, 2], 0)); println(firstOr<String>([\"x\"], \"fallback\"))",
            "func tick(): () throws IOException {}\n"
                + "func <T> firstOr(values: []T, fallback: T): T throws IOException {\n"
                + "    for val item in values { tick(); return item }\n"
                + "    fallback\n"
                + "}");

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var instances = compilation.Program.Program.Functions
            .Where(function => function.Name == "firstOr").ToArray();
        Assert.Equal(2, instances.Length);
        foreach (var instance in instances)
        {
            var argument = Assert.Single(instance.SpecializationArguments);
            Assert.Equal(argument, instance.Parameters[0].Type.Arguments[0]);
            Assert.Contains(instance.Effects, effect => effect.Name == "std.stdio.IOException");
            var iteration = Assert.IsType<FirArrayForEach>(instance.Body!.Statements[0]);
            Assert.Equal(argument, instance.Slots[iteration.ItemSlot].Type);
            Assert.Empty(compilation.Program.FlowGraphs.Single(graph => graph.FunctionId == instance.Id).Diagnostics);
        }
    }

    [Fact]
    public void ClosedInstanceStillRejectsAnOperatorUnsupportedByItsConcreteType()
    {
        var compilation = Compile("println(add<bool>(true, false))",
            "func <T> add(left, right: T): T = left + right");

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2207");
    }

    [Fact]
    public void ClosedGenericRebindRanksUserOperatorBeforeFirRejectsTheSelectedBody()
    {
        var compilation = Compile("add<Number>(Number(1), Number(2))",
            "class Number(val value: int) { companion object {\n"
                + "    operator func +(left: Number, right: Number): Number = left\n"
                + "    operator func +(left: Number, right: Object): String = \"fallback\"\n"
                + "} }\n"
                + "func <T> add(left, right: T): T = left + right");

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2207");
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
    }
}
