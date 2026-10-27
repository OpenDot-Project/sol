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
public sealed class NativeLambdaFunctionValueTests
{
    private const string LambdaSource = "import std.stdio\n"
        + "func apply(value: int, transform: (int) -> int): int = transform(value)\n"
        + "func increment(value: int): int = value + 1\n"
        + "object Main { func main(args: []String) throws IOException {\n"
        + "val named: (int) -> int = { value -> value + 1 }\n"
        + "val positional: (int) -> int = { $0 + 1 }\n"
        + "val sparse: (int, int, int) -> int = { $0 + $2 }\n"
        + "val constant: () -> int = { -> 7 }\n"
        + "val forwarded: (int) -> int = { value -> increment(value) }\n"
        + "val inferred = { value: int -> value + 1 }\n"
        + "val action = { println(\"from lambda\") }\n"
        + "val callbacks: []((int) -> int) = [{ value -> value + 1 }, { $0 + 2 }]\n"
        + "println(named(41)); println(positional(41)); println(sparse(20, 999, 22)); println(forwarded(41));\n"
        + "println(inferred(41));\n"
        + "println(constant()); action(); println(apply(41) { value -> value + 1 });\n"
        + "println(callbacks[0](41)); println(callbacks[1](41))\n"
        + "} }";

    public static bool Enabled => NativeRuntimeIntegrationTests.Enabled;

    private static FirCompilation Compile(string source) => FirCompilation.Create(
        [AstTestSources.Parse(source)], TestContext.Current.CancellationToken);

    [Fact]
    public void NoncapturingLambdaValuesRetainTheirSourceIdentityAndSignature()
    {
        var compilation = Compile(LambdaSource);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));

        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program).Program;
        var lambdas = program.Functions.Where(function => function.LambdaSource is not null).ToArray();
        Assert.Equal(10, lambdas.Length);
        Assert.All(lambdas, function =>
        {
            Assert.Equal("<lambda>", function.Name);
            Assert.NotNull(function.Body);
            Assert.Empty(function.SpecializationArguments);
            var lambda = Assert.IsType<AstLambda>(function.LambdaSource);
            var signature = Assert.IsType<ResolvedType>(program.Analysis.GetExpression(lambda)?.Type);
            Assert.Equal(ResolvedTypeKind.Function, signature.Kind);
            Assert.Equal(signature.ParameterModes.Length, function.Parameters.Length);
            Assert.Equal(signature.Arguments[^1], function.ReturnType);
            Assert.Equal(signature.Effects.ToArray(), function.Effects.ToArray());
            for (int index = 0; index < function.Parameters.Length; index++)
            {
                Assert.Equal(signature.Arguments[index], function.Parameters[index].Type);
                Assert.Equal(signature.ParameterModes[index], function.Parameters[index].Mode);
            }
        });

        var sourceLambdas = program.Analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstLambda>().ToArray();
        Assert.Equal(lambdas.Length, sourceLambdas.Length);
        Assert.All(lambdas, function => Assert.Contains(sourceLambdas,
            source => ReferenceEquals(source, function.LambdaSource)));
        Assert.Equal(Enumerable.Range(0, program.Functions.Length), program.Functions.Select(function => function.Id));
    }

    [Fact(Skip = "Runs in the required native-backend test job.",
        SkipUnless = nameof(Enabled), Timeout = 60000)]
    public void NoncapturingLambdaValuesExecuteThroughAotAndOrc()
    {
        var compilation = Compile(LambdaSource);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program);
        string backend = NativeCompiler.BackendRoot(null);
        const string expected = "42\n42\n42\n42\n42\n7\nfrom lambda\n42\n42\n43\n";

        var artifact = NativeCompiler.Compile(program, backendDirectory: backend,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(artifact.RuntimeDll);
        string directory = Path.Combine(Path.GetTempPath(), "sol-lambda-value-" + Guid.NewGuid().ToString("N"));
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
    public void CapturingLambdaValuesRemainOutsideTheNativeFunctionPointerAbi()
    {
        var compilation = Compile("object Main { func main(args: []String) {\n"
            + "val seed = 41\n"
            + "val callback: (int) -> int = { value -> value + seed }\n"
            + "} }");

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
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
            throw new TimeoutException("AOT lambda-value fixture exceeded 30 seconds.");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
