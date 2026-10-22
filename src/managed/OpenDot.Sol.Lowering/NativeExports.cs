// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Lowering;

public sealed partial class LlvmEmitter
{
    private void EmitExports()
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { "sol_library_shutdown" };
        var reserved = new HashSet<string>(StringComparer.Ordinal)
        {
            "memcpy", "memmove", "memset", "memcmp", "memchr", "strlen", "strcmp", "wcslen", "wcscmp",
            "malloc", "calloc", "realloc", "free", "fmod", "fmodf", "exit", "abort",
        };
        foreach (var function in _verified.Program.Functions.Where(f => f.ExportName is not null))
        {
            string name = function.ExportName!;
            if (name.StartsWith("sol_", StringComparison.Ordinal) || name.StartsWith('_') || reserved.Contains(name))
            { throw new IOException("C export name is reserved by the compiler/runtime: " + name); }
            if (!names.Add(name)) { throw new IOException("C export names must be unique: " + name); }
            bool Unmanaged(ResolvedType type) => type.Kind == ResolvedTypeKind.Primitive && type.Name is not ("bool" or "half" or "quad");
            if (!(Unmanaged(function.ReturnType) || Fir.FirVerifier.IsEmpty(function.ReturnType))
                || function.Parameters.Any(p => !Unmanaged(p.Type)) || !function.Effects.IsEmpty)
            { throw new IOException("C exports require unmanaged scalar signatures and no escaping checked exceptions: " + name); }
            _nativeFunction = Declare(name, Type(function.ReturnType), function.Parameters.Select(p => p.Mode is AstPassingMode.Ref or AstPassingMode.Out ? "ptr" : Type(p.Type)).ToArray(), 2);
            Start(NewBlock("entry"));
            Call("sol_rt_check_abi", Int(unchecked((long)RuntimeContract.Id), "i64"));
            long transition = Call("sol_gc_enter_export");
            long result = Call("sol_fn_" + function.Id, function.Parameters.Select((p, i) => Op(LlvmOperation.Parameter, args: [_nativeFunction], data: i)).ToArray());
            Call("sol_gc_leave_export", transition);
            Op(LlvmOperation.Return, args: Type(function.ReturnType) == "void" ? [] : [result]);
        }
        _nativeFunction = Declare("sol_library_shutdown", "void", [], 2);
        Start(NewBlock("entry")); Call("sol_rt_shutdown"); Op(LlvmOperation.Return);
    }
}
