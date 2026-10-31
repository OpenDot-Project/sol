// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.IO;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FirSafetyTests
{
    private const string Source = """
        module mutation.native
        import std.stdio
        func fill(out value: int): bool { value = 42; true }
        object Main { func main(args: []String) throws IOException {
            var x: int; fill(out x); println(x); println(args.length)
        } }
        """;

    private static FirCompilation Compile(byte[] bytes)
    {
        var cancellation = TestContext.Current.CancellationToken;
        var syntax = SyntaxTree.Parse(SourceText.FromBytes(bytes), errorLimit: 40, maxDepth: 48, cancellationToken: cancellation);
        return FirCompilation.Create([AstSnapshot.Create(PsiSnapshot.Create(syntax, cancellation), cancellation)], cancellation);
    }

    private static VerifiedFirProgram Valid()
    {
        var result = Compile(SourceText.FromString(Source).Bytes.ToArray());
        Assert.Empty(result.Diagnostics);
        return Assert.IsType<VerifiedFirProgram>(result.Program);
    }

    [Theory]
    [InlineData("println(12 / 3)", "SDiv")]
    [InlineData("println(12 % 5)", "SRem")]
    [InlineData("val a: uint = 12; val b: uint = 3; println(a / b)", "UDiv")]
    [InlineData("val a: ulong = 12; val b: ulong = 5; println(a % b)", "URem")]
    [InlineData("println(12 - 3)", "llvm.ssub.with.overflow.i32")]
    [InlineData("println(12 * 3)", "llvm.smul.with.overflow.i32")]
    [InlineData("println(12 & 3)", "And")]
    [InlineData("println(12 | 3)", "Or")]
    [InlineData("println(12 ^ 3)", "Xor")]
    [InlineData("println(~12)", "Xor")]
    [InlineData("println(!false)", "Xor")]
    [InlineData("val a = 12; println(-a)", "llvm.ssub.with.overflow.i32")]
    [InlineData("val a: long = 12; println(a << 2)", "ZExt")]
    [InlineData("val a: byte = 12; println(a >> 2)", "Trunc")]
    [InlineData("println(12 >> 2)", "AShr")]
    [InlineData("val a: ulong = 12; println(a >> 2)", "LShr")]
    [InlineData("println(1.5 != 2.5)", "FCmp")]
    [InlineData("println(1.5 <= 2.5)", "FCmp")]
    [InlineData("println(2.5 >= 1.5)", "FCmp")]
    [InlineData("val a: single = 1.5; println(a * a)", "FMul")]
    public void ScalarOperationsSelectQualifiedInstructionsAndGuardPoisonPaths(string body, string instruction)
    {
        string source = "module native.operations; import std.stdio; object Main { func main(args: []String) throws IOException { " + body + " } }";
        var result = Compile(SourceText.FromString(source).Bytes.ToArray());
        var program = Assert.IsType<VerifiedFirProgram>(result.Program);
        string ir = LoweringTrace.Inspect(program, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(instruction.StartsWith("llvm.", StringComparison.Ordinal) ? "\"callee\":\"" + instruction + "\"" : "\"operation\":\"" + instruction + "\"", ir);
        if (instruction == "SDiv" || instruction == "SRem")
        {
            Assert.Contains("sol_rt_fail", ir);
            Assert.Contains("sol_rt_fail", ir);
        }
        if (body.Contains(">>", StringComparison.Ordinal) || body.Contains("<<", StringComparison.Ordinal))
        { Assert.Contains("sol_rt_fail", ir); }
    }

    [Fact]
    public void MissingBackendIsAnExplicitInstallationFailureWithoutCreatingAnArtifact()
    {
        string missing = Path.Combine(Path.GetTempPath(), "sol-absent-" + Guid.NewGuid().ToString("N"));
        var error = Assert.Throws<IOException>(() => NativeCompiler.Compile(Valid(), backendDirectory: missing, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("not installed", error.Message);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void FloatingNegationPreservesSignedZeroWithoutSubtractingFromPositiveZero()
    {
        var result = Compile(SourceText.FromString("module signed.zero; import std.stdio; object Main { func main(args: []String) throws IOException { val zero = 0.0; println(-zero) } }").Bytes.ToArray());
        var program = Assert.IsType<VerifiedFirProgram>(result.Program);
        Assert.Contains("FNeg", LoweringTrace.Inspect(program, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void BranchingLoopHeadersAreRejectedBeforeLowering()
    {
        var program = Valid().Program;
        var entry = program.Functions[program.Entry];
        var origin = entry.Origin;
        var loop = new FirLoop(new FirReturn(null, origin), null, new FirBlock([], origin), null, origin);
        var changed = entry with { Body = new FirBlock([loop], origin) };
        var result = FirVerifier.Verify(program with { Functions = program.Functions.SetItem(entry.Id, changed) }, TestContext.Current.CancellationToken);
        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, d => d.Code == "SOL2402");
    }

    [Fact]
    public void ShortCircuitOutWriteCannotInitializeTheSkippedPath()
    {
        var program = Valid().Program;
        var entry = program.Functions[program.Entry];
        var evaluation = Assert.IsType<FirEvaluate>(entry.Body!.Statements[1]);
        var call = Assert.IsType<FirCall>(evaluation.Value);
        var skipped = new FirBinary("&&", new FirConstant("false", call.Type, call.Origin), call, call.Type, call.Origin);
        var tampered = entry with { Body = entry.Body with { Statements = entry.Body.Statements.SetItem(1, evaluation with { Value = skipped }) } };
        var changedFunctions = program.Functions.SetItem(entry.Id, tampered);
        var result = FirVerifier.Verify(program with { Functions = changedFunctions }, TestContext.Current.CancellationToken);
        Assert.Null(result.Program);
        var flow = FirFlowGraph.Create(tampered, changedFunctions, TestContext.Current.CancellationToken);
        Assert.Contains(flow.Diagnostics, diagnostic => diagnostic.Code == "SOL2410");
    }

    [Fact]
    public void AdapterIdentityAndDuplicateParametersCannotMintACapability()
    {
        var program = Valid().Program;
        var adapter = program.Functions.First(f => f.RuntimeFunction == FirRuntimeFunction.Print);
        var changed = adapter with { RuntimeFunction = FirRuntimeFunction.Errorln };
        Assert.Null(FirVerifier.Verify(program with { Functions = program.Functions.SetItem(adapter.Id, changed) }, TestContext.Current.CancellationToken).Program);
        var fill = program.Functions.Single(f => f.Name == "fill");
        changed = fill with { Parameters = [fill.Parameters[0], fill.Parameters[0]] };
        Assert.Null(FirVerifier.Verify(program with { Functions = program.Functions.SetItem(fill.Id, changed) }, TestContext.Current.CancellationToken).Program);
    }

    [Fact]
    public void EveryTokenDeletionEitherRejectsOrProducesDeterministicLoweringCalls()
    {
        var source = SourceText.FromString(Source);
        var syntax = SyntaxTree.Parse(source, cancellationToken: TestContext.Current.CancellationToken);
        foreach (var token in syntax.Tokens.Where(t => t.Kind != SyntaxKind.EndOfFileToken))
        {
            byte[] mutated = new byte[source.Length - token.Span.Length];
            source.Bytes[..token.Span.Start].CopyTo(mutated);
            source.Bytes[token.Span.End..].CopyTo(mutated.AsSpan(token.Span.Start));
            Check(mutated);
        }
    }

    [Fact]
    public void SeededByteInsertionsExerciseFirAndLoweringPolicy()
    {
        byte[] original = SourceText.FromString(Source).Bytes.ToArray();
        var random = new Random(20261023);
        for (int sample = 0; sample < 40; sample++)
        {
            int position = random.Next(original.Length + 1);
            byte[] mutated = new byte[original.Length + 1];
            original.AsSpan(0, position).CopyTo(mutated);
            original.AsSpan(position).CopyTo(mutated.AsSpan(position + 1));
            mutated[position] = (byte)random.Next(256);
            Check(mutated);
        }
    }

    private static void Check(byte[] bytes)
    {
        var result = Compile(bytes);
        Assert.All(result.Diagnostics, d => d.Source.Validate(d.Span));
        if (result.Program is not { } program) { Assert.NotEmpty(result.Diagnostics); return; }
        Assert.Empty(result.Diagnostics);
        var cancellation = TestContext.Current.CancellationToken;
        string ir = LoweringTrace.Inspect(program, cancellationToken: cancellation);
        Assert.Contains("wmain", ir);
        Assert.Equal(ir, LoweringTrace.Inspect(program, cancellationToken: cancellation));
        if (NativeRuntimeIntegrationTests.Enabled)
        {
            using var module = LlvmEmitter.Build(program, NativeCompiler.LoadBridge(NativeCompiler.BackendRoot(null)), cancellation);
            module.Verify();
        }
    }
}
