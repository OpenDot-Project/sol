// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: extern/unsafe syntax only; native ABI/code generation/transition checks remain planned.
module source.native.rules

extern "c" {
    @link("kernel32")
    func GetTickCount64(): ulong
}

func ticks(): ulong {
    unsafe { return GetTickCount64() }
}

@repr("c")
struct NativePoint {
    var x: int
    var y: int
    constructor(x: int, y: int) { this.x = x; this.y = y }
}

// The initial target is Windows x64 C ABI. C long is not Sol long; width mappings
// are explicit. Half/quad native pass-through cannot be assumed supported.
// String/Object/Throwable/interfaces/closures/nullable/ref structs do not pass directly.
// Unmanaged layout and blittable ref/out storage need an explicit verified boundary.
// unsafe grants access to unsafe operations; it does not disable all arithmetic/null/bounds checks.
// NativePtr is an opaque library/runtime value, not a numeric alias or a new primitive.
// Roots publish before a NativeSafe transition. Callbacks attach to the runtime and
// cannot leak Sol/C++/CLR exceptions across a C boundary. UTF-8/UTF-16 ownership is explicit.
