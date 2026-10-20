// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading;
using OpenDot.Sol.Fir;

namespace OpenDot.Sol.Lowering;

public sealed record NativeArtifact(byte[] Executable, byte[]? RuntimeDll, string LlvmIr);

/// <summary>LLVM object production and isolated lld-link execution for the qualified Windows bootstrap.</summary>
public static class NativeCompiler
{
    public static NativeArtifact Compile(VerifiedFirProgram program, bool staticRuntime = false,
        string? backendDirectory = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(backendDirectory ?? Environment.GetEnvironmentVariable("SOLC_NATIVE_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "native"));
        string llvmIr = LlvmEmitter.Emit(program, staticRuntime, cancellationToken);
        var bridge = LoadBridge(root);
        byte[] objectBytes = bridge.CompileObject(llvmIr);
        if (objectBytes.Length < 20 || objectBytes[0] != 0x64 || objectBytes[1] != 0x86)
        { throw new IOException("LLVM did not produce an AMD64 COFF object."); }
        string scratch = Path.Combine(Path.GetTempPath(), "sol-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string objectPath = Path.Combine(scratch, "program.obj"), output = Path.Combine(scratch, "program.exe");
            File.WriteAllBytes(objectPath, objectBytes);
            var configuration = File.ReadAllLines(Path.Combine(root, "link-paths.txt"));
            string linker = Path.Combine(root, "lld-link.exe");
            if (!File.Exists(linker)) { throw new IOException("The qualified lld-link executable is missing."); }
            string runtime = Path.Combine(root, staticRuntime ? "SolRT-static.lib" : "SolRT.lib");
            var arguments = new List<string> { "/nologo", "/machine:x64", "/subsystem:console", "/Brepro", "/out:" + output,
                objectPath, runtime, "libcmt.lib", "libvcruntime.lib", "libucrt.lib", "oldnames.lib", "kernel32.lib" };
            arguments.AddRange(configuration.Select(path => "/libpath:" + path));
            var info = new ProcessStartInfo(linker) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (string argument in arguments) { info.ArgumentList.Add(argument); }
            using var process = Process.Start(info) ?? throw new IOException("Could not start lld-link.");
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { if (!process.HasExited) { process.Kill(true); } throw; }
            string message = error.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult();
            if (process.ExitCode != 0) { throw new IOException("lld-link failed: " + message); }
            cancellationToken.ThrowIfCancellationRequested();
            return new(File.ReadAllBytes(output), staticRuntime ? null : File.ReadAllBytes(Path.Combine(root, "SolRT.dll")), llvmIr);
        }
        finally { Directory.Delete(scratch, true); }
    }

    public static ILlvmCompiler LoadBridge(string backendDirectory)
    {
        string root = Path.GetFullPath(backendDirectory);
        string bridge = Path.Combine(root, "OpenDot.Sol.LlvmBridge.dll"), llvm = Path.Combine(root, "LLVM-C.dll");
        if (!File.Exists(bridge) || !File.Exists(llvm)) { throw new IOException("The native backend is not installed. Build/install the LLVM backend component."); }
        try
        {
            // Load the exact component DLL, never an arbitrary LLVM-C.dll from the working directory.
            NativeLibrary.Load(llvm);
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(bridge);
            var type = assembly.GetType("OpenDot.Sol.LlvmBridge.Compiler", true)!;
            var compiler = Activator.CreateInstance(type) as ILlvmCompiler ?? throw new IOException("LLVM bridge contract mismatch.");
            if (compiler.Version != "23.1.3") { throw new IOException("LLVM backend version mismatch; 23.1.3 is required."); }
            return compiler;
        }
        catch (Exception error) when (error is BadImageFormatException or DllNotFoundException or FileLoadException or TypeLoadException or TargetInvocationException)
        { throw new IOException("The LLVM bridge could not be loaded: " + error.Message, error); }
    }
}
