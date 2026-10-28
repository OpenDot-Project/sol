// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

[Collection("CompilerConsole")]
public sealed class NativeAnonymousFunctionAbiMatrixTests
{
    private const string MixedAbiSource = """
        import std.stdio

        func invoke(
            callback: (ref int, int, out String) -> String throws IOException,
            ref counter: int,
            amount: int,
            out stored: String
        ): String throws IOException {
            callback(ref counter, amount, out stored)
        }

        object Main {
            func main(args: []String) throws IOException {
                val callback: (ref int, int, out String) -> String throws IOException =
                    func(ref current: int, amount: int, out destination: String): String throws IOException {
                        current += amount
                        destination = "stored"
                        Gc.collect()
                        println("input")
                        "result"
                    }
                var counter = 40
                var stored: String
                val returned = invoke(callback, ref counter, 2, out stored)
                println(returned)
                println(counter)
                println(stored)
            }
        }
        """;

    public static bool Enabled => NativeRuntimeIntegrationTests.Enabled;

    [Fact(Skip = "Runs in the required native-backend test job.",
        SkipUnless = nameof(Enabled), Timeout = 60000)]
    public void MixedRefOutManagedResultAndCheckedEffectsMatchAcrossAotAndOrc()
    {
        var compilation = Compile(MixedAbiSource);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + "[" + diagnostic.Span.Start + "]: " + diagnostic.Message)));
        var verified = Assert.IsType<VerifiedFirProgram>(compilation.Program);
        var synthetic = Assert.Single(verified.Program.Functions,
            function => function.AnonymousFunctionSource is not null);
        Assert.Equal(new[] { AstPassingMode.Ref, AstPassingMode.Value, AstPassingMode.Out },
            synthetic.Parameters.Select(parameter => parameter.Mode));
        Assert.Equal("std.stdio.IOException", Assert.Single(synthetic.Effects).Name);

        const string expected = "input\nresult\n42\nstored\n";
        string backend = NativeCompiler.BackendRoot(null);
        var artifact = NativeCompiler.Compile(verified, backendDirectory: backend,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(artifact.RuntimeDll);

        string directory = Path.Combine(Path.GetTempPath(),
            "sol-anonymous-abi-matrix-" + Guid.NewGuid().ToString("N"));
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
            int orcExitCode = OrcExecutor.Execute(verified, [], backendDirectory: backend,
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
    public void AnonymousFunctionDefaultParametersAreRejectedBySourceSemantics()
    {
        string source = "object Main { func main(args: []String) {\n"
            + "val callback: (int) -> int = func(value: int = 40): int { value }\n"
            + "val result = callback(2)\n} }";
        var compilation = Compile(source);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2226");
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2402");
    }

    private static FirCompilation Compile(string source)
    {
        var syntax = SyntaxTree.Parse(SourceText.FromString("module test\n" + source, "anonymous-abi.sol"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(syntax.HasErrors, string.Join("\n", syntax.Diagnostics.Select(diagnostic =>
            diagnostic.Code + "@" + diagnostic.Span.Start + ": " + diagnostic.Message + " in `"
            + syntax.Source.GetText(diagnostic.Span) + "`")));
        var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        AstVerifier.Verify(ast, TestContext.Current.CancellationToken);
        return FirCompilation.Create([ast], TestContext.Current.CancellationToken);
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
            ?? throw new InvalidOperationException("Could not start the native ABI-matrix fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Native ABI-matrix fixture exceeded 30 seconds.");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
