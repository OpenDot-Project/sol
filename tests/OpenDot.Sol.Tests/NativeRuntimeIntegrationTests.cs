// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Cli;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

/// <summary>Native CI opts in explicitly; a required run with a missing backend fails instead of skipping.</summary>
[Collection("CompilerConsole")]
public sealed class NativeRuntimeIntegrationTests
{
    public static bool Enabled => Environment.GetEnvironmentVariable("SOL_TEST_NATIVE_REQUIRED") == "1";
    private static string Backend
    {
        get
        {
            string? root = Environment.GetEnvironmentVariable("SOLC_NATIVE_ROOT");
            Assert.NotNull(root); Assert.NotEmpty(root);
            Assert.True(File.Exists(Path.Combine(root, "sol-exec-host.exe")), "The required ORC backend is missing.");
            Assert.False(File.Exists(Path.Combine(root, "lld-link.exe")), "Solc must use embedded LLD.");
            Assert.False(File.Exists(Path.Combine(root, "llvm-lib.exe")), "Archive production must use embedded LLD.");
            return root;
        }
    }

    private static VerifiedFirProgram Build(string body, bool library = false)
    {
        string source = library ? "func add(a, b: int): int = a + b" :
            "import std.stdio\nobject Main { func main(args: []String) throws IOException { " + body + " } }";
        var compilation = FirCompilation.Create([AstTestSources.Parse(source)], TestContext.Current.CancellationToken, library);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics.Select(d => d.Message)));
        return compilation.Program;
    }

    [Theory(Skip = "Runs in the required native-backend test job.", SkipUnless = nameof(Enabled), Timeout = 60000)]
    [InlineData(NativeArtifactKind.Bin, false)]
    [InlineData(NativeArtifactKind.Bin, true)]
    [InlineData(NativeArtifactKind.Cdylib, false)]
    [InlineData(NativeArtifactKind.Cdylib, true)]
    [InlineData(NativeArtifactKind.Staticlib, false)]
    [InlineData(NativeArtifactKind.Staticlib, true)]
    [InlineData(NativeArtifactKind.Solib, false)]
    [InlineData(NativeArtifactKind.Solib, true)]
    public void RealLlvmProducesEachArtifactAndTheExpectedCompanions(NativeArtifactKind kind, bool staticRuntime)
    {
        var program = Build("val values = [1, 2]; Gc.collect()", library: kind != NativeArtifactKind.Bin);
        var artifact = NativeCompiler.Compile(program, staticRuntime, Backend, TestContext.Current.CancellationToken,
            kind, artifactName: kind == NativeArtifactKind.Cdylib ? "NativeLibrary.dll" : null);
        Assert.NotEmpty(artifact.Executable);
        if (kind is NativeArtifactKind.Bin or NativeArtifactKind.Cdylib)
        { Assert.Equal((byte)'M', artifact.Executable[0]); Assert.Equal((byte)'Z', artifact.Executable[1]); }
        if (kind == NativeArtifactKind.Staticlib) { Assert.Equal("!<arch>\n", Encoding.ASCII.GetString(artifact.Executable, 0, 8)); }
        if (kind == NativeArtifactKind.Solib) { Assert.NotEmpty(SolLibraryArchive.Read(artifact.Executable)); }
        Assert.Equal(!staticRuntime && kind != NativeArtifactKind.Solib, artifact.RuntimeDll is not null);
        Assert.Equal(kind == NativeArtifactKind.Cdylib, artifact.ImportLibrary is not null);
        Assert.Equal(kind == NativeArtifactKind.Staticlib && !staticRuntime, artifact.RuntimeLibrary is not null);
        string scratch = Path.Combine(Path.GetTempPath(), "sol-native-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string source = Path.Combine(scratch, "Main.sol");
            File.WriteAllText(source, kind == NativeArtifactKind.Bin
                ? "module native.entry; object Main { func main(args: []String) { val items = [1, 2]; Gc.collect() } }"
                : "module native.library; func add(a, b: int): int = a + b");
            string extension = kind switch { NativeArtifactKind.Bin => ".exe", NativeArtifactKind.Cdylib => ".dll", NativeArtifactKind.Staticlib => ".lib", _ => ".solib" };
            string output = Path.Combine(scratch, "NativeLibrary" + extension);
            string[] flags = [source, "--artifact-type=" + kind.ToString().ToLowerInvariant(), "-o", output];
            if (staticRuntime) { flags = [.. flags, "--static-solrt-lib"]; }
            Assert.Equal(0, Program.Main(flags));
            Assert.True(File.Exists(output));
            Assert.Equal(!staticRuntime && kind != NativeArtifactKind.Solib, File.Exists(Path.Combine(scratch, "SolRT.dll")));
            if (kind == NativeArtifactKind.Solib) { Assert.NotEmpty(SolLibraryArchive.Read(File.ReadAllBytes(output))); }
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    [Theory(Skip = "Runs in the required native-backend test job.", SkipUnless = nameof(Enabled), Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public void OrcRunsInTheGuestAndPreservesIndependentUtf8Streams(bool staticRuntime)
    {
        using var stdout = new MemoryStream(); using var stderr = new MemoryStream();
        int code = OrcExecutor.Execute(Build("println(args[0] + \"🙂\"); errorln(\"error\"); Gc.collect()"),
            ["İstanbul"], staticRuntime, Backend, TestContext.Current.CancellationToken, stdout, stderr);
        Assert.Equal(0, code);
        Assert.Equal("İstanbul🙂\n", Encoding.UTF8.GetString(stdout.ToArray()));
        Assert.Equal("error\n", Encoding.UTF8.GetString(stderr.ToArray()));
        using var parent = Process.GetCurrentProcess();
        Assert.DoesNotContain(parent.Modules.Cast<ProcessModule>(), module =>
            string.Equals(module.ModuleName, "SolRT.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(Skip = "Runs in the required native-backend test job.", SkipUnless = nameof(Enabled), Timeout = 30000)]
    public async Task CancellationAfterGuestOutputTerminatesExecution()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var output = new SignalStream(fail: false); using var error = new MemoryStream();
        var program = Build("for { println(\"running\") }");
        Task<int> execution = Task.Run(() => OrcExecutor.Execute(program, [], backendDirectory: Backend,
            cancellationToken: cancellation.Token, standardOutput: output, standardError: error), TestContext.Current.CancellationToken);
        try { await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await execution);
    }

    [Fact(Skip = "Runs in the required native-backend test job.", SkipUnless = nameof(Enabled), Timeout = 30000)]
    public async Task BrokenOutputCannotLeaveTheGuestBlockedOnAFullPipe()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        using var output = new SignalStream(fail: true); using var error = new MemoryStream();
        var program = Build("for { println(\"output\") }");
        await Assert.ThrowsAsync<IOException>(() => Task.Run(() => OrcExecutor.Execute(program, [], backendDirectory: Backend,
            cancellationToken: cancellation.Token, standardOutput: output, standardError: error), TestContext.Current.CancellationToken));
    }

    [Theory(Skip = "Runs in the required native-backend test job.", SkipUnless = nameof(Enabled), Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidOutputStreamCannotLeaveANewGuestRunning(bool staticRuntime)
    {
        string name = staticRuntime ? "sol-exec-host-static" : "sol-exec-host";
        var before = GuestIds(name);
        using var output = new MemoryStream([], writable: false); using var error = new MemoryStream();
        Assert.Throws<NotSupportedException>(() => OrcExecutor.Execute(Build("for { }"), [], staticRuntime,
            Backend, TestContext.Current.CancellationToken, output, error));
        Assert.All(GuestIds(name), id => Assert.Contains(id, before));
    }

    private static int[] GuestIds(string name) => Process.GetProcessesByName(name).Select(process =>
    { using (process) { return process.Id; } }).ToArray();

    private sealed class SignalStream(bool fail) : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { if (count > 0) { Accept(); } }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); if (!buffer.IsEmpty) { Accept(); } return ValueTask.CompletedTask; }
        private void Accept()
        {
            Started.TrySetResult();
            if (fail) { throw new IOException("Consumer closed the output stream."); }
        }
    }
}
