// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
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
        string scratch = Path.Combine(Path.GetTempPath(), "sol-orc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string ir = Path.Combine(scratch, "program.ll");
            File.WriteAllText(ir, LlvmEmitter.Emit(program, staticRuntime, cancellationToken), new UTF8Encoding(false));
            var info = new ProcessStartInfo(host)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(ir); info.ArgumentList.Add("--");
            foreach (string argument in arguments) { info.ArgumentList.Add(argument); }
            using var process = Process.Start(info) ?? throw new IOException("Cannot start the native ORC host.");
            // Explicit byte pumps preserve redirected Windows console handles across the process boundary.
            Task output = process.StandardOutput.BaseStream.CopyToAsync(standardOutput ?? Console.OpenStandardOutput(), cancellationToken);
            Task error = process.StandardError.BaseStream.CopyToAsync(standardError ?? Console.OpenStandardError(), cancellationToken);
            Task streams = Task.WhenAll(output, error);
            Task exited = process.WaitForExitAsync(cancellationToken);
            try
            {
                var pending = new List<Task> { exited, output, error };
                while (!exited.IsCompleted)
                {
                    Task first = Task.WhenAny(pending).GetAwaiter().GetResult();
                    if (first == exited) { break; }
                    if (first.IsFaulted || first.IsCanceled)
                    {
                        if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(30000); }
                        streams.GetAwaiter().GetResult();
                    }
                    pending.Remove(first);
                }
                exited.GetAwaiter().GetResult();
                streams.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(30000); }
                throw;
            }
            return process.ExitCode;
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }
}
