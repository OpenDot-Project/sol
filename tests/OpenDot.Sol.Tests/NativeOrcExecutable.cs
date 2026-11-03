// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;

namespace OpenDot.Sol.Tests;

// Test-only application runner. The compiler itself never starts a generated program.
internal static class NativeOrcExecutable
{
    internal static int Execute(VerifiedFirProgram program, IEnumerable<string> arguments, bool staticRuntime = false,
        string? backendDirectory = null, CancellationToken cancellationToken = default,
        Stream? standardOutput = null, Stream? standardError = null)
    {
        if (standardOutput is { CanWrite: false } || standardError is { CanWrite: false })
        { throw new NotSupportedException("The application output stream must be writable."); }
        var artifact = OrcExecutableCompiler.Compile(program, staticRuntime, backendDirectory, cancellationToken);
        string scratch = Path.Combine(Path.GetTempPath(), "sol-orc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string executable = Path.Combine(scratch, "sol-orc-test.exe");
            File.WriteAllBytes(executable, artifact.Executable);
            File.WriteAllBytes(Path.Combine(scratch, "SolJit.dll"), artifact.JitDll!);
            if (artifact.RuntimeDll is not null) { File.WriteAllBytes(Path.Combine(scratch, "SolRT.dll"), artifact.RuntimeDll); }
            var info = new ProcessStartInfo(executable)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) { info.ArgumentList.Add(argument); }
            using var process = Process.Start(info) ?? throw new IOException("Cannot start the generated ORC executable.");
            var tasks = new List<Task>();
            try
            {
                tasks.Add(process.StandardOutput.BaseStream.CopyToAsync(standardOutput ?? Console.OpenStandardOutput(), cancellationToken));
                tasks.Add(process.StandardError.BaseStream.CopyToAsync(standardError ?? Console.OpenStandardError(), cancellationToken));
                tasks.Add(process.WaitForExitAsync(cancellationToken));
                var pending = new List<Task>(tasks);
                while (pending.Count != 0)
                {
                    Task first = Task.WhenAny(pending).GetAwaiter().GetResult();
                    first.GetAwaiter().GetResult(); pending.Remove(first);
                }
            }
            catch
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(30000); }
                try { Task.WhenAll(tasks).GetAwaiter().GetResult(); }
                catch (Exception) { /* Preserve the original cancellation or consumer failure. */ }
                throw;
            }
            return process.ExitCode;
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }
}
