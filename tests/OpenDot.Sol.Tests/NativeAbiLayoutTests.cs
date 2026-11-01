// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.IO;
using System.Linq;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

[Collection("CompilerConsole")]
public sealed class NativeAbiLayoutTests
{
    public static bool Enabled => NativeRuntimeIntegrationTests.Enabled;

    [Fact(Skip = "Runs in the required native-backend test job.", SkipUnless = nameof(Enabled))]
    public void LlvmTargetDataMatchesCompilerOffsetsAndNaturalAlignment()
    {
        string? backend = Environment.GetEnvironmentVariable("SOLC_NATIVE_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(backend));
        using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", "abi-manifest.xml"));
        AbiManifest.Validate(input);
        using var module = NativeCompiler.LoadBridge(backend!).CreateModule();
        long i32 = module.Apply(LlvmOperation.IntegerType, 0, [], 32, "");
        long i64 = module.Apply(LlvmOperation.IntegerType, 0, [], 64, "");
        long pointer = module.Apply(LlvmOperation.PointerType, 0, [], 0, "");
        long header = module.Apply(LlvmOperation.StructType, 0, [pointer, i32, i32], 0, "");
        foreach (var layout in NativeAbi.Layouts)
        {
            long[] fields = layout.Fields.Select(field => field.Size == 16 ? header
                : field.Size == 4 ? i32 : i64).ToArray();
            long type = module.Apply(LlvmOperation.StructType, 0, fields, 0, "");
            Assert.Equal(layout.Size, module.Apply(LlvmOperation.TypeSize, type, [], 0, ""));
            Assert.Equal(8, module.Apply(LlvmOperation.TypeAlignment, type, [], 0, ""));
            for (int i = 0; i < fields.Length; i++)
            {
                Assert.Equal(layout.Fields[i].Offset,
                    module.Apply(LlvmOperation.ElementOffset, type, [], i, ""));
            }
            Assert.ThrowsAny<Exception>(() => module.Apply(LlvmOperation.ElementOffset, type, [], fields.Length, ""));
        }
        long unsized = module.Apply(LlvmOperation.VoidType, 0, [], 0, "");
        Assert.ThrowsAny<Exception>(() => module.Apply(LlvmOperation.TypeSize, unsized, [], 0, ""));
    }

    [Fact(Skip = "Runs in the required native-backend test job.", SkipUnless = nameof(Enabled))]
    public void EhOperationsRejectForeignBlocksMissingBundlesAndInvalidTokens()
    {
        string backend = Environment.GetEnvironmentVariable("SOLC_NATIVE_ROOT")!;
        using var module = NativeCompiler.LoadBridge(backend).CreateModule();
        long Op(LlvmOperation op, long type = 0, long[]? args = null, long data = 0, string text = "")
            => module.Apply(op, type, args ?? [], data, text);
        long voidType = Op(LlvmOperation.VoidType), i32 = Op(LlvmOperation.IntegerType, data: 32);
        long personality = Op(LlvmOperation.Function, i32, text: "__CxxFrameHandler3");
        long caller = Op(LlvmOperation.Function, voidType, text: "caller");
        long other = Op(LlvmOperation.Function, voidType, text: "other");
        long external = Op(LlvmOperation.Function, voidType, text: "callee");
        long entry = Op(LlvmOperation.Block, args: [caller], text: "entry");
        long foreign = Op(LlvmOperation.Block, args: [other], text: "foreign");
        long done = Op(LlvmOperation.Block, args: [caller], text: "done");
        Op(LlvmOperation.Personality, args: [caller, personality]);
        Op(LlvmOperation.Position, args: [entry]);
        Assert.Throws<InvalidOperationException>(() => Op(LlvmOperation.Invoke, args: [external, foreign, done]));
        Assert.Throws<InvalidOperationException>(() => Op(LlvmOperation.FuncletCall, args: [external]));
        Assert.Throws<InvalidOperationException>(() => Op(LlvmOperation.FuncletCall, args: [external], data: caller));
        Assert.Throws<InvalidOperationException>(() => Op(LlvmOperation.Personality, args: [caller, external]));
        Op(LlvmOperation.Return);
        Op(LlvmOperation.Position, args: [done]); Op(LlvmOperation.Return);
        Op(LlvmOperation.Position, args: [foreign]); Op(LlvmOperation.Return);
        module.Verify();
    }
}
