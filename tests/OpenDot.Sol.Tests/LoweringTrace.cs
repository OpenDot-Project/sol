// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;

namespace OpenDot.Sol.Tests;

// Records immediate interop calls to test managed policy without loading LLVM. It does not emit IR,
// optimize, verify LLVM, or pretend to produce native artifacts; native jobs cover those behaviors.
internal sealed class LoweringTrace : ILlvmCompiler, ILlvmModule
{
    private readonly List<object> _operations = [];
    private readonly Dictionary<long, string> _functions = [];
    private long _next;
    public string Version => "23.1.3";
    public ILlvmModule CreateModule() => this;
    public long Apply(LlvmOperation operation, long type, long[] operands, long data, string text)
    {
        long result = ++_next;
        if (operation == LlvmOperation.Function) { _functions.Add(result, text); }
        string? callee = operation == LlvmOperation.Call ? _functions[operands[0]] : null;
        _operations.Add(new { operation = operation.ToString(), result, type, operands, data, text, callee }); return result;
    }
    public long CreateString(byte[] utf8) { long result = ++_next; _operations.Add(new { operation = "StringBytes", result, utf8 }); return result; }
    public void Verify() { }
    public void Dispose() { }
    public string Print() => throw new NotSupportedException("A recording bridge cannot print LLVM.");
    public byte[] Bitcode() => throw new NotSupportedException("A recording bridge cannot emit bitcode.");
    public byte[] CompileObject() => throw new NotSupportedException("A recording bridge cannot compile objects.");
    public static string Inspect(VerifiedFirProgram program, bool staticRuntime = false, CancellationToken cancellationToken = default, bool cExports = false)
    {
        var trace = new LoweringTrace(); using var module = LlvmEmitter.Build(program, trace, cancellationToken, cExports);
        return JsonSerializer.Serialize(trace._operations);
    }
}
