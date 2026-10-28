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
public sealed class NativeNestedNullableFunctionArrayTests
{
    private const string Source = "import std.stdio\n"
        + "func mainBody(): int throws IOException {\n"
        + "val matrix: [][]((() -> int)?) = [[null, func() { 11 }], [func() { 29 }, null]]\n"
        + "Gc.collect()\n"
        + "val rowZero = matrix[0]\n"
        + "Gc.collect()\n"
        + "val absent = rowZero[0]\n"
        + "if absent == null { println(0) }\n"
        + "val present = rowZero[1]\n"
        + "var total = 0\n"
        + "if present != null { val value = present(); println(value); total += value }\n"
        + "val rowOne = matrix[1]\n"
        + "Gc.collect()\n"
        + "val other = rowOne[0]\n"
        + "if other != null { val value = other(); println(value); total += value }\n"
        + "val finalAbsent = rowOne[1]\n"
        + "if finalAbsent == null { println(1) }\n"
        + "total\n"
        + "}\n"
        + "object Main {\n"
        + "func main(args: []String) throws IOException { println(mainBody()) }\n"
        + "}";

    public static bool Enabled => NativeRuntimeIntegrationTests.Enabled;

    [Fact(Skip = "Runs in the required native-backend test job.",
        SkipUnless = nameof(Enabled), Timeout = 60000)]
    public void NestedNullableFunctionLeavesSurviveAotAndOrcCollections()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(Source)],
            TestContext.Current.CancellationToken);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Span + ": " + diagnostic.Message)));
        var verified = Assert.IsType<VerifiedFirProgram>(compilation.Program);
        string backend = NativeCompiler.BackendRoot(null);
        var artifact = NativeCompiler.Compile(verified, backendDirectory: backend,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(artifact.RuntimeDll);

        const string expected = "0\n11\n29\n1\n40\n";
        string directory = Path.Combine(Path.GetTempPath(), "sol-nested-nullable-function-"
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
            int exitCode = OrcExecutor.Execute(verified, [], backendDirectory: backend,
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
            ?? throw new InvalidOperationException("Could not start the nested nullable-function fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Nested nullable-function fixture exceeded 30 seconds.");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
