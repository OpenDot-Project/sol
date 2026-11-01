// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

[Collection("CompilerConsole")]
public sealed class NativeLazyEngineTests
{
    public static bool Enabled => NativeRuntimeIntegrationTests.Enabled;

    [Theory(Skip = "Runs in the required native-backend test job.", SkipUnless = nameof(Enabled), Timeout = 120000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LazyEntryAndHelperUnwindAcrossRegisteredWindowsCodeWithPublishedCallerRoots(bool staticRuntime)
    {
        var result = await RunProbe(staticRuntime, corrupt: false, TestContext.Current.CancellationToken);
        Assert.True(result.Exit == 0, result.Error);
        Assert.Contains("lazy-functions=2", result.Output);
        Assert.Contains("unwind-tables=2", result.Output);
        Assert.Empty(result.Error);
    }

    [Theory(Skip = "Runs in the required native-backend test job.", SkipUnless = nameof(Enabled), Timeout = 120000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DamagedEnvelopeFailsBeforeAnyProgramBodyIsCompiled(bool staticRuntime)
    {
        var result = await RunProbe(staticRuntime, corrupt: true, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Exit);
        Assert.Contains("SHA-256", result.Error);
        Assert.Empty(result.Output);
    }

    private static async Task<(int Exit, string Output, string Error)> RunProbe(bool staticRuntime, bool corrupt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string backend = Environment.GetEnvironmentVariable("SOLC_NATIVE_ROOT")!;
        string flavor = staticRuntime ? "static" : "dynamic";
        string probe = Path.GetFullPath(Path.Combine(backend, "..", "jit-probe", flavor, "sol-jit-probe.exe"));
        Assert.True(File.Exists(probe), "The native lazy-JIT conformance probe is missing.");
        using var module = NativeCompiler.LoadBridge(backend).CreateModule();
        NativeEhProbeTests.BuildProbe(module, staticRuntime); module.Verify();
        byte[] envelope = OrcEnvelope.Create(module.Bitcode(), staticRuntime);
        if (corrupt) { envelope[^1] ^= 0x40; }
        string directory = Path.Combine(Path.GetTempPath(), "sol-lazy-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string payload = Path.Combine(directory, "program.bin"); File.WriteAllBytes(payload, envelope);
            var start = new ProcessStartInfo(probe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = directory,
            };
            start.ArgumentList.Add(payload);
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = child.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await child.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); }
                throw;
            }
            return (child.ExitCode, await output, await error);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
