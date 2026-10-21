// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

[Collection("CompilerConsole")]
public sealed class NativeLlvmBridgeTests
{
    public static bool Enabled => NativeRuntimeIntegrationTests.Enabled;
    private static ILlvmCompiler Compiler() => NativeCompiler.LoadBridge(NativeCompiler.BackendRoot(null));
    private static long Apply(ILlvmModule module, LlvmOperation op, long type = 0, long[]? args = null, long data = 0, string text = "")
        => module.Apply(op, type, args ?? [], data, text);

    [Fact(Skip = "Runs in required native-backend CI.", SkipUnless = nameof(Enabled), Timeout = 60000)]
    public void CppSessionPrintsInspectionAndProducesBinaryBitcodeAndCoff()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("object Main { func main(args: []String) {} }")], TestContext.Current.CancellationToken);
        var compiler = Compiler(); Assert.Equal("23.1.3", compiler.Version);
        using var module = LlvmEmitter.Build(Assert.IsType<VerifiedFirProgram>(compilation.Program), compiler, TestContext.Current.CancellationToken);
        module.Verify(); string printed = module.Print();
        Assert.Contains("define i32 @wmain", printed);
        byte[] bitcode = module.Bitcode(); Assert.Equal(new byte[] { 0x42, 0x43, 0xC0, 0xDE }, bitcode[..4]);
        byte[] coff = module.CompileObject(); Assert.Equal(new byte[] { 0x64, 0x86 }, coff[..2]);
        Assert.Equal(printed, module.Print()); // Optimization operates on a clone, preserving inspection/bitcode identity.
        Assert.Equal(bitcode, module.Bitcode()); Assert.Equal(coff, module.CompileObject());
        Assert.DoesNotContain(Process.GetCurrentProcess().Modules.Cast<ProcessModule>(), m => m.ModuleName.Equals("LLVM-C.dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Process.GetCurrentProcess().Modules.Cast<ProcessModule>(), m => m.ModuleName.Equals("SolRT.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(Skip = "Runs in required native-backend CI.", SkipUnless = nameof(Enabled), Timeout = 60000)]
    public async Task SessionsRejectForeignWrongKindCrossThreadAndDisposedHandles()
    {
        var compiler = Compiler(); using var first = compiler.CreateModule(); var second = compiler.CreateModule();
        long integer = Apply(first, LlvmOperation.IntegerType, data: 32);
        Assert.Throws<InvalidOperationException>(() => Apply(second, LlvmOperation.ArrayType, integer, data: 4));
        long secondInteger = Apply(second, LlvmOperation.IntegerType, data: 32);
        long value = Apply(second, LlvmOperation.Integer, secondInteger, text: "42");
        Assert.Throws<InvalidOperationException>(() => Apply(second, LlvmOperation.ArrayType, value, data: 4));
        Assert.Throws<InvalidOperationException>(() => Apply(second, LlvmOperation.Integer, secondInteger, text: "1-2"));
        Assert.Throws<InvalidOperationException>(() => Apply(second, LlvmOperation.Integer, secondInteger, text: "4294967296"));
        Assert.Throws<InvalidOperationException>(() => Apply(second, (LlvmOperation)1000));
        long empty = Apply(second, LlvmOperation.VoidType);
        long function = Apply(second, LlvmOperation.Function, empty, text: "empty_block_regression");
        long block = Apply(second, LlvmOperation.Block, args: [function]); Apply(second, LlvmOperation.Position, args: [block]);
        Apply(second, LlvmOperation.Alloca, secondInteger); // Empty blocks have no terminator; querying one must not assert.
        Apply(second, LlvmOperation.Return); second.Verify();
        Assert.Throws<InvalidOperationException>(() => Apply(second, LlvmOperation.Alloca, secondInteger));
        await Task.Run(() => Assert.Throws<InvalidOperationException>(() => first.Verify()), TestContext.Current.CancellationToken);
        second.Dispose(); second.Dispose(); Assert.Throws<ObjectDisposedException>(() => second.Verify());
        Assert.Throws<ObjectDisposedException>(() => Apply(second, LlvmOperation.VoidType));
    }

    [Fact(Skip = "Runs in required native-backend CI.", SkipUnless = nameof(Enabled), Timeout = 60000)]
    public async Task NativeOrcRejectsWrongRuntimeAbiUsingBuilderProducedBitcode()
    {
        using var module = Compiler().CreateModule();
        long i32 = Apply(module, LlvmOperation.IntegerType, data: 32), i64 = Apply(module, LlvmOperation.IntegerType, data: 64);
        long pointer = Apply(module, LlvmOperation.PointerType), empty = Apply(module, LlvmOperation.VoidType);
        long check = Apply(module, LlvmOperation.Function, empty, [i64], text: "sol_rt_check_abi");
        long main = Apply(module, LlvmOperation.Function, i32, [i32, pointer], text: "wmain");
        long block = Apply(module, LlvmOperation.Block, args: [main]); Apply(module, LlvmOperation.Position, args: [block]);
        long zero = Apply(module, LlvmOperation.Integer, i64, text: "0"); Apply(module, LlvmOperation.Call, args: [check, zero]);
        Apply(module, LlvmOperation.Return, args: [Apply(module, LlvmOperation.Integer, i32, text: "0")]); module.Verify();
        var result = await RunGuest(module.Bitcode(), TestContext.Current.CancellationToken); Assert.Equal(1, result.Code); Assert.Contains("runtime contract mismatch", result.Error);
    }

    [Theory(Skip = "Runs in required native-backend CI.", SkipUnless = nameof(Enabled), Timeout = 60000)]
    [InlineData("target triple = \"x86_64-pc-windows-msvc\"")]
    [InlineData("BCxx")]
    public async Task OrcDoesNotFallBackToParsingText(string bytes)
    {
        var result = await RunGuest(Encoding.UTF8.GetBytes(bytes), TestContext.Current.CancellationToken); Assert.Equal(1, result.Code); Assert.Contains("Sol ORC host:", result.Error);
    }

    [Fact(Skip = "Runs in required native-backend CI.", SkipUnless = nameof(Enabled), Timeout = 60000)]
    public void OrcSupportsStackProbesForLargePreciseRootFrames()
    {
        string locals = string.Join("\n", Enumerable.Range(0, 600).Select(i => $"val value{i} = \"x\";"));
        var compilation = FirCompilation.Create([AstTestSources.Parse("import std.stdio; object Main { func main(args: []String) throws IOException { " + locals + " println(value599) } }")], TestContext.Current.CancellationToken);
        using var output = new MemoryStream(); using var error = new MemoryStream();
        int code = OrcExecutor.Execute(Assert.IsType<VerifiedFirProgram>(compilation.Program), [], cancellationToken: TestContext.Current.CancellationToken, standardOutput: output, standardError: error);
        Assert.Equal(0, code); Assert.Equal("x\n", Encoding.UTF8.GetString(output.ToArray())); Assert.Empty(error.ToArray());
    }

    private static async Task<(int Code, string Error)> RunGuest(byte[] bitcode, CancellationToken cancellationToken)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "sol-cpp-api-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        try
        {
            string path = Path.Combine(scratch, "program.bc"); File.WriteAllBytes(path, bitcode);
            var info = new ProcessStartInfo(Path.Combine(NativeCompiler.BackendRoot(null), "sol-exec-host.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(path); info.ArgumentList.Add("--");
            using var process = Process.Start(info)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(cancellationToken); }
            catch { if (!process.HasExited) { process.Kill(true); process.WaitForExit(30000); } throw; }
            await output; return (process.ExitCode, await error);
        }
        finally { Directory.Delete(scratch, true); }
    }
}
