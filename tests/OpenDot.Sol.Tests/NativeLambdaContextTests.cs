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
public sealed class NativeLambdaContextTests
{
    private const string NestedFactorySource = "import std.stdio\n"
        + "object Main { func main(args: []String) throws IOException {\n"
        + "val nested: () -> (() -> int) = { -> { -> 40 + 2 } }\n"
        + "val inner = nested()\n"
        + "val supply: () -> [](() -> int) = { -> [{ -> 7 }] }\n"
        + "val fromArray = supply()\n"
        + "println(inner()); println(fromArray[0]())\n"
        + "} }";

    private const string GenericTrailingSource = "import std.stdio\n"
        + "func <T> transform(value: T, action: (T) -> T): T = action(value)\n"
        + "func <T> delayed(value: T, action: () -> T): T = action()\n"
        + "object Main { func main(args: []String) throws IOException {\n"
        + "val trailing = transform(41) { value -> value + 1 }\n"
        + "val explicit = transform<int>(41) { value -> value + 1 }\n"
        + "val named = transform(action = { value: int -> value + 1 }, value = 41)\n"
        + "val parenthesized = transform(41, { value -> value + 1 })\n"
        + "val explicitString = transform<std.core.String>(\"sol\") "
        + "{ value: std.core.String -> value }\n"
        + "val zero = delayed(\"later\") { \"later\" }\n"
        + "println(trailing); println(explicit); println(named); println(parenthesized); "
        + "println(explicitString); println(zero)\n"
        + "} }";

    private const string GenericLambdaBodySource = "import std.stdio\n"
        + "func <T> identity(value: T): T = value\n"
        + "object Main { func main(args: []String) throws IOException {\n"
        + "val apply: (int) -> int = { value -> identity(value) }\n"
        + "val provider: () -> ((int) -> int) = { -> identity }\n"
        + "println(apply(42))\n"
        + "val identityFn = provider()\n"
        + "println(identityFn(41))\n"
        + "} }";

    public static bool Enabled => NativeRuntimeIntegrationTests.Enabled;

    private static FirCompilation Compile(string source) => FirCompilation.Create(
        [AstTestSources.Parse(source)], TestContext.Current.CancellationToken);

    [Fact]
    public void NestedLambdaFunctionValuesHaveOneSyntheticFunctionPerSourceNode()
    {
        var compilation = Compile(NestedFactorySource);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + "[" + diagnostic.Span.Start + ","
                + diagnostic.Span.End + "]: " + diagnostic.Message)));

        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program).Program;
        var sourceLambdas = program.Analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstLambda>().ToArray();
        var functions = program.Functions.Where(function => function.LambdaSource is not null).ToArray();

        Assert.Equal(4, sourceLambdas.Length);
        Assert.Equal(sourceLambdas.Length, functions.Length);
        Assert.All(sourceLambdas, lambda => Assert.Single(functions,
            function => ReferenceEquals(function.LambdaSource, lambda)));
        Assert.Contains(functions, function => function.ReturnType.Kind == ResolvedTypeKind.Function);
        Assert.All(functions, function =>
            Assert.Same(function.LambdaSource!.Origin.Element, function.Origin.Element));
    }

    [Fact(Skip = "Runs in the required native-backend test job.",
        SkipUnless = nameof(Enabled), Timeout = 60000)]
    public void NestedAndReturnedLambdaValuesExecuteThroughAotAndOrc()
    {
        var compilation = Compile(NestedFactorySource);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + "[" + diagnostic.Span.Start + ","
                + diagnostic.Span.End + "]: " + diagnostic.Message)));
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program);
        string backend = NativeCompiler.BackendRoot(null);
        const string expected = "42\n7\n";

        var artifact = NativeCompiler.Compile(program, backendDirectory: backend,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(artifact.RuntimeDll);
        string directory = Path.Combine(Path.GetTempPath(), "sol-nested-lambda-" + Guid.NewGuid().ToString("N"));
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
    public void NestedLambdaCapturingAnOuterLambdaParameterIsRejectedAtTheInnerSource()
    {
        var source = AstTestSources.Parse("object Main { func main(args: []String) {\n"
            + "val factory: (int) -> (() -> int) = { seed -> { -> seed } }\n"
            + "val callback = factory(42)\n} }");
        var nested = source.Root.Descendants().OfType<AstLambda>().ToArray();
        Assert.Equal(2, nested.Length);

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400"
            && diagnostic.Span == nested[1].Origin.Span);
    }

    [Fact]
    public void NoncapturingLambdaCanSupplyAClosedGenericCallbackArgument()
    {
        var compilation = Compile(GenericTrailingSource);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + "[" + diagnostic.Span.Start
                + "]: " + diagnostic.Message)));

        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program).Program;
        var lambdas = program.Functions.Where(function => function.LambdaSource is not null).ToArray();
        var instances = program.Functions.Where(function => function.Name == "transform"
            && !function.SpecializationArguments.IsEmpty).ToArray();
        var delayed = Assert.Single(program.Functions.Where(function => function.Name == "delayed"
            && !function.SpecializationArguments.IsEmpty));
        Assert.Equal(2, instances.Length);
        Assert.Contains(instances, instance => Assert.Single(instance.SpecializationArguments).Name == "int");
        Assert.Contains(instances, instance => Assert.Single(instance.SpecializationArguments).Name == "std.core.String");
        Assert.Equal("std.core.String", Assert.Single(delayed.SpecializationArguments).Name);
        Assert.Equal(6, lambdas.Length);
        Assert.All(lambdas.Where(lambda => !lambda.Parameters.IsEmpty), lambda =>
        {
            Assert.Contains(lambda.Parameters[0].Type.Name, new[] { "int", "std.core.String" });
        });
        Assert.Contains(lambdas, lambda => lambda.Parameters.IsEmpty
            && lambda.ReturnType.Name == "std.core.String");
        var main = Assert.Single(program.Functions, function => function.Name == "main");
        var calls = main.Body!.Statements.OfType<FirDeclare>()
            .Select(declaration => declaration.Initializer).OfType<FirCall>().ToArray();
        Assert.Equal(6, calls.Length);
        Assert.All(calls, call =>
        {
            Assert.Contains(call.Target, instances.Select(instance => instance.Id).Append(delayed.Id));
            var callback = Assert.IsType<FirFunctionReference>(Assert.Single(call.Arguments,
                argument => argument.Parameter == 1).Value);
            Assert.Contains(lambdas, lambda => lambda.Id == callback.Target);
        });
    }

    [Fact]
    public void NoncapturingLambdaMayCallAndReferenceAClosedGenericFunction()
    {
        var compilation = Compile(GenericLambdaBodySource);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + "[" + diagnostic.Span.Start
                + "]: " + diagnostic.Message)));

        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program).Program;
        var lambdas = program.Functions.Where(function => function.LambdaSource is not null).ToArray();
        Assert.Equal(2, lambdas.Length);
        var identity = Assert.Single(program.Functions, function => function.Name == "identity"
            && !function.SpecializationArguments.IsEmpty);
        Assert.Equal("int", Assert.Single(identity.SpecializationArguments).Name);
        var directCall = Assert.IsType<FirCall>(Assert.IsType<FirReturn>(
            Assert.Single(lambdas.Single(function => function.ReturnType.Name == "int").Body!.Statements)).Value);
        Assert.Equal(identity.Id, directCall.Target);
        Assert.Equal(identity.SpecializationArguments, directCall.TypeArguments);
        var reference = Assert.IsType<FirFunctionReference>(Assert.IsType<FirReturn>(
            Assert.Single(lambdas.Single(function => function.ReturnType.Kind == ResolvedTypeKind.Function)
                .Body!.Statements)).Value);
        Assert.Equal(identity.Id, reference.Target);
    }

    [Fact(Skip = "Runs in the required native-backend test job.",
        SkipUnless = nameof(Enabled), Timeout = 60000)]
    public void GenericCallsInsideLambdasExecuteThroughAotAndOrc()
    {
        var compilation = Compile(GenericLambdaBodySource);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + "[" + diagnostic.Span.Start
                + "]: " + diagnostic.Message)));
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program);
        string backend = NativeCompiler.BackendRoot(null);
        const string expected = "42\n41\n";

        var artifact = NativeCompiler.Compile(program, backendDirectory: backend,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(artifact.RuntimeDll);
        string directory = Path.Combine(Path.GetTempPath(), "sol-generic-lambda-body-" + Guid.NewGuid().ToString("N"));
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

    [Fact(Skip = "Runs in the required native-backend test job.",
        SkipUnless = nameof(Enabled), Timeout = 60000)]
    public void GenericCallbackLambdaExecutesThroughAotAndOrc()
    {
        var compilation = Compile(GenericTrailingSource);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + "[" + diagnostic.Span.Start
                + "]: " + diagnostic.Message)));
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program);
        string backend = NativeCompiler.BackendRoot(null);
        const string expected = "42\n42\n42\n42\nsol\nlater\n";

        var artifact = NativeCompiler.Compile(program, backendDirectory: backend,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(artifact.RuntimeDll);
        string directory = Path.Combine(Path.GetTempPath(), "sol-generic-lambda-" + Guid.NewGuid().ToString("N"));
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
            throw new TimeoutException("AOT nested-lambda fixture exceeded 30 seconds.");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
