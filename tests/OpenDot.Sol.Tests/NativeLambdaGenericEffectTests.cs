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
public sealed class NativeLambdaGenericEffectTests
{
    private const string Source = "import std.stdio\n"
        + "func <T> identityWithIo(value: T): T throws IOException = value\n"
        + "object Main { func main(args: []String) throws IOException {\n"
        + "val transform: (int) -> int throws IOException = { value -> identityWithIo(value) }\n"
        + "val result = transform(41)\n"
        + "println(result)\n"
        + "} }";

    public static bool Enabled => NativeRuntimeIntegrationTests.Enabled;

    [Fact]
    public void ClosedGenericCallPreservesEffectsOnLambdaAndIndirectCall()
    {
        var compilation = Compile(Source);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + "[" + diagnostic.Span.Start
                + "]: " + diagnostic.Message)));

        var verified = Assert.IsType<VerifiedFirProgram>(compilation.Program);
        var program = verified.Program;
        var lambda = Assert.Single(program.Functions.Where(function => function.LambdaSource is not null));
        var effect = Assert.Single(lambda.Effects);
        Assert.Equal("std.stdio.IOException", effect.Name);

        var genericTarget = Assert.Single(program.Functions, function => function.Name == "identityWithIo"
            && !function.SpecializationArguments.IsEmpty);
        Assert.Equal("int", Assert.Single(genericTarget.SpecializationArguments).Name);
        Assert.Equal(lambda.Effects.Select(item => item.Name), genericTarget.Effects.Select(item => item.Name));

        var lambdaCall = Assert.IsType<FirCall>(Assert.IsType<FirReturn>(
            Assert.Single(lambda.Body!.Statements)).Value);
        Assert.Equal(genericTarget.Id, lambdaCall.Target);
        Assert.Equal(genericTarget.SpecializationArguments, lambdaCall.TypeArguments);

        var lambdaFlow = verified.FlowGraphs.Single(graph => graph.FunctionId == lambda.Id);
        var directCall = Assert.Single(lambdaFlow.Blocks.SelectMany(block => block.Calls));
        Assert.Equal(genericTarget.Id, directCall.Target);
        Assert.Contains(directCall.Effects, item => item.Name == "std.stdio.IOException");

        var mainFlow = verified.FlowGraphs.Single(graph => graph.FunctionId == program.Entry);
        var indirectCall = Assert.Single(mainFlow.Blocks.SelectMany(block => block.Calls),
            call => call.Target is null);
        Assert.Contains(indirectCall.Effects, item => item.Name == "std.stdio.IOException");
    }

    [Fact(Skip = "Runs in the required native-backend test job.",
        SkipUnless = nameof(Enabled), Timeout = 60000)]
    public void GenericCheckedEffectLambdaExecutesThroughAotAndOrc()
    {
        var compilation = Compile(Source);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + "[" + diagnostic.Span.Start
                + "]: " + diagnostic.Message)));
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program);
        string backend = NativeCompiler.BackendRoot(null);
        const string expected = "41\n";

        var artifact = NativeCompiler.Compile(program, backendDirectory: backend,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(artifact.RuntimeDll);
        string directory = Path.Combine(Path.GetTempPath(), "sol-generic-lambda-effects-"
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

    private static FirCompilation Compile(string source) => FirCompilation.Create(
        [AstTestSources.Parse(source)], TestContext.Current.CancellationToken);

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
            throw new TimeoutException("AOT generic-effect lambda fixture exceeded 30 seconds.");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
