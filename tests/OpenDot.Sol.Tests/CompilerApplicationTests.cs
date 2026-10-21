// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.IO;
using System.Text.Json;
using OpenDot.Sol.Cli;
using Xunit;

namespace OpenDot.Sol.Tests;

[CollectionDefinition("CompilerConsole", DisableParallelization = true)]
public sealed class CompilerConsoleCollection;

[Collection("CompilerConsole")]
public sealed class CompilerApplicationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sol-app-" + Guid.NewGuid().ToString("N"));

    public CompilerApplicationTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void CstEmissionIsLosslessAndLogicalModuleNamesIgnorePaths()
    {
        string source = Write("different.sol", "module chosen.name\nfunc add(a, b: int) = a + b\n");
        string output = Path.Combine(_directory, "output.json");
        var result = Run(source, "--emit=cst", "-o", output);
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Stderr);
        using var json = JsonDocument.Parse(File.ReadAllBytes(output));
        Assert.Equal("chosen.name", json.RootElement.GetProperty("module").GetString());
        Assert.Equal("CompilationUnit", json.RootElement.GetProperty("root").GetProperty("kind").GetString());
    }

    [Fact]
    public void InvalidSourceDoesNotOverwriteExistingOutputAndJsonDiagnosticsAreStructured()
    {
        string source = Write("invalid.sol", "val missingModule = 1\n");
        string output = Write("output.json", "existing artifact");
        var result = Run(source, "--emit=cst", "--error-format=json", "-o", output);
        Assert.Equal(1, result.Exit);
        Assert.Equal("existing artifact", File.ReadAllText(output));
        using var json = JsonDocument.Parse(result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        Assert.Equal("SOL1100", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("line").GetInt32());
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public void SameModulePartsAreAcceptedButOutputsMayNotCollide()
    {
        string first = Write("a.sol", "module shared\nfunc one() = 1\n");
        string second = Write("b.sol", "module shared\nfunc two() = 2\n");
        var result = Run(first, second, "--emit=cst", "--out-dir", Path.Combine(_directory, "out"));
        Assert.Equal(0, result.Exit);
        Assert.True(File.Exists(Path.Combine(_directory, "out/a.cst.json")));
        Assert.True(File.Exists(Path.Combine(_directory, "out/b.cst.json")));
    }

    [Fact]
    public void SourceAndArtifactCollisionFailsBeforeWriting()
    {
        const string text = "module app\n";
        string source = Write("main.sol", text);
        Assert.Equal(2, Run(source, "--emit=cst", "-o", source).Exit);
        Assert.Equal(text, File.ReadAllText(source));
        Assert.Equal(2, Run(source, source, "--emit=cst").Exit);
    }

    [Fact]
    public void OutputFailureRollsBackPreviouslyCommittedFiles()
    {
        string first = Write("first.sol", "module app\n");
        string second = Write("second.sol", "module app\n");
        string output = Path.Combine(_directory, "out");
        Directory.CreateDirectory(output);
        string previous = Path.Combine(output, "first.cst.json");
        File.WriteAllText(previous, "previous artifact");
        Directory.CreateDirectory(Path.Combine(output, "second.cst.json"));
        Assert.Equal(1, Run(first, second, "--emit=cst", "--out-dir", output).Exit);
        Assert.Equal("previous artifact", File.ReadAllText(previous));
        Assert.Empty(Directory.GetFiles(output, ".solc-*.tmp*"));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    public void InformationalCommandsSucceedWithoutLoadingLlvm(string option)
    {
        var result = Run(option);
        Assert.Equal(0, result.Exit);
        Assert.Contains("0.0.1", result.Stdout);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public void OrcExecutionRejectsAnExecutableOutputPath()
    {
        string source = Write("main.sol", "module app\n");
        string output = Path.Combine(_directory, "main.exe");
        var result = Run(source, "-o", output, "-Cllvm-compiler=orc");
        Assert.Equal(2, result.Exit);
        Assert.Contains("no output path", result.Stderr);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ExplainAndPrintAreRootOptions()
    {
        Assert.Equal("2026", Run("--print=edition-list").Stdout.Trim());
        Assert.Contains("UTF-8", Run("--explain", "SOL1001").Stdout);
        Assert.Equal(2, Run("--explain", "DOES_NOT_EXIST").Exit);
        Assert.Equal(2, Run("run").Exit);
        Assert.Equal(2, Run("--print", "not-a-key").Exit);
    }

    [Fact]
    public void ExplicitColorIsAppliedOnlyToHumanDiagnostics()
    {
        string source = Write("main.sol", "module app\n");
        Assert.Contains("\u001b[31m", Run(source, "--color=always").Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", Run(source, "--color=never").Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", Run(source, "--color=always", "--error-format=json").Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AstEmissionIncludesNormalizedParametersAndTypeShapes()
    {
        string source = Write("ast.sol", "module chosen.name\nfunc f(a,b:[]String?):int = 1");
        string output = Path.Combine(_directory, "ast.json");
        var result = Run(source, "--emit=ast", "-o", output);
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Stderr);
        using var json = JsonDocument.Parse(File.ReadAllBytes(output));
        Assert.Equal("chosen.name", json.RootElement.GetProperty("module").GetString());
        var nodes = json.RootElement.GetProperty("nodes").EnumerateArray();
        bool functionFound = false;
        foreach (var node in nodes)
        {
            if (node.GetProperty("kind").GetString() != "FunctionDeclaration") { continue; }
            var parameters = node.GetProperty("parameters");
            Assert.Equal(2, parameters.GetArrayLength());
            Assert.Equal("[](String)?", parameters[0].GetProperty("type").GetString());
            Assert.Equal("a", parameters[0].GetProperty("name").GetString());
            Assert.Equal("b", parameters[1].GetProperty("name").GetString());
            functionFound = true;
        }
        Assert.True(functionFound);
    }

    [Fact]
    public void AstEmissionUsesItsOwnDefaultExtension()
    {
        string source = Write("model.sol", "module app\nclass C {}");
        string output = Path.Combine(_directory, "out");
        Assert.Equal(0, Run(source, "--emit=ast", "--out-dir", output).Exit);
        Assert.True(File.Exists(Path.Combine(output, "model.ast.json")));
        Assert.False(File.Exists(Path.Combine(output, "model.cst.json")));
    }

    [Fact]
    public void AstEmissionDoesNotImplyDeclarationOrTypeChecking()
    {
        string source = Write("model.sol", "module app\nfunc f(a:Unknown) {}\nfunc f(b:Unknown) {}");
        string output = Path.Combine(_directory, "model.ast.json");
        Assert.Equal(0, Run(source, "--emit=ast", "-o", output).Exit);
        Assert.True(File.Exists(output));
        Assert.Equal(1, Run(source).Exit);
    }

    [Fact]
    public void AstOutputFailureRollsBackAllPreviouslyReplacedOutputs()
    {
        string first = Write("first.sol", "module app\nval one = 1");
        string second = Write("second.sol", "module app\nval two = 2");
        string output = Path.Combine(_directory, "out");
        Directory.CreateDirectory(output);
        string previous = Path.Combine(output, "first.ast.json");
        File.WriteAllText(previous, "previous AST");
        Directory.CreateDirectory(Path.Combine(output, "second.ast.json"));
        Assert.Equal(1, Run(first, second, "--emit=ast", "--out-dir", output).Exit);
        Assert.Equal("previous AST", File.ReadAllText(previous));
        Assert.Empty(Directory.GetFiles(output, ".solc-*.tmp*"));
    }

    [Fact]
    public void AstParseFailureDoesNotReplaceAnyExistingArtifact()
    {
        string first = Write("first.sol", "module app\nclass C {}");
        string invalid = Write("invalid.sol", "func missingModule() {}");
        string output = Path.Combine(_directory, "out");
        Directory.CreateDirectory(output);
        string previous = Path.Combine(output, "first.ast.json");
        File.WriteAllText(previous, "previous AST");
        Assert.Equal(1, Run(first, invalid, "--emit=ast", "--out-dir", output).Exit);
        Assert.Equal("previous AST", File.ReadAllText(previous));
        Assert.False(File.Exists(Path.Combine(output, "invalid.ast.json")));
    }

    [Fact]
    public void AstOutputCannotOverwriteSourceOrCollideBetweenInputs()
    {
        const string text = "module app\nclass C {}";
        string source = Write("model.sol", text);
        Assert.Equal(2, Run(source, "--emit=ast", "-o", source).Exit);
        Assert.Equal(text, File.ReadAllText(source));
        string subdirectory = Path.Combine(_directory, "sub");
        Directory.CreateDirectory(subdirectory);
        string second = Path.Combine(subdirectory, "model.sol");
        File.WriteAllText(second, "module other\nclass D {}");
        string output = Path.Combine(_directory, "out");
        Assert.Equal(2, Run(source, second, "--emit=ast", "--out-dir", output).Exit);
        Assert.False(Directory.Exists(output));
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static (int Exit, string Stdout, string Stderr) Run(params string[] args)
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try { return (Program.Main(args), stdout.ToString(), stderr.ToString()); }
        finally { Console.SetOut(previousOut); Console.SetError(previousError); }
    }
}
