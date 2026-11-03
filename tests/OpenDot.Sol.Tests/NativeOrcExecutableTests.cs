// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using OpenDot.Sol.Cli;
using Xunit;

namespace OpenDot.Sol.Tests;

[Collection("CompilerConsole")]
public sealed class NativeOrcExecutableTests
{
    public static bool Enabled => NativeRuntimeIntegrationTests.Enabled;

    [Theory(Skip = "Runs in the required native-backend test job.", SkipUnless = nameof(Enabled), Timeout = 120000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompilerPublishesRelocatableExecutableWithoutRunningItsProgram(bool staticRuntime)
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        string scratch = Path.Combine(Path.GetTempPath(), "sol-orc-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string source = Path.Combine(scratch, "Main.sol"), output = Path.Combine(scratch, "built", "App.exe");
            File.WriteAllText(source, "module native.orc; import std.stdio; object Main { func main(args: []String) throws IOException { println(args[0]) } }");
            using var compilerOutput = new StringWriter(); using var compilerError = new StringWriter();
            var originalOutput = Console.Out; var originalError = Console.Error;
            try
            {
                Console.SetOut(compilerOutput); Console.SetError(compilerError);
                string[] flags = [source, "-Cllvm-compiler=orc", "-o", output];
                if (staticRuntime) { flags = [.. flags, "--static-solrt-lib"]; }
                Assert.True(Program.Main(flags) == 0, compilerError.ToString());
            }
            finally { Console.SetOut(originalOutput); Console.SetError(originalError); }
            Assert.Empty(compilerOutput.ToString()); Assert.Empty(compilerError.ToString());
            Assert.True(File.Exists(output));
            Assert.True(File.Exists(Path.Combine(scratch, "built", "SolJit.dll")));
            Assert.Equal(!staticRuntime, File.Exists(Path.Combine(scratch, "built", "SolRT.dll")));
            string relocated = Path.Combine(scratch, "relocated");
            Directory.Move(Path.GetDirectoryName(output)!, relocated);
            var result = await Run(Path.Combine(relocated, "App.exe"), scratch);
            Assert.True(result.Exit == 0, result.Error);
            Assert.Equal("İstanbul🙂\n", result.Output); Assert.Empty(result.Error);

            string sidecar = Path.Combine(relocated, "SolJit.dll");
            using (var file = new FileStream(sidecar, FileMode.Open, FileAccess.ReadWrite))
            { file.Position = file.Length - 1; int last = file.ReadByte(); file.Position--; file.WriteByte((byte)(last ^ 1)); }
            result = await Run(Path.Combine(relocated, "App.exe"), scratch);
            Assert.Equal(1, result.Exit); Assert.Contains("SHA-256 mismatch", result.Error); Assert.Empty(result.Output);
            if (!staticRuntime)
            {
                using (var file = new FileStream(sidecar, FileMode.Open, FileAccess.ReadWrite))
                { file.Position = file.Length - 1; int last = file.ReadByte(); file.Position--; file.WriteByte((byte)(last ^ 1)); }
                string runtime = Path.Combine(relocated, "SolRT.dll");
                File.WriteAllBytes(runtime, [0, 1, 2, 3]);
                result = await Run(Path.Combine(relocated, "App.exe"), scratch);
                Assert.Equal(1, result.Exit); Assert.Contains("SHA-256 mismatch", result.Error); Assert.Empty(result.Output);
            }
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    private static async Task<(int Exit, string Output, string Error)> Run(string executable, string directory)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("İstanbul🙂");
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try { await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(60)); }
        catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(30000); } throw; }
        return (process.ExitCode, await output, await error);
    }
}
