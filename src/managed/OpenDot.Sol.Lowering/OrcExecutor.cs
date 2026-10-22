// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using OpenDot.Sol.Fir;

namespace OpenDot.Sol.Lowering;

/// <summary>Runs verified native FIR in the CLR-independent, process-isolated ORC host.</summary>
public static class OrcExecutor
{
    public static int Execute(VerifiedFirProgram program, IEnumerable<string> arguments, bool staticRuntime = false,
        string? backendDirectory = null, CancellationToken cancellationToken = default,
        Stream? standardOutput = null, Stream? standardError = null)
    {
        ArgumentNullException.ThrowIfNull(program); ArgumentNullException.ThrowIfNull(arguments);
        if (program.Program.IsLibrary) { throw new ArgumentException("ORC execution requires a program entry.", nameof(program)); }
        string root = Path.GetFullPath(backendDirectory ?? Environment.GetEnvironmentVariable("SOLC_NATIVE_ROOT") ?? Path.Combine(AppContext.BaseDirectory, "native"));
        string host = Path.Combine(root, staticRuntime ? "sol-exec-host-static.exe" : "sol-exec-host.exe");
        if (!File.Exists(host)) { throw new IOException("The qualified native ORC host is not installed."); }
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bitcode;
        using (var module = LlvmEmitter.Build(program, NativeCompiler.LoadBridge(root), cancellationToken))
        { bitcode = module.Bitcode(); }
        if (bitcode.Length is < 4 or > 64 * 1024 * 1024) { throw new IOException("LLVM bitcode exceeds the guest transport bounds."); }
        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var info = new ProcessStartInfo(host)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--bitcode-pipe"); info.ArgumentList.Add(pipe.GetClientHandleAsString()); info.ArgumentList.Add("--");
        foreach (string argument in arguments) { info.ArgumentList.Add(argument); }
        using var process = Process.Start(info) ?? throw new IOException("Cannot start the native ORC host.");
        var tasks = new List<Task>();
        try
        {
            pipe.DisposeLocalCopyOfClientHandle();
            tasks.Add(SendBitcode(pipe, bitcode, cancellationToken));
            // Guest stdin and its working directory remain inherited; bitcode has a dedicated channel.
            tasks.Add(process.StandardOutput.BaseStream.CopyToAsync(standardOutput ?? Console.OpenStandardOutput(), cancellationToken));
            tasks.Add(process.StandardError.BaseStream.CopyToAsync(standardError ?? Console.OpenStandardError(), cancellationToken));
            tasks.Add(process.WaitForExitAsync(cancellationToken));
            var pending = new List<Task>(tasks);
            while (pending.Count != 0)
            {
                Task first = Task.WhenAny(pending).GetAwaiter().GetResult();
                first.GetAwaiter().GetResult();
                pending.Remove(first);
            }
        }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(30000); }
            // Killing the guest releases a blocked pipe writer and the two redirected readers.
            try { Task.WhenAll(tasks).GetAwaiter().GetResult(); }
            catch (Exception) { /* Preserve the first transport/cancellation failure. */ }
            throw;
        }
        return process.ExitCode;
    }

    private static async Task SendBitcode(AnonymousPipeServerStream pipe, byte[] bitcode, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bitcode.Length);
        try
        {
            await pipe.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await pipe.WriteAsync(bitcode, cancellationToken).ConfigureAwait(false);
        }
        finally { pipe.Dispose(); }
    }
}
