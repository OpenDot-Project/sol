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
public sealed class NativeReturnedNullableCallbackArrayTests
{
    private const string Source = "import std.stdio\n"
        + "func makeGrid(): [][]((() -> int)?) {\n"
        + "val first: []((() -> int)?) = [null, func() { 6 }]\n"
        + "val second: []((() -> int)?) = [func() { 35 }, null]\n"
        + "[first, second]\n}"
        + "func consumeGrid(rows: [][]((() -> int)?)): int {\n"
        + "Gc.collect()\n"
        + "var total = 0\n"
        + "val first = rows[0]\n"
        + "val missing = first[0]\n"
        + "if missing == null { total += 2 }\n"
        + "val firstCallback = first[1]\n"
        + "if firstCallback != null { total += firstCallback() }\n"
        + "Gc.collect()\n"
        + "val second = rows[1]\n"
        + "val secondCallback = second[0]\n"
        + "if secondCallback != null { total += secondCallback() }\n"
        + "val missingAgain = second[1]\n"
        + "if missingAgain == null { total += 3 }\n"
        + "total\n}"
        + "object Main {\n"
        + "func main(args: []String) throws IOException {\n"
        + "val grid = makeGrid()\n"
        + "Gc.collect()\n"
        + "println(consumeGrid(grid))\n"
        + "Gc.collect()\n"
        + "}\n}"
        ;

    public static bool Enabled => NativeRuntimeIntegrationTests.Enabled;

    [Fact(Skip = "Runs in the required native-backend test job.",
        SkipUnless = nameof(Enabled), Timeout = 60000)]
    public void ReturnedNullableCallbackArraysStayLiveAcrossAotAndGuestOrcCollections()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(Source)],
            TestContext.Current.CancellationToken);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Span + ": " + diagnostic.Message)));
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program);
        string backend = NativeCompiler.BackendRoot(null);
        var artifact = NativeCompiler.Compile(program, backendDirectory: backend,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(artifact.RuntimeDll);

        const string expected = "46\n";
        string directory = Path.Combine(Path.GetTempPath(), "sol-returned-nullable-callbacks-"
            + Guid.NewGuid().ToString("N"));
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
            ?? throw new InvalidOperationException("Could not start the returned-callback-array fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Returned-callback-array fixture exceeded 30 seconds.");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
