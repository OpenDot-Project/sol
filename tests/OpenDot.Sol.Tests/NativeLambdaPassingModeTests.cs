// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

[Collection("CompilerConsole")]
public sealed class NativeLambdaPassingModeTests
{
    private const string Source = "import std.stdio\n"
        + "object Main { func main(args: []String) throws IOException {\n"
        + "val initialize: (out int) -> () = { out target: int -> target = 41 }\n"
        + "var result: int\n"
        + "initialize(out result)\n"
        + "val increment: (ref int) -> () = { ref value: int -> value += 1 }\n"
        + "increment(ref result)\n"
        + "println(result)\n"
        + "} }";

    public static bool Enabled => NativeRuntimeIntegrationTests.Enabled;

    [Fact]
    public void NamedLambdaParametersPreserveExactRefAndOutFunctionAbi()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(Source)], TestContext.Current.CancellationToken);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + "[" + diagnostic.Span.Start + "]: " + diagnostic.Message)));

        var functions = compilation.Program.Program.Functions
            .Where(function => function.LambdaSource is not null).OrderBy(function => function.Origin.Span.Start)
            .ToArray();
        Assert.Equal(2, functions.Length);
        Assert.Equal(AstPassingMode.Out, Assert.Single(functions[0].Parameters).Mode);
        Assert.Equal(AstPassingMode.Ref, Assert.Single(functions[1].Parameters).Mode);
    }

    [Fact(Skip = "Runs in the required native-backend test job.",
        SkipUnless = nameof(Enabled), Timeout = 60000)]
    public void RefAndOutLambdaValuesExecuteThroughAotAndOrc()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(Source)], TestContext.Current.CancellationToken);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + "[" + diagnostic.Span.Start + "]: " + diagnostic.Message)));
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program);
        string backend = NativeCompiler.BackendRoot(null);
        const string expected = "42\n";

        var artifact = NativeCompiler.Compile(program, backendDirectory: backend,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(artifact.RuntimeDll);
        string directory = Path.Combine(Path.GetTempPath(), "sol-lambda-ref-out-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void OutLambdaParameterMustBeAssignedOnEveryNormalExit()
    {
        var source = AstTestSources.Parse("object Main { func main(args: []String) {\n"
            + "val initialize: (out int) -> () = { out value: int -> }\n"
            + "var result: int\n"
            + "initialize(out result)\n} }");
        var lambda = Assert.Single(source.Root.Descendants().OfType<AstLambda>());

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2242"
            && diagnostic.Span == lambda.Origin.Span);
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
            throw new TimeoutException("AOT ref/out lambda fixture exceeded 30 seconds.");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
