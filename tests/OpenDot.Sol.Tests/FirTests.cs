// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FirTests
{
    private static FirCompilation Compile(string body, string declarations = "") => FirCompilation.Create([
        AstTestSources.Parse("import std.stdio\n" + declarations + "\nobject Main { func main(args: []String) throws IOException { " + body + " } }")], TestContext.Current.CancellationToken);
    private static VerifiedFirProgram Valid(FirCompilation compilation)
    {
        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        return compilation.Program;
    }

    [Fact]
    public void EntryLibraryCallsAndSourceFlowProduceVerifiedFir()
    {
        var program = Valid(Compile("println(\"Hello, Sol!\"); println(add(20, 22)); println(args.length)", "func add(a, b: int) = a + b"));
        Assert.Equal("main", program.Program.Functions[program.Program.Entry].Name);
        Assert.NotEmpty(program.FlowGraphs);
        Assert.All(program.FlowGraphs, g => Assert.Empty(g.Diagnostics));
        string llvm = LlvmEmitter.Emit(program, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("@wmain", llvm);
        Assert.Contains("llvm.sadd.with.overflow.i32", llvm);
        Assert.Contains("@sol_rt_arguments", llvm);
        Assert.Contains("@sol_rt_write_string", llvm);
        Assert.DoesNotContain("C:\\Users", llvm);
    }

    [Theory]
    [InlineData("var sum = 0; for var i: int = 0; i < 4; i += 1 { sum += i }; println(sum)")]
    [InlineData("var i = 0; for { i += 1; if i == 2 { continue }; if i == 4 { break } }; println(i)")]
    [InlineData("var x: int; fill(out x); println(x)", "func fill(out x: int) { x = 42 }")]
    [InlineData("var a = 1; var b = 2; swap(ref a, ref b); println(a, b)", "func swap(ref a, b: int) { val saved = a; a = b; b = saved }")]
    [InlineData("println(pick(b = 2, a = 1))", "func pick(a, b: int): int = a * 10 + b")]
    [InlineData("println(args[0])")]
    [InlineData("println(false && check())", "func check(): bool = true")]
    [InlineData("println(-2147483648)")]
    [InlineData("val x: byte = 255; println(x)")]
    [InlineData("println(1.5 + 2.5)")]
    [InlineData("println(\"\\u{1F642}\\0text\")")]
    public void NativeSubsetHasTypedControlAndStorage(string body, string declarations = "")
    {
        var program = Valid(Compile(body, declarations));
        Assert.NotEmpty(LlvmEmitter.Emit(program, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("var x: int; println(x)")]
    [InlineData("val x: int = true")]
    [InlineData("val callback = { 42 }; println(callback())")]
    [InlineData("defer {}; println(1)")]
    [InlineData("try { println(1) } catch (e: IOException) {}")]
    [InlineData("println(default<int>())")]
    [InlineData("println(id(1))", "func <T> id(value: T): T = value")]
    [InlineData("val values = [1, 2]; println(values.length)")]
    [InlineData("println(overflow())", "func overflow(): byte = 256")]
    [InlineData("println(1)", "val globalValue = 42")]
    [InlineData("stop()", "func stop(): ! { for {} }")]
    public void UnsafeOrUnqualifiedSourceNeverProducesVerifiedFir(string body, string declarations = "")
    {
        var result = Compile(body, declarations);
        Assert.Null(result.Program);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Theory]
    [InlineData("func main(args: []String) {}")]
    [InlineData("object Main { func main() {} }")]
    [InlineData("class Main { func main(args: []String) {} }")]
    [InlineData("object Main { private func main(args: []String) {} }")]
    public void EntryContractIsEnforced(string source)
    { Assert.Null(FirCompilation.Create([AstTestSources.Parse(source)], TestContext.Current.CancellationToken).Program); }

    [Fact]
    public void InspectionHasResolvedIdentityAndCfgWithoutHostPaths()
    {
        var program = Valid(Compile("println(args.length)"));
        string json = FirJson.Serialize(program, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("verified").GetBoolean());
        Assert.Equal(1, document.RootElement.GetProperty("schema").GetInt32());
        Assert.DoesNotContain("<solrt>", json);
        Assert.NotEmpty(document.RootElement.GetProperty("flow").EnumerateArray());
        Assert.Equal(json, FirJson.Serialize(program, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void VerifierRejectsTamperedReturnLiteralAndSlotIdentity()
    {
        var verified = Valid(Compile("println(answer())", "func answer(): int = 42"));
        var original = verified.Program;
        var answer = original.Functions.Single(f => f.Name == "answer");
        var ret = Assert.IsType<FirReturn>(Assert.Single(answer.Body!.Statements));
        var literal = Assert.IsType<FirConstant>(ret.Value);
        var bad = literal with { Spelling = "2147483648" };
        var function = answer with { Body = answer.Body with { Statements = [ret with { Value = bad }] } };
        Assert.Null(FirVerifier.Verify(original with { Functions = original.Functions.SetItem(function.Id, function) }, TestContext.Current.CancellationToken).Program);
        function = answer with { Body = answer.Body with { Statements = [ret with { Value = new FirRead(999, literal.Type, literal.Origin) }] } };
        Assert.Null(FirVerifier.Verify(original with { Functions = original.Functions.SetItem(function.Id, function) }, TestContext.Current.CancellationToken).Program);
    }

    [Fact]
    public void CfgIndependentlyRejectsOutExitAndUnassignedRead()
    {
        var verified = Valid(Compile("var x: int; fill(out x); println(x)", "func fill(out value: int) { value = 1 }"));
        var function = verified.Program.Functions.Single(f => f.Name == "fill");
        var returnNode = Assert.IsType<FirReturn>(function.Body!.Statements[^1]);
        var broken = function with { Body = function.Body with { Statements = [returnNode] } };
        var result = FirVerifier.Verify(verified.Program with { Functions = verified.Program.Functions.SetItem(function.Id, broken) }, TestContext.Current.CancellationToken);
        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, d => d.Code == "SOL2411");
    }

    [Fact]
    public void CancellationAndMissingInputHaveDefinedFailures()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => FirCompilation.Create([], cancellation.Token));
        Assert.Throws<ArgumentException>(() => FirCompilation.Create([], TestContext.Current.CancellationToken));
    }
}
