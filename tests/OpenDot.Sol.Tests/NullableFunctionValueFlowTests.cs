// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class NullableFunctionValueFlowTests
{
    private static FirCompilation Compile(string declarations) => FirCompilation.Create(
        [AstTestSources.Parse(declarations)], TestContext.Current.CancellationToken, library: true);

    private static void AssertVerified(string declarations)
    {
        var compilation = Compile(declarations);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Span + ": " + diagnostic.Message)));
        Assert.IsType<VerifiedFirProgram>(compilation.Program);
    }

    private static void AssertRejected(string declarations, params string[] expectedCodes)
    {
        var compilation = Compile(declarations);
        Assert.Null(compilation.Program);
        Assert.NotEmpty(compilation.Diagnostics);
        Assert.Contains(compilation.Diagnostics, diagnostic => expectedCodes.Contains(diagnostic.Code));
    }

    [Theory]
    [InlineData("func invoke(callback: (() -> int)?): int { if callback != null { return callback() }; 0 }")]
    [InlineData("func invoke(callback: (() -> int)?): int { if null != callback { return callback() }; 0 }")]
    [InlineData("func invoke(callback: (() -> int)?): int { if callback == null { return 0 }; callback() }")]
    [InlineData("func invoke(callback: (() -> int)?): int { if null == callback { return 0 }; callback() }")]
    [InlineData("func invoke(callback: (() -> int)?): int { if callback != null && callback() == 42 { return callback() }; 0 }")]
    [InlineData("func invoke(callback: (() -> int)?): int { if callback == null || callback() == 42 { return 42 }; 0 }")]
    public void BranchProofMakesTheExactLocalCallable(string declaration) => AssertVerified(declaration);

    [Fact]
    public void NullableCallableResultAndExplicitNullAreVerifiable()
    {
        AssertVerified("func nullableResult(): (() -> int)? = func() { 42 }\n"
            + "func returnNull(): (() -> int)? = null");
    }

    [Fact]
    public void NullAndFunctionValuesShareExactPointerSlotsAndArrayElements()
    {
        AssertVerified("func target(): int = 42\n"
            + "func run(): int {\n"
            + "var callback: (() -> int)? = null\n"
            + "if callback == null { callback = target }\n"
            + "val callbacks: []((() -> int)?) = [null, callback]\n"
            + "val selected = callbacks[1]\n"
            + "if selected != null { return selected() }\n"
            + "0\n}");
    }

    [Fact]
    public void NestedArraysTraceContainersAndKeepNullableFunctionsAtTheLeaves()
    {
        AssertVerified("func nested(): int {\n"
            + "val callbacks: [][]((() -> int)?) = [[null], [func() { 42 }]]\n"
            + "val selected = callbacks[1][0]\n"
            + "if selected != null { return selected() }\n"
            + "0\n}");
    }

    [Fact]
    public void DefaultNullUsesTheNullableFormalTypeAndVarargKeepsOneArraySlot()
    {
        AssertVerified("func invoke(callback: (() -> int)? = null): int {\n"
            + "if callback != null { return callback() }\n"
            + "0\n}\n"
            + "func first(vararg callbacks: (() -> int)?): int {\n"
            + "val selected = callbacks[0]\n"
            + "if selected != null { return selected() }\n"
            + "0\n}\n"
            + "func caller(): int { first(null, func() { invoke() }) }");
    }

    [Fact]
    public void RefAndOutKeepTheirNullableFunctionStorageContracts()
    {
        AssertVerified("func set(out callback: (() -> int)?): () { callback = func() { 42 } }\n"
            + "func clear(ref callback: (() -> int)?): () { callback = null }\n"
            + "func caller(): int {\n"
            + "var callback: (() -> int)?\n"
            + "set(out callback)\n"
            + "if callback != null { clear(ref callback) }\n"
            + "if callback == null { return 0 }\n"
            + "callback()\n}");
    }

    [Fact]
    public void CheckedEffectsStayOnThePayloadFunctionType()
    {
        AssertVerified("open class IOException: Exception {}\n"
            + "func callback(): int throws IOException = 42\n"
            + "func invoke(value: (() -> int throws IOException)?): int throws IOException {\n"
            + "if value == null { return 0 }\n"
            + "value()\n}\n"
            + "func caller(): int throws IOException = invoke(callback)");
    }

    [Theory]
    [InlineData("func invoke(callback: (() -> int)?): int = callback()")]
    [InlineData("func invoke(callback: (() -> int)?): int { var current = callback; if current != null { current = null; return current() }; 0 }")]
    [InlineData("func invoke(flag: bool, callback: (() -> int)?): int { if flag { if callback == null { return 0 } }; callback() }")]
    [InlineData("func invoke(callback: (() -> int)?): int { val alias = callback; if callback != null { return alias() }; 0 }")]
    [InlineData("func invoke(callback: (() -> int)?): int { if !(callback == null) { return callback() }; 0 }")]
    public void CallsWithoutAStableProofAreRejected(string declaration)
        => AssertRejected(declaration, "SOL2221", "SOL2243", "SOL2400", "SOL2402");

    [Fact]
    public void RefCallInvalidatesPriorNullableFunctionRefinement()
    {
        AssertRejected("func clear(ref callback: (() -> int)?): () { callback = null }\n"
            + "func invoke(callback: (() -> int)?): int {\n"
            + "if callback != null { clear(ref callback); return callback() }\n"
            + "0\n}", "SOL2221", "SOL2243", "SOL2400");
    }

    [Fact]
    public void NullableManagedReferenceDoesNotEnterTheFunctionPointerSubset()
    {
        AssertRejected("class Payload {}\n"
            + "func unsupported(value: Payload?): Payload? = value", "SOL2402");
    }

    [Fact]
    public void NullablePointerAssignmentRequiresTheExactFunctionAbi()
    {
        var compilation = Compile("func sample(callback: (() -> int)?): int {\n"
            + "if callback == null { return 0 }\n"
            + "callback()\n}");
        var types = Assert.IsType<VerifiedFirProgram>(compilation.Program).Program.Analysis.Types;
        var integer = types.GetPrimitive("int");
        var other = types.GetPrimitive("long");
        var function = types.GetFunction([], integer, []);
        var otherFunction = types.GetFunction([], other, []);
        var nullableFunction = types.GetNullable(function);
        var nullOnly = types.GetNullable(types.GetBottom());

        Assert.True(FirVerifier.IsAssignable(function, nullableFunction));
        Assert.True(FirVerifier.IsAssignable(nullOnly, nullableFunction));
        Assert.False(FirVerifier.IsAssignable(otherFunction, nullableFunction));
        Assert.False(FirVerifier.IsAssignable(nullableFunction, function));
        Assert.True(FirVerifier.Supported(nullableFunction));
        Assert.True(FirVerifier.Supported(types.GetFunction([nullableFunction], nullableFunction, [AstPassingMode.Value])));
        Assert.True(FirVerifier.Supported(nullOnly));
        Assert.False(FirVerifier.Supported(types.GetArray(nullOnly)));
        Assert.False(FirVerifier.Supported(types.GetNullable(types.Types.Single(FirVerifier.IsString))));
    }

    [Fact]
    public void RefinedReadHasThePayloadTypeButTheSlotRetainsNullability()
    {
        var compilation = Compile("func invoke(callback: (() -> int)?): int {\n"
            + "if callback == null { return 0 }\n"
            + "callback()\n}");
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program).Program;
        var function = Assert.Single(program.Functions, candidate => candidate.Name == "invoke");
        var slot = Assert.Single(function.Slots, candidate => candidate.Name == "callback");
        var indirect = Assert.Single(function.Body!.Statements.OfType<FirReturn>(),
            statement => statement.Value is FirIndirectCall);
        var call = Assert.IsType<FirIndirectCall>(indirect.Value);
        var read = Assert.IsType<FirRead>(call.Function);

        Assert.True(FirVerifier.IsNullableFunction(slot.Type));
        Assert.Equal(slot.Type.Arguments[0], read.Type);
        Assert.NotEqual(slot.Type, read.Type);
        Assert.Equal(slot.Id, read.Slot);
    }
}
