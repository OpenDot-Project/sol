// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

[Collection("CompilerConsole")]
public sealed class NativeNullableFunctionValueTests
{
    private const string Source = "import std.stdio\n"
        + "func choose(enabled: bool): (() -> int)? {\n"
        + "if enabled { return func() { 42 } }\n"
        + "return null\n}"
        + "func accept(callback: (() -> int)?): int {\n"
        + "if callback != null { return callback() }\n"
        + "return 0\n}"
        + "func invokeAfterGuard(callback: (() -> int)?): int {\n"
        + "if callback == null { return 0 }\n"
        + "callback()\n}"
        + "func invokeAcrossAnd(callback: (() -> int)?): int {\n"
        + "if callback != null && callback() == 41 { return callback() }\n"
        + "0\n}"
        + "func invokeAcrossOr(callback: (() -> int)?): int {\n"
        + "if callback == null || callback() == 41 { return 42 }\n"
        + "0\n}"
        + "func set(out callback: (() -> int)?): () { callback = func() { 42 } }"
        + "func clear(ref callback: (() -> int)?): () { callback = null }"
        + "func callOrDefault(callback: (() -> int)? = null): int {\n"
        + "if callback != null { return callback() }\n"
        + "return 0\n}"
        + "func firstAvailable(vararg callbacks: (() -> int)?): int {\n"
        + "val callback = callbacks[1]\n"
        + "if callback != null { return callback() }\n"
        + "return 0\n}"
        + "object Main {\n"
        + "func main(args: []String) throws IOException {\n"
        + "var callback: (() -> int)? = null\n"
        + "if callback == null { callback = func() { 41 } }\n"
        + "if callback != null { println(callback() + 1) }\n"
        + "val missing = choose(false)\n"
        + "if missing == null { println(0) }\n"
        + "val supplied = choose(true)\n"
        + "if supplied != null { println(supplied()) }\n"
        + "val callbacks: []((() -> int)?) = [null, func() { 42 }]\n"
        + "Gc.collect()\n"
        + "val first = callbacks[0]\n"
        + "if first == null { println(0) }\n"
        + "val second = callbacks[1]\n"
        + "if second != null { println(second()) }\n"
        + "println(accept(callback))\n"
        + "if null != callback { println(callback()) }\n"
        + "println(invokeAfterGuard(callback))\n"
        + "println(invokeAfterGuard(null))\n"
        + "println(invokeAcrossAnd(callback))\n"
        + "println(invokeAcrossAnd(null))\n"
        + "println(invokeAcrossOr(callback))\n"
        + "println(invokeAcrossOr(null))\n"
        + "var output: (() -> int)?\n"
        + "set(out output)\n"
        + "if output != null { println(output()) }\n"
        + "clear(ref output)\n"
        + "if output == null { println(0) }\n"
        + "println(callOrDefault())\n"
        + "println(callOrDefault(func() { 42 }))\n"
        + "println(firstAvailable(null, func() { 42 }))\n"
        + "callback = null\n"
        + "if callback == null { println(0) }\n"
        + "}\n}"
        ;

    public static bool Enabled => NativeRuntimeIntegrationTests.Enabled;

    private static VerifiedFirProgram Compile()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(Source)],
            TestContext.Current.CancellationToken);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Span + ": " + diagnostic.Message)));
        return Assert.IsType<VerifiedFirProgram>(compilation.Program);
    }

    [Fact]
    public void NullableFunctionTypesKeepTheirPointerShapeInVerifiedFir()
    {
        var program = Compile().Program;
        var chooser = Assert.Single(program.Functions, function => function.Name == "choose");
        var callbackType = chooser.ReturnType;

        Assert.True(FirVerifier.Supported(callbackType));
        Assert.True(FirVerifier.IsNullableFunction(callbackType));
        Assert.Equal("ptr", LlvmEmitter.Type(callbackType));
        Assert.Equal("ptr", LlvmEmitter.Type(callbackType.Arguments[0]));
        Assert.Contains(program.Functions.SelectMany(function => function.Slots),
            slot => FirVerifier.IsNullableFunction(slot.Type));
        Assert.Contains(program.Functions.SelectMany(function => function.Body!.Statements)
            .OfType<FirDeclare>(), declaration => declaration.Initializer is FirArrayLiteral
                { Type.Arguments: [{ Kind: OpenDot.Sol.Analysis.ResolvedTypeKind.Nullable }] });
    }

    [Fact(Skip = "Runs in the required native-backend test job.",
        SkipUnless = nameof(Enabled), Timeout = 60000)]
    public void NullableFunctionPointersRoundTripThroughAotArraysAndOrc()
    {
        var program = Compile();
        string backend = NativeCompiler.BackendRoot(null);
        const string expected = "42\n0\n42\n0\n42\n41\n41\n41\n0\n41\n0\n42\n42\n42\n0\n0\n42\n42\n0\n";
        var artifact = NativeCompiler.Compile(program, backendDirectory: backend,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(artifact.RuntimeDll);

        string directory = Path.Combine(Path.GetTempPath(), "sol-nullable-function-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string executable = Path.Combine(directory, "fixture.exe");
            File.WriteAllBytes(executable, artifact.Executable);
            File.WriteAllBytes(Path.Combine(directory, "SolRT.dll"), artifact.RuntimeDll!);
            var native = Run(executable);
            Assert.Equal(0, native.ExitCode);
            Assert.Equal(expected, NormalizeNewlines(native.StandardOutput));
            Assert.Empty(native.StandardError);

            using var output = new MemoryStream();
            using var error = new MemoryStream();
            int exitCode = OrcExecutor.Execute(program, [], backendDirectory: backend,
                cancellationToken: TestContext.Current.CancellationToken,
                standardOutput: output, standardError: error);
            Assert.Equal(0, exitCode);
            Assert.Equal(expected, Encoding.UTF8.GetString(output.ToArray()));
            Assert.Empty(error.ToArray());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static (int ExitCode, string StandardOutput, string StandardError) Run(string executable)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the nullable-function fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Nullable-function fixture exceeded 30 seconds.");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
