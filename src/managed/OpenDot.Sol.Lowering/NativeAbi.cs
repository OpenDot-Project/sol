// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;

namespace OpenDot.Sol.Lowering;

/// <summary>Windows x64 target layout; this describes target storage, never CLR object layout.</summary>
internal static class NativeAbi
{
    internal const ulong BridgeProtocol = 0x534F4C4C00000003;
    internal const ulong JitProtocol = 0x534F4C4A00000001;
    internal const int AllocationAlignment = 16;

    internal sealed record Field(string Name, int Offset, int Size, bool GcReference = false);
    internal sealed record Layout(string Name, int Size, int Alignment, ImmutableArray<Field> Fields);

    internal static readonly ImmutableArray<Layout> Layouts =
    [
        new("Header", 16, 8, [new("Type", 0, 8), new("Flags", 8, 4), new("Reserved", 12, 4)]),
        new("TypeDescriptor", 80, 16,
        [
            new("Size", 0, 8), new("Alignment", 8, 8), new("TypeId", 16, 8),
            new("Kind", 24, 4), new("LayoutVersion", 28, 4), new("Base", 32, 8),
            new("ScanShape", 40, 8), new("MethodTable", 48, 8), new("GenericIdentity", 56, 8),
            new("ExceptionMetadata", 64, 8), new("Reserved", 72, 8),
        ]),
        new("RootFrame", 24, 8,
            [new("Previous", 0, 8), new("Slots", 8, 8), new("Count", 16, 4), new("Reserved", 20, 4)]),
        new("InterfaceValue", 16, 8, [new("Object", 0, 8, true), new("Table", 8, 8)]),
        new("InterfaceCell", 32, 8,
            [new("Object", 0, 16), new("Value", 16, 8, true), new("Table", 24, 8)]),
        new("ReferenceLocation", 32, 8,
            [new("Kind", 0, 4), new("Flags", 4, 4), new("Owner", 8, 8, true),
                new("AddressOrOffset", 16, 8), new("CallerRoot", 24, 8)]),
    ];

    internal static int Align(int offset, int alignment)
    {
        if (offset < 0 || alignment <= 0 || (alignment & (alignment - 1)) != 0)
        { throw new ArgumentOutOfRangeException(nameof(alignment)); }
        return checked((offset + alignment - 1) & -alignment);
    }
}
