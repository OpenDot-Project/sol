// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

[Collection("CompilerConsole")]
public sealed class NativeEhProbeTests
{
    public static bool Enabled => NativeRuntimeIntegrationTests.Enabled;

    [Theory(Skip = "Runs in the required native-backend test job.", SkipUnless = nameof(Enabled), Timeout = 60000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedWindowsFuncletsPreserveCarrierIdentityThroughCleanupAndGc(bool staticRuntime)
    {
        string backend = Environment.GetEnvironmentVariable("SOLC_NATIVE_ROOT")!;
        var compiler = NativeCompiler.LoadBridge(backend);
        using var module = compiler.CreateModule();
        BuildProbe(module, staticRuntime);
        module.Verify();
        string printed = module.Print();
        Assert.Contains("catchswitch", printed);
        Assert.Contains("cleanuppad", printed);
        Assert.Contains("[ \"funclet\"", printed);
        string scratch = Path.Combine(Path.GetTempPath(), "sol-eh-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string obj = Path.Combine(scratch, "probe.obj"), exe = Path.Combine(scratch, "probe.exe");
            File.WriteAllBytes(obj, module.CompileObject());
            var arguments = new List<string>
            {
                "/nologo", "/machine:x64", "/subsystem:console", "/out:" + exe, obj,
                Path.Combine(backend, staticRuntime ? "SolRT-static.lib" : "SolRT.lib"),
                "msvcrt.lib", "vcruntime.lib", "ucrt.lib", "oldnames.lib", "kernel32.lib",
            };
            arguments.AddRange(WindowsLinkEnvironment.Resolve(backend, Environment.GetEnvironmentVariable("LIB"))
                .Select(path => "/libpath:" + path));
            int status = ((ILldLinker)compiler).LinkWindows(arguments.ToArray(), out string diagnostics);
            Assert.True(status == 0, diagnostics);
            if (!staticRuntime) { File.Copy(Path.Combine(backend, "SolRT.dll"), Path.Combine(scratch, "SolRT.dll")); }
            using var child = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            })!;
            var error = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            try
            {
                await child.WaitForExitAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); }
                throw;
            }
            Assert.True(child.ExitCode == 0, $"Native EH probe exited {child.ExitCode}: {await error}");
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    // Immediate LLVM API conformance probe; source FIR admission remains a separate gate.
    private static void BuildProbe(ILlvmModule module, bool staticRuntime)
    {
        long Op(LlvmOperation op, long type = 0, long[]? args = null, long data = 0, string text = "")
            => module.Apply(op, type, args ?? [], data, text);
        long voidType = Op(LlvmOperation.VoidType), ptr = Op(LlvmOperation.PointerType);
        long i32 = Op(LlvmOperation.IntegerType, data: 32), i64 = Op(LlvmOperation.IntegerType, data: 64);
        long i1 = Op(LlvmOperation.IntegerType, data: 1);
        long Integer(int value) => Op(LlvmOperation.Integer, i32, text: value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        long nullValue = Op(LlvmOperation.Zero, ptr), zero = Integer(0), one = Integer(1);
        long frameType = Op(LlvmOperation.StructType, args: [ptr, ptr, i32, i32]);
        long descriptorType = Op(LlvmOperation.StructType, args: [i64, i64, i64, i32, i32, ptr, ptr, ptr, ptr, ptr, i64]);
        long descriptor = Op(LlvmOperation.ExternalGlobal, descriptorType, data: staticRuntime ? 0 : 1,
            text: "sol_rt_runtime_exception_type");
        long Declare(string name, long result, params long[] parameters)
            => Op(LlvmOperation.Function, result, parameters, text: name);
        long personality = Declare("__CxxFrameHandler3", i32);
        long attach = Declare("sol_gc_attach", voidType), collect = Declare("sol_gc_collect", voidType);
        long push = Declare("sol_gc_push_frame", voidType, ptr), pop = Declare("sol_gc_pop_frame", voidType, ptr);
        long barrier = Declare("sol_gc_store_ref", voidType, ptr, ptr);
        long shutdown = Declare("sol_rt_shutdown", voidType);
        long create = Declare("sol_rt_exception_create", ptr, ptr, ptr, ptr);
        long throwing = Declare("sol_rt_throw", voidType, ptr);
        long current = Declare("sol_rt_current_exception", ptr);
        long isInstance = Declare("sol_rt_is_instance", i1, ptr, ptr);
        long helper = Declare("sol_probe_throw", voidType, ptr, ptr);
        long main = Declare("wmain", i32, i32, ptr);
        Op(LlvmOperation.Personality, args: [helper, personality]);
        Op(LlvmOperation.Personality, args: [main, personality]);
        long Block(long function, string name) => Op(LlvmOperation.Block, args: [function], text: name);
        void Position(long block) => Op(LlvmOperation.Position, args: [block]);
        long Call(long function, params long[] args) => Op(LlvmOperation.Call, args: [function, .. args]);
        long Field(long frame, int index) => Op(LlvmOperation.StructGep, frameType, [frame], index);
        long PushFrame(long? value = null)
        {
            long frame = Op(LlvmOperation.Alloca, frameType, data: 8);
            Op(LlvmOperation.Store, args: [Op(LlvmOperation.Zero, frameType), frame]);
            if (value is not null)
            {
                long root = Op(LlvmOperation.Alloca, ptr, data: 8);
                Op(LlvmOperation.Store, args: [value.Value, root]);
                Op(LlvmOperation.Store, args: [root, Field(frame, 1)]);
                Op(LlvmOperation.Store, args: [one, Field(frame, 2)]);
            }
            Call(push, frame);
            return frame;
        }
        long helperEntry = Block(helper, "entry"), dead = Block(helper, "dead"), cleanup = Block(helper, "cleanup");
        Position(helperEntry);
        long errorParameter = Op(LlvmOperation.Parameter, args: [helper], data: 0);
        long counterParameter = Op(LlvmOperation.Parameter, args: [helper], data: 1);
        long helperFrame = PushFrame(errorParameter);
        Op(LlvmOperation.Invoke, args: [throwing, dead, cleanup, errorParameter]);
        Position(dead); Op(LlvmOperation.Unreachable);
        Position(cleanup);
        long cleanupPad = Op(LlvmOperation.CleanupPad);
        long rootAddress = Op(LlvmOperation.Load, ptr, [Field(helperFrame, 1)]);
        Op(LlvmOperation.FuncletCall, args: [barrier, rootAddress, nullValue], data: cleanupPad);
        // At this point only the carrier keeps the Throwable alive.
        Op(LlvmOperation.FuncletCall, args: [collect], data: cleanupPad);
        Op(LlvmOperation.Store, args: [one, counterParameter]);
        Op(LlvmOperation.FuncletCall, args: [pop, helperFrame], data: cleanupPad);
        Op(LlvmOperation.CleanupReturn, args: [cleanupPad, 0]);

        long entry = Block(main, "entry"), unexpected = Block(main, "unexpected");
        long dispatch = Block(main, "dispatch"), handler = Block(main, "handler"), done = Block(main, "done");
        long success = Block(main, "success"), failure = Block(main, "failure");
        Position(entry); Call(attach);
        long callerFrame = PushFrame();
        long counter = Op(LlvmOperation.Alloca, i32, data: 8);
        Op(LlvmOperation.Store, args: [zero, counter]);
        long errorValue = Call(create, descriptor, nullValue, nullValue);
        Op(LlvmOperation.Invoke, args: [helper, unexpected, dispatch, errorValue, counter]);
        Position(unexpected); Op(LlvmOperation.Unreachable);
        Position(dispatch);
        long catchSwitch = Op(LlvmOperation.CatchSwitch, args: [0, handler]);
        Position(handler);
        long catchPad = Op(LlvmOperation.CatchPad, args: [catchSwitch, nullValue, Integer(64), nullValue]);
        long caught = Op(LlvmOperation.FuncletCall, args: [current], data: catchPad);
        Op(LlvmOperation.FuncletCall, args: [collect], data: catchPad);
        long matches = Op(LlvmOperation.FuncletCall, args: [isInstance, caught, descriptor], data: catchPad);
        long identical = Op(LlvmOperation.ICmp, args: [caught, errorValue], data: 0);
        long valid = Op(LlvmOperation.And, args: [matches, identical]);
        Op(LlvmOperation.CatchReturn, args: [catchPad, done]);
        Position(done);
        long count = Op(LlvmOperation.Load, i32, [counter]);
        long cleaned = Op(LlvmOperation.ICmp, args: [count, one], data: 0);
        long passed = Op(LlvmOperation.And, args: [valid, cleaned]);
        Call(pop, callerFrame); Call(shutdown);
        Op(LlvmOperation.ConditionalBranch, args: [passed, success, failure]);
        Position(success); Op(LlvmOperation.Return, args: [zero]);
        Position(failure); Op(LlvmOperation.Return, args: [Integer(99)]);
    }
}
