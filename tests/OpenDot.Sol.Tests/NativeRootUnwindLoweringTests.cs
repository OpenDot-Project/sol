// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using System.Text.Json;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class NativeRootUnwindLoweringTests
{
    [Fact]
    public void DirectAndIndirectCallsHaveUnwindEdgesAndEveryFrameHasOneCleanupPop()
    {
        var source = AstTestSources.Parse("func increment(value: int): int = value + 1;\n"
            + "func apply(callback: (int) -> int): int = callback(3);\n"
            + "object Main { func main(args: []String) { val result = apply(increment) } }");
        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);
        Assert.NotNull(compilation.Program);
        using var document = JsonDocument.Parse(LoweringTrace.Inspect(compilation.Program,
            cancellationToken: TestContext.Current.CancellationToken));
        var operations = document.RootElement.EnumerateArray().ToArray();
        string Operation(JsonElement item) => item.GetProperty("operation").GetString()!;
        Assert.Contains(operations, item => Operation(item) == "IndirectInvoke");
        Assert.DoesNotContain(operations, item => Operation(item) == "IndirectCall");
        Assert.Contains(operations, item => Operation(item) == "Invoke"
            && item.GetProperty("callee").GetString()!.StartsWith("sol_v0_", System.StringComparison.Ordinal));
        int frames = operations.Count(item => item.GetProperty("callee").GetString() == "sol_gc_push_frame");
        Assert.Equal(compilation.Program.Program.Functions.Count(f => f.RuntimeFunction == FirRuntimeFunction.None) + 1, frames);
        Assert.Equal(frames, operations.Count(item => Operation(item) == "CleanupPad"));
        Assert.Equal(frames, operations.Count(item => Operation(item) == "CleanupReturn"));
        Assert.Equal(frames, operations.Count(item => Operation(item) == "FuncletCall"
            && item.GetProperty("callee").GetString() == "sol_gc_pop_frame"));
        Assert.DoesNotContain(operations, item => Operation(item) == "Invoke"
            && item.GetProperty("callee").GetString() is "sol_gc_pop_frame" or "sol_gc_push_frame" or "sol_gc_store_ref");
        var argument = operations.Single(item => item.GetProperty("callee").GetString() == "sol_rt_arguments");
        int position = System.Array.IndexOf(operations, argument);
        Assert.Contains(operations.Skip(position + 1), item => item.GetProperty("callee").GetString() == "sol_gc_store_ref"
            && item.GetProperty("operands")[2].GetInt64() == argument.GetProperty("result").GetInt64());
    }
}
