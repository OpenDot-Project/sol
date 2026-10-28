// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

[Collection("CompilerConsole")]
public sealed class NativeAnonymousFunctionTests
{
    private const string Source = "import std.stdio\n"
        + "func <T> identity(value: T): T = value\n"
        + "object Main { func main(args: []String) throws IOException {\n"
        + "val blockBody = func(value: int): int { value + 1 }\n"
        + "val expressionBody = func(value: int): int = value + 2\n"
        + "val inferredBody: (int) -> int = func(value: int) { value + 3 }\n"
        + "val genericTarget = func(value: int): int { identity(value) }\n"
        + "val nestedBody = func(value: int): int { val increment = func(input: int): int = input + 1; increment(value) }\n"
        + "val lambdaHost: (int) -> int = { value -> val increment = func(input: int): int = input + 1; increment(value) }\n"
        + "val anonymousHost = func(value: int): int { val increment: (int) -> int = { item -> item + 1 }; increment(value) }\n"
        + "val collectVararg: ([]int) -> int = func(vararg values): int { values[0] }\n"
        + "val write = func(out value: int): () { value = 42 }\n"
        + "val callbackArray: []((int) -> int) = [func(value: int): int { value + 4 }, "
        + "func(value: int): int = value * 2]\n"
        + "val immediate = (func(value: int): int { value + 42 })(0)\n"
        + "var output: int; write(out output)\n"
        + "println(blockBody(41)); println(expressionBody(40)); "
        + "println(inferredBody(39)); println(genericTarget(42)); println(nestedBody(41)); "
        + "println(lambdaHost(41)); println(anonymousHost(41)); println(collectVararg([42])); println(output); "
        + "println(callbackArray[0](38)); println(callbackArray[1](21)); println(immediate)\n"
        + "} }";

    public static bool Enabled => NativeRuntimeIntegrationTests.Enabled;

    private static FirCompilation Compile() => FirCompilation.Create([AstTestSources.Parse(Source)],
        TestContext.Current.CancellationToken);

    [Fact]
    public void NoncapturingAnonymousFunctionsKeepVerifiedSourceAndParameterIdentity()
    {
        var compilation = Compile();
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program).Program;
        var functions = program.Functions.Where(function => function.AnonymousFunctionSource is not null).ToArray();

        Assert.Equal(13, functions.Length);
        Assert.All(functions, function =>
        {
            Assert.Equal("<anonymous-function>", function.Name);
            Assert.NotNull(function.Body);
            Assert.Empty(function.SpecializationArguments);
            Assert.Equal(function.AnonymousFunctionSource!.Parameters.Length, function.Parameters.Length);
            Assert.Equal(program.Analysis.GetExpression(function.AnonymousFunctionSource)?.Type?.Arguments[^1],
                function.ReturnType);
        });
        Assert.Equal(Enumerable.Range(0, program.Functions.Length), program.Functions.Select(function => function.Id));
    }

    [Fact(Skip = "Runs in the required native-backend test job.",
        SkipUnless = nameof(Enabled), Timeout = 60000)]
    public void NoncapturingAnonymousFunctionsExecuteThroughAotAndOrc()
    {
        var compilation = Compile();
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program);
        string backend = NativeCompiler.BackendRoot(null);
        const string expected = "42\n42\n42\n42\n42\n42\n42\n42\n42\n42\n42\n42\n";

        var artifact = NativeCompiler.Compile(program, backendDirectory: backend,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(artifact.RuntimeDll);
        string directory = Path.Combine(Path.GetTempPath(), "sol-anonymous-function-" + Guid.NewGuid().ToString("N"));
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
            ?? throw new InvalidOperationException("Could not start the native anonymous-function fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Native anonymous-function fixture exceeded 30 seconds.");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
