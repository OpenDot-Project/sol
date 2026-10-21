// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
namespace OpenDot.Sol.Lowering;

public sealed partial class LlvmEmitter
{
    private void DeclareRuntime()
    {
        Declare("sol_rt_literal", "ptr", ["ptr"]);
        Declare("sol_rt_arguments", "ptr", ["i32", "ptr"]);
        Declare("sol_rt_shutdown", "void", []);
        Declare("sol_rt_fail", "void", ["i32"]);
        Declare("sol_rt_write_string", "void", ["ptr", "i32"]);
        Declare("sol_rt_write_integer", "void", ["i64", "i32", "i32"]);
        Declare("sol_rt_write_float", "void", ["double", "i32", "i32"]);
        Declare("sol_rt_newline", "void", ["i32"]);
        Declare("sol_rt_string_concat", "ptr", ["ptr", "ptr"]);
        Declare("sol_rt_string_compare", "i32", ["ptr", "ptr"]);
        Declare("sol_rt_string_length", "i32", ["ptr"]);
        Declare("sol_rt_string_substring", "ptr", ["ptr", "i32", "i32"]);
        Declare("sol_rt_string_index_of", "i32", ["ptr", "ptr", "i32"]);
        Declare("sol_rt_string_hash", "i32", ["ptr"]);
        Declare("sol_rt_array_length", "i32", ["ptr"]);
        Declare("sol_rt_array_new", "ptr", ["i32", "i32"]);
        Declare("sol_rt_array_address", "ptr", ["ptr", "i32", "i32"]);
        Declare("sol_gc_push_frame", "void", ["ptr"]);
        Declare("sol_gc_pop_frame", "void", ["ptr"]);
        Declare("sol_gc_store_ref", "void", ["ptr", "ptr"]);
        Declare("sol_gc_poll", "void", []);
        Declare("sol_gc_collect", "void", []);
        Declare("sol_gc_enter_export", "i32", []);
        Declare("sol_gc_leave_export", "void", ["i32"]);
        Declare("sol_rt_check_abi", "void", ["i64"]);
        foreach (string type in new[] { "i8", "i16", "i32", "i64" })
        {
            long pair = Op(LlvmOperation.StructType, args: [T(type), T("i1")]);
            foreach (string sign in new[] { "s", "u" })
            {
                foreach (string operation in new[] { "add", "sub", "mul" })
                { Declare("llvm." + sign + operation + ".with.overflow." + type, pair, [T(type), T(type)]); }
            }
        }
    }
}
