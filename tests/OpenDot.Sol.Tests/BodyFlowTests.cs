// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class BodyFlowTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String {}\nopen class Throwable {}\n"
        + "open class Exception: Throwable {}\nopen class RuntimeException: Exception {}\n";
    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session("module app\n" + source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }
    private static void Valid(BodyAnalysis result) => Assert.False(result.HasErrors,
        string.Join('\n', result.Diagnostics.Select(d => d.Code + " " + d.Message + ": " + d.Source.GetText(d.Span))));
    private static void Error(BodyAnalysis result, string code) => Assert.Contains(result.Diagnostics, d => d.Code == code);

    [Theory]
    [InlineData("func f(): sbyte = -128")]
    [InlineData("func f(): int = -2147483648")]
    [InlineData("func f(): long = -9223372036854775808")]
    [InlineData("func f(): int { var items: []int = []; items.length }")]
    [InlineData("func f(): int { var items: []int = [1, 2]; items[0] = 3; items[0] }")]
    [InlineData("func f(flag: bool): int { var x: int; if flag { return 1 } else { x = 2 }; x }")]
    [InlineData("func f(out x: int) { try { return } finally { x = 1 } }")]
    public void ValidFlowEdges(string source) => Valid(Analyze(source));

    [Theory]
    [InlineData("func f(): byte = -1", "SOL2201")]
    [InlineData("func f(): int = 2147483648", "SOL2201")]
    [InlineData("func f(a, b: double): double = a & b", "SOL2207")]
    [InlineData("func f(a: double): double = ~a", "SOL2207")]
    [InlineData("func f(a, b: long): long = a << b", "SOL2207")]
    [InlineData("func f(): int { var x: int; for false { x = 1 }; x }", "SOL2240")]
    [InlineData("func f(): int { for { break } }", "SOL2247")]
    [InlineData("func f(): int { return 1; 2 }", "SOL2245")]
    [InlineData("func f(flag: bool): int { var x: int; flag && assign(out x); x }\nfunc assign(out x: int): bool { x = 1; true }", "SOL2240")]
    public void InvalidFlowEdges(string source, string code) => Error(Analyze(source), code);

    [Fact]
    public void SafeCallDoesNotDefinitelyAssignArgumentsOnSkippedPath()
    {
        Error(Analyze("class Writer { func fill(out value: int) { value = 1 } }\n"
            + "func f(writer: Writer?): int { var value: int; writer?.fill(out value); value }"), "SOL2240");
    }

    [Fact]
    public void ShortCircuitNullCheckRefinesOnlyItsRightOperand()
    {
        Valid(Analyze("class Item { val value: int = 1 }\nfunc f(item: Item?): bool = item != null && item.value > 0"));
        Error(Analyze("class Item { val value: int = 1 }\nfunc f(item: Item?): bool = item == null && item.value > 0"), "SOL2243");
    }

    [Fact]
    public void AWriteInvalidatesAFormerSmartCast()
    {
        Error(Analyze("class Item { val value: int = 1 }\nfunc f(original: Item?): int { var item = original; "
            + "if item == null { return 0 }; item = null; item.value }"), "SOL2243");
    }

    [Fact]
    public void CatchReceivesOnlyPreTryDefiniteAssignments()
    {
        Error(Analyze("func risky(): () throws Exception {}\nfunc f(): int { var x: int; try { risky(); x = 1 } catch (e: Exception) {}; x }"), "SOL2240");
    }

    [Fact]
    public void ReturnInsideTryDoesNotLeakCaughtCheckedEffect()
    {
        Valid(Analyze("func risky(): int throws Exception { for {} }\n"
            + "func f(): int { try { return risky() } catch (e: Exception) { return 0 } }"));
    }

    [Fact]
    public void CatchBodyAndFinallyEffectsStillEscape()
    {
        Error(Analyze("func risky(): () throws Exception {}\nfunc f() { try {} finally { risky() } }"), "SOL2250");
        Error(Analyze("func risky(): () throws Exception {}\nfunc f() { try { risky() } catch (e: Exception) { risky() } }"), "SOL2250");
    }

    [Fact]
    public void NullableThrowAndCatchOfNonThrowableAreRejected()
    {
        Error(Analyze("func f(error: Exception?): ! throws Exception { throw error }"), "SOL2251");
        Error(Analyze("func f() { try {} catch (e: int) {} }"), "SOL2251");
    }

    [Fact]
    public void OverrideCannotReduceVisibilityOrBroadenCheckedBudget()
    {
        Error(Analyze("open class Base { open func f(): () {} }\nclass Child: Base { private override func f(): () {} }"), "SOL2233");
        Error(Analyze("open class Base { open func f(): () {} }\nclass Child: Base { override func f(): () throws Exception {} }"), "SOL2235");
    }

    [Fact]
    public void UnrelatedProtectedAccessAndPrivateSetterAreRejected()
    {
        Error(Analyze("class Item { protected func f(): int = 1 }\nfunc read(item: Item): int = item.f()"), "SOL2214");
        Error(Analyze("class Item { var value: int { get; private set; } = 1 }\nfunc write(item: Item) { item.value = 2 }"), "SOL2241");
    }

    [Fact]
    public void GenericBodiesResolveMembersFromConstraints()
    {
        Valid(Analyze("interface Named { func name(): int }\nfunc <T: Named> get(value: T): int = value.name()"));
        Error(Analyze("func <T> get(value: T): int = value.name()"), "SOL2215");
    }

    [Fact]
    public void FunctionEffectsRemainPartOfLambdaTypes()
    {
        var result = Analyze("func f() { val callback: (int) -> int = { x -> x + 1 } }");
        Valid(result);
        var lambda = result.Expressions.Single(e => e.Expression is AstLambda).Type!;
        Assert.Equal(ResolvedTypeKind.Function, lambda.Kind);
        Assert.Equal("int", lambda.Arguments[^1].Name);
        Assert.Empty(lambda.Effects);
    }

    [Fact]
    public void RefLikeHeapFieldsAreRejected()
    { Error(Analyze("ref struct View {}\nclass Holder { var value: View }"), "SOL2260"); }

    [Fact]
    public void UnimplementedCleanupProofIsVisibleInResult()
    {
        var result = Analyze("func f() { defer {} }");
        Assert.False(result.IsComplete);
        Assert.NotEmpty(result.UnverifiedNodes);
        Assert.Contains(result.UnverifiedNodes, n => n.Origin.Text.StartsWith("defer", StringComparison.Ordinal));
    }
}
