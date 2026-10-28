// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Globalization;
using System.Linq;
using System.Text.Json;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class NullableFunctionValueArrayAndFirBoundaryTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\n";

    [Fact]
    public void NestedNullableCallbackArraysRetainLeafAndContainerShapes()
    {
        var program = Compile("func first(): int = 41\n"
            + "object Main { func main(args: []String): () {\n"
            + "val callbacksA: []((() -> int)?) = [null, first]\n"
            + "val callbacksB: []((() -> int)?) = [func() { 42 }]\n"
            + "val nested: [][]((() -> int)?) = [callbacksA, callbacksB]\n"
            + "val snapshot = nested[1][0]\n"
            + "if snapshot != null { val answer = snapshot() }\n"
            + "} }");
        var main = Assert.Single(program.Program.Functions, function => function.Name == "main");
        var callbacksA = Slot(main, "callbacksA");
        var callbacksB = Slot(main, "callbacksB");
        var nested = Slot(main, "nested");
        var snapshot = Slot(main, "snapshot");
        var nestedLiteral = Assert.IsType<FirArrayLiteral>(Declaration(main, nested).Initializer);
        var firstLiteral = Assert.IsType<FirArrayLiteral>(Declaration(main, callbacksA).Initializer);
        var secondLiteral = Assert.IsType<FirArrayLiteral>(Declaration(main, callbacksB).Initializer);

        Assert.True(FirVerifier.IsNullableFunction(Assert.Single(callbacksA.Type.Arguments)));
        Assert.True(FirVerifier.IsNullableFunction(Assert.Single(callbacksB.Type.Arguments)));
        Assert.Equal(ResolvedTypeKind.Array, nested.Type.Kind);
        var rowType = Assert.Single(nested.Type.Arguments);
        Assert.Equal(ResolvedTypeKind.Array, rowType.Kind);
        Assert.True(FirVerifier.IsNullableFunction(Assert.Single(rowType.Arguments)));
        Assert.All(nestedLiteral.Elements, element => Assert.IsType<FirRead>(element));
        Assert.Equal(callbacksA.Type, Assert.IsType<FirRead>(nestedLiteral.Elements[0]).Type);
        Assert.Equal(callbacksB.Type, Assert.IsType<FirRead>(nestedLiteral.Elements[1]).Type);
        Assert.True(FirVerifier.IsNullableFunction(snapshot.Type));
        Assert.Contains(firstLiteral.Elements, element => element is FirConstant constant
            && FirVerifier.IsNullSentinel(constant.Type));
        Assert.Contains(secondLiteral.Elements, element => element is FirFunctionReference);
    }

    [Fact]
    public void EmptyAndNullOnlyCallbackArraysPreserveTheirNullableFunctionElementType()
    {
        var program = Compile("object Main { func main(args: []String): () {\n"
            + "val empty: []((() -> int)?) = []\n"
            + "val onlyNull: []((() -> int)?) = [null]\n"
            + "val count = empty.length\n"
            + "val value = onlyNull[0]\n"
            + "} }");
        var main = Assert.Single(program.Program.Functions, function => function.Name == "main");
        var empty = Assert.IsType<FirArrayLiteral>(Declaration(main, Slot(main, "empty")).Initializer);
        var onlyNull = Assert.IsType<FirArrayLiteral>(Declaration(main, Slot(main, "onlyNull")).Initializer);

        Assert.Empty(empty.Elements);
        Assert.True(FirVerifier.IsNullableFunction(Assert.Single(empty.Type.Arguments)));
        var nullElement = Assert.IsType<FirConstant>(Assert.Single(onlyNull.Elements));
        Assert.True(FirVerifier.IsNullSentinel(nullElement.Type));
        Assert.True(FirVerifier.IsNullableFunction(Assert.Single(onlyNull.Type.Arguments)));
    }

    [Fact]
    public void NullableArrayItemSnapshotSurvivesAWriteToTheSameArrayIndex()
    {
        var session = AstTestSources.Session("module app\nfunc invoke(callbacks: []((() -> int)?)): int {\n"
            + "val snapshot = callbacks[0]\n"
            + "callbacks[0] = null\n"
            + "if snapshot == null { return 0 }\n"
            + "snapshot()\n} ", Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var snapshot = Assert.Single(sources.Lexical.Scopes.SelectMany(scope => scope.Symbols),
            local => local.Name == "snapshot");
        var invocation = Assert.Single(session.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstCall>(), call => call.Target is AstAtom { Spelling: "snapshot" });
        var binding = Assert.IsType<ExpressionBinding>(analysis.GetExpression(invocation));

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        Assert.True(analysis.IsComplete);
        Assert.True(FirVerifier.IsNullableFunction(analysis.GetLocalType(snapshot)!));
        Assert.Equal(BodyBindingStatus.Bound, binding.Status);
        Assert.Equal("int", binding.Type!.Name);
    }

    [Fact]
    public void SourceCorrespondenceRejectsAFlowNarrowedReadRetargetedToASameAbiSibling()
    {
        var verified = Compile("func invoke(callback: (() -> int)?, sibling: (() -> int)?): int {\n"
            + "if callback == null { return 0 }\n"
            + "callback()\n}", library: true);
        var program = verified.Program;
        var function = Assert.Single(program.Functions, candidate => candidate.Name == "invoke");
        var callback = Slot(function, "callback");
        var sibling = Slot(function, "sibling");
        var returned = Assert.Single(function.Body!.Statements.OfType<FirReturn>(), statement =>
            statement.Value is FirIndirectCall);
        var call = Assert.IsType<FirIndirectCall>(returned.Value);
        var read = Assert.IsType<FirRead>(call.Function);
        var forgedCall = call with { Function = new FirRead(sibling.Id, read.Type, read.Origin) };
        var forgedFunction = ReplaceReturn(function, returned, returned with { Value = forgedCall });

        Assert.Equal(callback.Type, sibling.Type);
        Assert.Equal(callback.Type.Arguments[0], read.Type);
        Assert.NotEqual(callback.Type, read.Type);
        var result = FirVerifier.Verify(Replace(program, forgedFunction), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("different source local slot", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsNullableArrayElementWhoseFunctionAbiWasChanged()
    {
        var verified = Compile("func invoke(callbacks: []((() -> int)?)): int {\n"
            + "val selected = callbacks[0]\n"
            + "if selected == null { return 0 }\n"
            + "selected()\n}", library: true);
        var program = verified.Program;
        var function = Assert.Single(program.Functions, candidate => candidate.Name == "invoke");
        var selected = Slot(function, "selected");
        var declaration = Declaration(function, selected);
        var index = Assert.IsType<FirArrayIndex>(declaration.Initializer);
        var types = program.Analysis.Types;
        var changedFunction = types.GetFunction([], types.GetPrimitive("long"), []);
        var changedElement = types.GetNullable(changedFunction);
        var forgedIndex = index with { Type = changedElement };
        var forgedFunctionBody = ReplaceInitializer(function, declaration,
            declaration with { Initializer = forgedIndex });

        var result = FirVerifier.Verify(Replace(program, forgedFunctionBody), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402");
    }

    [Fact]
    public void YellowTeaArrayLayoutUsesManagedEdgesForNestedArraysAndRawPointerSlotsForCallbacks()
    {
        var verified = Compile("func first(): int = 41\n"
            + "object Main { func main(args: []String): () {\n"
            + "val callbacksA: []((() -> int)?) = [null, first]\n"
            + "val callbacksB: []((() -> int)?) = [func() { 42 }]\n"
            + "val nested: [][]((() -> int)?) = [callbacksA, callbacksB]\n"
            + "} }");

        Assert.Equal(new[] { 12, 12, 11 }, ArrayElementKinds(verified));
    }

    private static VerifiedFirProgram Compile(string source, bool library = false)
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(source)],
            TestContext.Current.CancellationToken, library: library);
        Assert.True(compilation.Program is not null, string.Join('\n', compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Span + ": " + diagnostic.Message)));
        Assert.Empty(compilation.Diagnostics);
        return Assert.IsType<VerifiedFirProgram>(compilation.Program);
    }

    private static FirSlot Slot(FirFunction function, string name) => Assert.Single(function.Slots,
        slot => slot.Name == name);

    private static FirDeclare Declaration(FirFunction function, FirSlot slot) =>
        Assert.Single(function.Body!.Statements.OfType<FirDeclare>(), declaration => declaration.Slot == slot.Id);

    private static FirProgram Replace(FirProgram program, FirFunction function) =>
        program with { Functions = program.Functions.SetItem(function.Id, function) };

    private static FirFunction ReplaceReturn(FirFunction function, FirReturn before, FirReturn after)
    {
        var statements = function.Body!.Statements.ToBuilder();
        int index = statements.IndexOf(before);
        Assert.True(index >= 0);
        statements[index] = after;
        return function with { Body = function.Body with { Statements = statements.ToImmutable() } };
    }

    private static FirFunction ReplaceInitializer(FirFunction function, FirDeclare before, FirDeclare after)
    {
        var statements = function.Body!.Statements.ToBuilder();
        int index = statements.IndexOf(before);
        Assert.True(index >= 0);
        statements[index] = after;
        return function with { Body = function.Body with { Statements = statements.ToImmutable() } };
    }

    private static int[] ArrayElementKinds(VerifiedFirProgram program)
    {
        using var trace = JsonDocument.Parse(LoweringTrace.Inspect(program,
            cancellationToken: TestContext.Current.CancellationToken));
        var operations = trace.RootElement.EnumerateArray().ToArray();
        var byResult = operations.ToDictionary(operation => operation.GetProperty("result").GetInt64());
        return operations.Where(operation => operation.GetProperty("operation").GetString() == "Call"
                && operation.GetProperty("callee").GetString() == "sol_rt_array_new")
            .Select(call => byResult[call.GetProperty("operands")[2].GetInt64()]
                .GetProperty("text").GetString()!)
            .Select(text => int.Parse(text, CultureInfo.InvariantCulture))
            .ToArray();
    }
}
