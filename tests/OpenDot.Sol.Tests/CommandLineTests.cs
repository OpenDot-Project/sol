// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.IO;
using System.Linq;
using OpenDot.Sol.Cli;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void RootCompilerOptionsAreTyped()
    {
        var options = CompilerOptions.Parse(["input.sol", "--emit=cst", "--artifact-type", "solib",
            "-C", "llvm-compiler=orc", "--edition=2026", "--error-limit=5", "--error-format=json"]);
        Assert.Equal(CompilerCommand.Compile, options.Command);
        Assert.Equal(EmitKind.Cst, options.Emit);
        Assert.Equal(CompilerBackend.Orc, options.Backend);
        Assert.Equal(ArtifactType.Solib, options.Artifact);
        Assert.True(options.JsonErrors);
        Assert.Equal(5, options.ErrorLimit);
        Assert.Equal("input.sol", Assert.Single(options.Sources));
    }

    [Theory]
    [InlineData("--help", CompilerCommand.Help)]
    [InlineData("-h", CompilerCommand.Help)]
    [InlineData("--version", CompilerCommand.Version)]
    [InlineData("-V", CompilerCommand.Version)]
    public void InformationalCommandsDoNotNeedSources(string argument, CompilerCommand command)
        => Assert.Equal(command, CompilerOptions.Parse([argument]).Command);

    [Theory]
    [InlineData("--entry=Main")]
    [InlineData("run")]
    [InlineData("explain")]
    [InlineData("check")]
    [InlineData("--artifact-type=lib")]
    [InlineData("--edition=2025")]
    [InlineData("--target=x86_64-unknown-linux-gnu")]
    [InlineData("--error-limit=0")]
    [InlineData("--error-format=ndjson")]
    [InlineData("--help=true")]
    public void ObsoleteOrUnknownOptionsAreRejected(string argument)
        => Assert.Throws<CommandLineException>(() => CompilerOptions.Parse(["main.sol", argument]));

    [Fact]
    public void ScalarConflictsAreRejectedAndIdenticalRepeatsAreAllowed()
    {
        Assert.Equal(ArtifactType.Bin, CompilerOptions.Parse(["main.sol", "--artifact-type=bin", "--artifact-type=bin"]).Artifact);
        Assert.Throws<CommandLineException>(() => CompilerOptions.Parse(["main.sol", "--artifact-type=bin", "--artifact-type=solib"]));
        Assert.Throws<CommandLineException>(() => CompilerOptions.Parse(["--help", "--version"]));
        Assert.Throws<CommandLineException>(() => CompilerOptions.Parse(["main.sol", "-o"]));
        Assert.Throws<CommandLineException>(() => CompilerOptions.Parse(["main.sol", "second.sol", "-o", "output.json"]));
        Assert.Throws<CommandLineException>(() => CompilerOptions.Parse(Array.Empty<string>()));
    }

    [Theory]
    [InlineData("-Cllvm-compiler=orc")]
    [InlineData("-C=llvm-compiler=orc")]
    public void AttachedCodegenOptionIsParsed(string argument)
        => Assert.Equal(CompilerBackend.Orc, CompilerOptions.Parse(["main.sol", argument]).Backend);

    [Fact]
    public void ResponseFilesPreserveSpacesAndRejectNestedExpansion()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sol-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "args.txt");
        try
        {
            File.WriteAllText(file, "path with spaces/main.sol\n--emit=cst\n");
            var options = CompilerOptions.Parse(["@" + file]);
            Assert.Equal("path with spaces/main.sol", options.Sources.Single());
            Assert.Equal(EmitKind.Cst, options.Emit);
            File.WriteAllText(file, "@nested.txt\n");
            Assert.Throws<CommandLineException>(() => CompilerOptions.Parse(["@" + file]));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LiteralSeparatorPreventsOptionAndResponseExpansion()
    {
        Assert.Equal("-name.sol", Assert.Single(CompilerOptions.Parse(["--", "-name.sol"]).Sources));
        Assert.Equal("@name.sol", Assert.Single(CompilerOptions.Parse(["--", "@name.sol"]).Sources));
        Assert.Equal("@name.sol", Assert.Single(CompilerOptions.Parse(["@@name.sol"]).Sources));
    }
}
