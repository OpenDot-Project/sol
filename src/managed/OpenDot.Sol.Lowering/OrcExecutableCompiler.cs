// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using OpenDot.Sol.Fir;

namespace OpenDot.Sol.Lowering;

/// <summary>Links a native bootstrap and an envelope without compiling program functions.</summary>
public static class OrcExecutableCompiler
{
    public static NativeArtifact Compile(VerifiedFirProgram program, bool staticRuntime = false,
        string? backendDirectory = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        cancellationToken.ThrowIfCancellationRequested();
        if (program.Program.IsLibrary) { throw new ArgumentException("ORC produces executable artifacts only.", nameof(program)); }
        string root = NativeCompiler.BackendRoot(backendDirectory);
        var bridge = NativeCompiler.LoadBridge(root);
        using var source = LlvmEmitter.Build(program, bridge, cancellationToken);
        byte[] payload = OrcEnvelope.Create(source.Bitcode(), staticRuntime);
        byte[] jit = File.ReadAllBytes(Path.Combine(root, "OpenDot.Sol.LlvmNative.dll"));
        byte[]? runtime = staticRuntime ? null : File.ReadAllBytes(Path.Combine(root, "SolRT.dll"));
        using var data = bridge.CreateModule();
        data.CreateData(payload, "sol_program_payload");
        byte[] length = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(length, (ulong)payload.Length);
        data.CreateData(length, "sol_program_payload_size");
        data.CreateData(SHA256.HashData(jit), "sol_program_jit_sha256");
        data.CreateData(runtime is null ? new byte[32] : SHA256.HashData(runtime), "sol_program_runtime_sha256");
        data.Verify();
        cancellationToken.ThrowIfCancellationRequested();
        string scratch = Path.Combine(Path.GetTempPath(), "sol-orc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string objectPath = Path.Combine(scratch, "payload.obj"), output = Path.Combine(scratch, "program.exe");
            File.WriteAllBytes(objectPath, data.CompileObject());
            string bootstrap = Path.Combine(root, staticRuntime ? "SolOrcBootstrap-static.lib" : "SolOrcBootstrap.lib");
            var arguments = new List<string>
            {
                "/nologo", "/machine:x64", "/Brepro", "/subsystem:console", "/out:" + output, objectPath, bootstrap,
                "msvcrt.lib", "vcruntime.lib", "ucrt.lib", "oldnames.lib", "kernel32.lib", "bcrypt.lib",
            };
            if (staticRuntime) { arguments.Add(Path.Combine(root, "SolRT-static.lib")); }
            arguments.AddRange(WindowsLinkEnvironment.Resolve(root, Environment.GetEnvironmentVariable("LIB"))
                .Select(path => "/libpath:" + path));
            cancellationToken.ThrowIfCancellationRequested();
            if (bridge is not ILldLinker linker) { throw new IOException("The embedded LLD component is unavailable."); }
            if (linker.LinkWindows(arguments.ToArray(), out string diagnostics) != 0)
            { throw new IOException("Embedded ORC executable linking failed: " + diagnostics); }
            cancellationToken.ThrowIfCancellationRequested();
            return new(File.ReadAllBytes(output), runtime) { JitDll = jit };
        }
        finally { Directory.Delete(scratch, true); }
    }
}
