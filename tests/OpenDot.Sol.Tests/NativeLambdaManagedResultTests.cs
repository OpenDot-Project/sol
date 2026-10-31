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
public sealed class NativeLambdaManagedResultTests
{
    private const string Source = "import std.stdio\n"
        + "func collectNow(): () = Gc.collect()\n"
        + "object Main { func main(args: []String) throws IOException {\n"
        + "val retain: (String) -> String = { input ->\n"
        + "val alive = input + \"-survives\"\n"
        + "collectNow()\n"
        + "alive\n"
        + "}\n"
        + "println(retain(\"sol\"))\n"
        + "} }";

    public static bool Enabled => NativeRuntimeIntegrationTests.Enabled;

    private static FirCompilation Compile() => FirCompilation.Create(
        [AstTestSources.Parse(Source)], TestContext.Current.CancellationToken);

    [Fact]
    public void LambdaReturningManagedStringHasAClosedStringSignatureAcrossGcCall()
    {
        var compilation = Compile();
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));

        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program).Program;
        var lambda = Assert.Single(program.Functions, function => function.LambdaSource is not null);
        Assert.Equal("std.core.String", lambda.ReturnType.Name);
        Assert.Equal("std.core.String", Assert.Single(lambda.Parameters).Type.Name);
        var collect = Assert.Single(program.Functions, function => function.Name == "collectNow");
        var collection = Assert.IsType<FirIntrinsic>(Assert.IsType<FirReturn>(
            Assert.Single(collect.Body!.Statements)).Value);
        Assert.Equal(FirIntrinsicKind.GcCollect, collection.Operation);
        Assert.Contains(lambda.Body!.Statements.OfType<FirEvaluate>(), statement =>
            statement.Value is FirCall call && call.Target == collect.Id);
        Assert.IsType<FirRead>(Assert.IsType<FirReturn>(lambda.Body.Statements[^1]).Value);
    }

    [Fact(Skip = "Runs in the required native-backend test job.",
        SkipUnless = nameof(Enabled), Timeout = 60000)]
    public void ManagedLambdaResultRemainsValidAcrossGcCollectionInAotAndOrc()
    {
        var compilation = Compile();
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program);
        string backend = NativeCompiler.BackendRoot(null);
        const string expected = "sol-survives\n";

        var artifact = NativeCompiler.Compile(program, backendDirectory: backend,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(artifact.RuntimeDll);
        string directory = Path.Combine(Path.GetTempPath(), "sol-lambda-managed-result-"
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
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start managed-result lambda fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Managed-result lambda fixture exceeded 30 seconds.");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
