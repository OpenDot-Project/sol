// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class BodyAnalysisTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String {}\nopen class Throwable {}\n"
        + "open class Exception: Throwable {}\nopen class RuntimeException: Exception {}\n";

    private static BodyAnalysis Analyze(string text, params string[] extra)
    {
        var declarations = AstTestSources.Session(new[] { "module app\n" + text, Core }.Concat(extra).ToArray());
        var sources = SourceAnalysis.Create(declarations, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Code + " " + d.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }
    private static void Valid(BodyAnalysis analysis)
    { Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message + ": " + d.Source.GetText(d.Span)))); }
    private static void Error(BodyAnalysis analysis, string code) => Assert.Contains(analysis.Diagnostics, d => d.Code == code);

    [Fact]
    public void ArithmeticAndExpressionReturnInferenceUseExactTypes()
    {
        var result = Analyze("func add(a, b: int) = a + b\nfunc use(): int = add(1, 2)");
        Valid(result);
        Assert.True(result.IsComplete);
        Assert.All(result.Bodies.Where(b => b.Declaration is AstFunction), b => Assert.Equal("int", b.ReturnType!.Name));
    }

    [Theory]
    [InlineData("func f(): bool = 1", "SOL2206")]
    [InlineData("func f(a: int, b: long): int = a + b", "SOL2207")]
    [InlineData("func f(): int {}", "SOL2247")]
    [InlineData("func f(flag: bool): int { if flag { return 1 } }", "SOL2247")]
    [InlineData("func f(): int { var value: int; value }", "SOL2240")]
    [InlineData("func f(): () { val value = 1; value = 2 }", "SOL2241")]
    [InlineData("func f(): () { missing }", "SOL2211")]
    [InlineData("func f(): () { break }", "SOL2246")]
    [InlineData("func f() = f()", "SOL2200")]
    public void InvalidBodiesReportSpecificDiagnostics(string text, string code) => Error(Analyze(text), code);

    [Theory]
    [InlineData("func f(flag: bool): int { var x: int; if flag { x = 1 } else { x = 2 }; x }")]
    [InlineData("func f(flag: bool): int { if flag { return 1 } else { return 2 } }")]
    [InlineData("func f(): ! { for {} }")]
    [InlineData("func f(): int { var x: int; x = 2; x }")]
    [InlineData("func f(): int { val values = [1, 2]; values[0] }")]
    [InlineData("func f(): byte = 255")]
    [InlineData("func f(): (int, bool) = (1, true)")]
    public void ValidResultAndAssignmentPaths(string text) => Valid(Analyze(text));

    [Fact]
    public void BranchAssignmentRequiresEveryContinuingPath()
    { Error(Analyze("func f(flag: bool): int { var x: int; if flag { x = 1 }; x }"), "SOL2240"); }

    [Fact]
    public void OutContractChecksEveryNormalReturn()
    {
        Valid(Analyze("func assign(out value: int) { value = 1 }\nfunc use(): int { var value: int; assign(out value); value }"));
        Error(Analyze("func assign(flag: bool, out value: int) { if flag { return }; value = 1 }"), "SOL2242");
        Error(Analyze("func assign(out value: int) { val before = value; value = 1 }"), "SOL2240");
    }

    [Fact]
    public void RefCallsRequireWritableExactStorage()
    {
        Error(Analyze("func edit(ref x: int) {}\nfunc f() { val x = 1; edit(ref x) }"), "SOL2221");
        Valid(Analyze("func edit(ref x: int) { x = 3 }\nfunc f(): int { var x = 1; edit(ref x); x }"));
    }

    [Fact]
    public void NullableReceiverNeedsProofAndEarlyReturnPreservesNarrowing()
    {
        const string type = "class Item { val value: int = 1 }\n";
        Error(Analyze(type + "func f(item: Item?): int = item.value"), "SOL2243");
        Valid(Analyze(type + "func f(item: Item?): int { if item == null { return 0 }; item.value }"));
        Valid(Analyze(type + "func f(item: Item?): int = item?.value ?: 0"));
    }

    [Fact]
    public void MutablePropertyDoesNotBecomeSmartCastStorage()
    {
        Error(Analyze("class Item { var next: Item? = null; val value: int = 1 }\n"
            + "func f(item: Item): int { if item.next != null { return item.next.value }; 0 }"), "SOL2243");
    }

    [Fact]
    public void GenericInheritedMethodSubstitutesReceiverArguments()
    {
        var result = Analyze("open class Base<T> { func echo(value: T): T = value }\nclass Derived: Base<int> {}\n"
            + "func f(value: Derived): int = value.echo(42)");
        Valid(result);
        Assert.Equal("int", result.Expressions.Single(e => e.Expression.Origin.Text == "value.echo(42)").Type!.Name);
    }

    [Fact]
    public void InstanceObjectAndCompanionCallsAreResolved()
    {
        Valid(Analyze("object Clock { func now(): int = 1 }\nclass Factory { companion object { func count(): int = 2 } }\n"
            + "func f(): int = Clock.now() + Factory.count()"));
    }

    [Fact]
    public void VisibilityAndOverrideContractsAreEnforced()
    {
        Error(Analyze("class Item { private func secret(): int = 1 }\nfunc f(item: Item): int = item.secret()"), "SOL2214");
        Error(Analyze("open class Base { func f(): int = 1 }\nclass Derived: Base { override func f(): int = 2 }"), "SOL2232");
        Error(Analyze("open class Base { open func f(): int = 1 }\nclass Derived: Base { func f(): int = 2 }"), "SOL2231");
        Valid(Analyze("open class Base { open func f(): int = 1 }\nclass Derived: Base { override func f(): int = 2 }"));
    }

    [Fact]
    public void NamedAndPositionalLambdasUseExpectedSignatures()
    {
        Valid(Analyze("func combine(a, b: int, operation: (int, int) -> int): int = operation(a, b)\n"
            + "func f(): int = combine(1, 2) { x, y -> x + y }\nfunc g(): int = combine(1, 2) { $0 + $1 }"));
        Error(Analyze("func f() { val callback: (int) -> int = { $1 } }"), "SOL2220");
        Error(Analyze("func f() { val callback: (int) -> int = { it } }"), "SOL2220");
    }

    [Fact]
    public void HeaderlessLambdaInfersResultWithoutExecutingBody()
    {
        var result = Analyze("func f(): int { val answer = { 42 }; answer() }");
        Valid(result);
        Assert.Equal(ResolvedTypeKind.Function, result.Expressions.Single(e => e.Expression is AstLambda).Type!.Kind);
    }

    [Fact]
    public void CheckedCallsAndCatchesUseSubtypeBudgets()
    {
        const string functions = "class Failure: Exception {}\nfunc fail(): int throws Failure { for {} }\n";
        Error(Analyze(functions + "func f(): int = fail()"), "SOL2250");
        Valid(Analyze(functions + "func f(): int throws Exception = fail()"));
        Valid(Analyze(functions + "func f(): int = try { fail() } catch (e: Failure) { 0 }"));
        Error(Analyze(functions + "func f() { try { fail() } catch (e: Exception) {} catch (e: Failure) {} }"), "SOL2252");
    }

    [Fact]
    public void UncheckedThrowNeedsNoThrowsClause()
    { Valid(Analyze("func f(error: RuntimeException): ! { throw error }")); }

    [Fact]
    public void ThrowingAnOrdinaryValueIsRejected()
    { Error(Analyze("func f(): ! { throw 1 }"), "SOL2251"); }

    [Fact]
    public void LambdaEffectsAreCheckedAtCreationNotExecutedInOuterFlow()
    {
        Error(Analyze("func fail(error: Exception): ! throws Exception { throw error }\n"
            + "func f(error: Exception) { val callback: () -> ! = { fail(error) } }"), "SOL2253");
        Valid(Analyze("func f(error: Exception) { val callback: () -> ! throws Exception = { throw error } }"));
    }

    [Fact]
    public void RefAndRefLikeCapturesAreRejected()
    {
        Error(Analyze("func f(ref x: int) { val callback = { x } }"), "SOL2262");
        Error(Analyze("ref struct View {}\nfunc f(view: View) { val callback = { view } }"), "SOL2262");
    }

    [Fact]
    public void UnknownBorrowOriginsNeverCertifyCompleteAnalysis()
    {
        var result = Analyze("ref struct View {}\nfunc f(view: View): View { return view }");
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void PublicQueriesRejectForeignSnapshotIdentity()
    {
        var result = Analyze("func f(): int = 1");
        var foreign = AstTestSources.Parse("func f(): int = 1");
        Assert.Throws<ArgumentException>(() => result.GetExpression(foreign.Nodes.OfType<AstExpression>().First()));
        Assert.Throws<ArgumentException>(() => result.GetBody(foreign.Nodes.OfType<AstCallable>().First()));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => BodyAnalysis.Create(result.Sources, cancellation.Token));
    }
}
