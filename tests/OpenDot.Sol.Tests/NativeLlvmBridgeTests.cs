// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Pipes;
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
        await Task.Factory.StartNew(() => Assert.Throws<InvalidOperationException>(() => first.Verify()), TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
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

    [Theory(Skip = "Runs in required native-backend CI.", SkipUnless = nameof(Enabled), Timeout = 60000)]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(67108865u)]
    [InlineData(uint.MaxValue)]
    [InlineData(16u)]
    public async Task OrcRejectsInvalidOrTruncatedPipeFrames(uint declaredLength)
    {
        foreach (bool staticRuntime in new[] { false, true })
        {
            var result = await RunGuest([0x42, 0x43, 0xC0, 0xDE], TestContext.Current.CancellationToken, declaredLength, staticRuntime);
            Assert.Equal(1, result.Code); Assert.Contains("bitcode pipe", result.Error);
        }
    }

    [Theory(Skip = "Runs in required native-backend CI.", SkipUnless = nameof(Enabled), Timeout = 60000)]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("18446744073709551616")]
    [InlineData("../program.bc")]
    [InlineData("1")]
    public async Task OrcRejectsNonPipeArguments(string handle)
    {
        var info = GuestInfo(false);
        info.ArgumentList.Add("--bitcode-pipe"); info.ArgumentList.Add(handle); info.ArgumentList.Add("--");
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken); var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, process.ExitCode); Assert.Contains("pipe", await error); Assert.Empty(await output);
    }

    private static ProcessStartInfo GuestInfo(bool staticRuntime) => new(Path.Combine(NativeCompiler.BackendRoot(null), staticRuntime ? "sol-exec-host-static.exe" : "sol-exec-host.exe"))
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };

    [Fact(Skip = "Runs in required native-backend CI.", SkipUnless = nameof(Enabled), Timeout = 60000)]
    public void EmbeddedLldCanRecoverFromOrdinaryErrorsAndLinkAgain()
    {
        var compiler = Compiler(); var linker = Assert.IsAssignableFrom<ILldLinker>(compiler);
        Assert.Equal(0, linker.LinkWindows(["--version"], out string version));
        Assert.Contains("23.1.3", version);
        Assert.Throws<InvalidOperationException>(() => linker.LinkWindows([], out _));
        Assert.Throws<ArgumentException>(() => linker.LinkWindows(["bad\0argument"], out _));
        for (int index = 0; index < 2; index++)
        {
            Assert.NotEqual(0, linker.LinkWindows(["/invalid-sol-test-option"], out string diagnostics));
            Assert.NotEmpty(diagnostics);
        }
        var compilation = FirCompilation.Create([AstTestSources.Parse("object Main { func main(args: []String) {} }")], TestContext.Current.CancellationToken);
        Assert.NotEmpty(NativeCompiler.Compile(Assert.IsType<VerifiedFirProgram>(compilation.Program), cancellationToken: TestContext.Current.CancellationToken).Executable);
    }

    [Fact(Skip = "Runs in required native-backend CI.", SkipUnless = nameof(Enabled), Timeout = 60000)]
    public async Task EmbeddedLldSerializesConcurrentDriverInvocations()
    {
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var compilation = FirCompilation.Create([AstTestSources.Parse("func add(a, b: int): int = a + b", module: "library")], TestContext.Current.CancellationToken, true);
            Assert.NotEmpty(NativeCompiler.Compile(Assert.IsType<VerifiedFirProgram>(compilation.Program),
                cancellationToken: TestContext.Current.CancellationToken, kind: NativeArtifactKind.Cdylib).Executable);
        }, TestContext.Current.CancellationToken)));
    }

    [Theory(Skip = "Runs in required native-backend CI.", SkipUnless = nameof(Enabled), Timeout = 60000)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OrcRejectsTruncatedPipeHeaders(int headerBytes)
    {
        var result = await RunGuest([], TestContext.Current.CancellationToken, headerBytes: headerBytes);
        Assert.Equal(1, result.Code); Assert.Contains("Truncated LLVM bitcode pipe", result.Error);
    }

    [Fact(Skip = "Runs in required native-backend CI.", SkipUnless = nameof(Enabled), Timeout = 60000)]
    public async Task OrcDoesNotAcceptModuleFilePaths()
    {
        var info = GuestInfo(false); info.ArgumentList.Add("../program.bc"); info.ArgumentList.Add("--");
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken); var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, process.ExitCode); Assert.Contains("Usage:", await error); Assert.Empty(await output);
    }

    private static async Task<(int Code, string Error)> RunGuest(byte[] bitcode, CancellationToken cancellationToken, uint? declaredLength = null, bool staticRuntime = false, int headerBytes = 4)
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var info = GuestInfo(staticRuntime);
        info.ArgumentList.Add("--bitcode-pipe"); info.ArgumentList.Add(pipe.GetClientHandleAsString()); info.ArgumentList.Add("--");
        using var process = Process.Start(info)!; pipe.DisposeLocalCopyOfClientHandle();
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken); var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            byte[] header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, declaredLength ?? (uint)bitcode.Length);
            try { await pipe.WriteAsync(header.AsMemory(0, headerBytes), cancellationToken); await pipe.WriteAsync(bitcode, cancellationToken); }
            catch (IOException) when (declaredLength is not null) { /* Rejected framing can close the reader early. */ }
            pipe.Dispose();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch { if (!process.HasExited) { process.Kill(true); process.WaitForExit(30000); } throw; }
        await output; return (process.ExitCode, await error);
    }
}
