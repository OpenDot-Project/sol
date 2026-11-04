// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
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
    public void OrcExecutesTypedFunctionValuesAndIndirectRefCalls()
    {
        const string source = "import std.stdio\n"
            + "func increment(value: int): int = value + 1\n"
            + "func <T> apply(value: T, callback: (T) -> T): T = callback(value)\n"
            + "func incrementRef(ref value: int): () { value += 1 }\n"
            + "func terminate(callback: () -> !): ! = callback()\n"
            + "object Main { func main(args: []String) throws IOException {\n"
            + "val callback: (int) -> int = increment; var answer = apply(40, callback);\n"
            + "val step: (ref int) -> () = incrementRef; step(ref answer); println(answer)\n"
            + "} }";
        var compilation = FirCompilation.Create([AstTestSources.Parse(source)], TestContext.Current.CancellationToken);
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program);
        using var output = new MemoryStream();
        using var error = new MemoryStream();

        using (var module = LlvmEmitter.Build(program, Compiler(), TestContext.Current.CancellationToken))
        {
            string ir = module.Print();
            Assert.Contains("call void %", ir, StringComparison.Ordinal);
            Assert.Equal(new byte[] { 0x42, 0x43, 0xC0, 0xDE }, module.Bitcode()[..4]);
            Assert.Equal(new byte[] { 0x64, 0x86 }, module.CompileObject()[..2]);
        }

        int exitCode = NativeOrcExecutable.Execute(program, [], cancellationToken: TestContext.Current.CancellationToken,
            standardOutput: output, standardError: error);
        Assert.Equal(0, exitCode);
        Assert.Equal("42\n", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Empty(error.ToArray());
    }

    [Fact(Skip = "Runs in required native-backend CI.", SkipUnless = nameof(Enabled), Timeout = 60000)]
    public void CppBridgeBuildsAndRejectsMismatchedIndirectCallSignatures()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        using var module = Compiler().CreateModule();
        long i32 = Apply(module, LlvmOperation.IntegerType, data: 32);
        long i64 = Apply(module, LlvmOperation.IntegerType, data: 64);
        long signature = Apply(module, LlvmOperation.FunctionType, i32, [i32]);
        long function = Apply(module, LlvmOperation.Function, i32, [i32], text: "indirect_target");
        long block = Apply(module, LlvmOperation.Block, args: [function]);
        Apply(module, LlvmOperation.Position, args: [block]);
        long parameter = Apply(module, LlvmOperation.Parameter, args: [function], data: 0);
        long wrongArgument = Apply(module, LlvmOperation.Integer, i64, text: "1");
        Assert.Throws<InvalidOperationException>(() => Apply(module, LlvmOperation.IndirectCall,
            signature, [function, wrongArgument]));
        long call = Apply(module, LlvmOperation.IndirectCall, signature, [function, parameter]);
        Apply(module, LlvmOperation.Return, args: [call]);

        module.Verify();
        Assert.Contains("call i32 ", module.Print(), StringComparison.Ordinal);
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
    public void ExecutableDataRetainsBinaryBytesAndRejectsDuplicateSymbols()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var module = Compiler().CreateModule();
        byte[] payload = [0, 0xFF, 0xC0, 0xDE, 0, 17];
        Assert.NotEqual(0, module.CreateData(payload, "sol_binary_payload"));
        Assert.Throws<InvalidOperationException>(() => module.CreateData(payload, "sol_binary_payload"));
        Assert.Throws<ArgumentException>(() => module.CreateData(payload, "bad\0name"));
        module.Verify();
        Assert.Contains("@sol_binary_payload", module.Print());
        Assert.Contains("constant [6 x i8]", module.Print());
        byte[] coff = module.CompileObject();
        Assert.Equal(0x64, coff[0]); Assert.Equal(0x86, coff[1]);
    }

    [Fact(Skip = "Runs in required native-backend CI.", SkipUnless = nameof(Enabled), Timeout = 60000)]
    public void OrcSupportsStackProbesForLargePreciseRootFrames()
    {
        string locals = string.Join("\n", Enumerable.Range(0, 600).Select(i => $"val value{i} = \"x\";"));
        var compilation = FirCompilation.Create([AstTestSources.Parse("import std.stdio; object Main { func main(args: []String) throws IOException { " + locals + " println(value599) } }")], TestContext.Current.CancellationToken);
        using var output = new MemoryStream(); using var error = new MemoryStream();
        int code = NativeOrcExecutable.Execute(Assert.IsType<VerifiedFirProgram>(compilation.Program), [], cancellationToken: TestContext.Current.CancellationToken, standardOutput: output, standardError: error);
        Assert.Equal(0, code); Assert.Equal("x\n", Encoding.UTF8.GetString(output.ToArray())); Assert.Empty(error.ToArray());
    }

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

}
