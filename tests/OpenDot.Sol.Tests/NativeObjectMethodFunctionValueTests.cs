// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

[Collection("CompilerConsole")]
public sealed class NativeObjectMethodFunctionValueTests
{
    public static bool Enabled => NativeRuntimeIntegrationTests.Enabled;

    private const string ObjectMethodSource = "object Arithmetic { "
        + "func increment(value: int): int = value + 1; "
        + "func <T> identity(value: T): T = value }\n"
        + "object Main { func main(args: []String) { "
        + "val callback: (int) -> int = Arithmetic.increment; "
        + "val identity: (String) -> String = Arithmetic.identity; "
        + "println(callback(41)); println(identity(\"Sol\")) } }";

    private static FirCompilation Compile(string source) => FirCompilation.Create(
        [AstTestSources.Parse(source)], TestContext.Current.CancellationToken);

    private static FirCompilation CompileFixture(string source)
    {
        var module = Regex.Match(source, "(?m)^module ([A-Za-z0-9_.]+)\\r?$");
        Assert.True(module.Success, "The semantic fixture must declare its module.");
        string declarations = source.Remove(module.Index, module.Length);
        return FirCompilation.Create([AstTestSources.Parse(declarations, module.Groups[1].Value,
            "FunctionValueObjectMethodStorage.sol")], TestContext.Current.CancellationToken);
    }

    [Fact]
    public void StatefulObjectMethodReferencesAreRejectedBeforeNativeLowering()
    {
        var compilation = Compile("object Stateful { val state = 1; "
            + "func read(value: int): int = value + state }\n"
            + "object Main { func main(args: []String) { "
            + "val callback: (int) -> int = Stateful.read } }");

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400"
            && diagnostic.Message.Contains("stateful receiver body", StringComparison.Ordinal));
    }

    [Fact]
    public void BoundClassMethodReferencesAreNotLoweredAsReceiverFreePointers()
    {
        var compilation = Compile("class Counter { func add(value: int): int = value + 1 }\n"
            + "object Main { func main(args: []String) { "
            + "val counter = Counter(); val callback: (int) -> int = counter.add } }");

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
    }

    [Fact(Skip = "Runs in the required native-backend test job.",
        SkipUnless = nameof(Enabled), Timeout = 60000)]
    public void StatelessObjectMethodValuesExecuteThroughAotAndOrcFromTheSharedFixture()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "spec", "examples", "semantics",
            "FunctionValueObjectMethodStorage.sol");
        var compilation = CompileFixture(File.ReadAllText(fixture));
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program);
        string backend = NativeCompiler.BackendRoot(null);
        string expected = "42\n42\nSol\n41\n";

        var artifact = NativeCompiler.Compile(program, backendDirectory: backend,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(artifact.RuntimeDll);
        string directory = Path.Combine(Path.GetTempPath(), "sol-object-callback-" + Guid.NewGuid().ToString("N"));
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
            int orcExitCode = OrcExecutor.Execute(program, [], backendDirectory: backend,
                cancellationToken: TestContext.Current.CancellationToken,
                standardOutput: output, standardError: error);
            Assert.Equal(0, orcExitCode);
            Assert.Equal(expected, Encoding.UTF8.GetString(output.ToArray()));
            Assert.Empty(error.ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start AOT fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("AOT object-method callback fixture exceeded 30 seconds.");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
