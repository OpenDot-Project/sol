// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: extern/unsafe syntax only; LLVM bridge, native link and ABI qualification are planned.
module demonstrations.windows.clock

extern "c" {
    @link("kernel32")
    func GetTickCount64(): ulong
}

func millisecondsSinceBoot(): ulong {
    unsafe { return GetTickCount64() }
}

// GetTickCount64 returns an unsigned 64-bit value. The wrapper does not pass Sol
// managed objects or exceptions through C ABI. Runtime transitions/roots remain mandatory.
