// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.IO;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class WindowsLinkEnvironmentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sol-link-paths-" + Guid.NewGuid().ToString("N"));
    public WindowsLinkEnvironmentTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    private string Libraries()
    {
        string path = Path.Combine(root, "libraries space İ");
        Directory.CreateDirectory(path);
        foreach (string name in new[] { "libcmt.lib", "libvcruntime.lib", "libucrt.lib", "oldnames.lib", "kernel32.lib" })
        { File.WriteAllBytes(Path.Combine(path, name), []); }
        return path;
    }

    [Fact]
    public void RelocatedBackendUsesDeveloperShellAndDeduplicatesPaths()
    {
        string libraries = Libraries();
        Assert.Equal([libraries], WindowsLinkEnvironment.Resolve(root, " ;\"" + libraries + "\";" + libraries + ";"));
    }

    [Fact]
    public void ExplicitDeveloperFileTakesPrecedenceOverEnvironment()
    {
        string libraries = Libraries();
        File.WriteAllLines(Path.Combine(root, "link-paths.txt"), [libraries]);
        Assert.Equal([libraries], WindowsLinkEnvironment.Resolve(root, "relative/untrusted"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(";;")]
    public void MissingEnvironmentReportsRequiredLibraries(string? environment)
    {
        var error = Assert.Throws<IOException>(() => WindowsLinkEnvironment.Resolve(root, environment));
        Assert.Contains("libcmt.lib", error.Message);
        Assert.Contains("x64 Visual Studio developer shell", error.Message);
    }

    [Fact]
    public void MissingSdkLibraryCannotBeHiddenByValidCrt()
    {
        string libraries = Libraries();
        File.Delete(Path.Combine(libraries, "kernel32.lib"));
        var error = Assert.Throws<IOException>(() => WindowsLinkEnvironment.Resolve(root, libraries));
        Assert.Contains("kernel32.lib", error.Message);
        Assert.DoesNotContain("libcmt.lib,", error.Message);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("C:relative")]
    public void RejectsWorkingDirectoryDependentPaths(string path)
        => Assert.Throws<IOException>(() => WindowsLinkEnvironment.Resolve(root, path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void EmptyBackendEnvironmentUsesPackagedLocation(string? environment)
        => Assert.Equal(Path.Combine(AppContext.BaseDirectory, "native"), NativeCompiler.ResolveBackendRoot(null, environment));

    [Fact]
    public void ExplicitBackendOverridesEnvironment()
        => Assert.Equal(root, NativeCompiler.ResolveBackendRoot(root, Path.GetTempPath()));
}
