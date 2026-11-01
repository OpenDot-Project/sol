// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Gc.h"
#include "ObjectAbi.h"
#include <array>
#include <cstddef>
#include <cstdio>
#include <span>

namespace
{
void Layout(const char *name, std::size_t size, std::size_t alignment,
            std::span<const std::size_t> offsets, bool last) noexcept
{
    std::printf("{\"name\":\"%s\",\"size\":%zu,\"alignment\":%zu,\"offsets\":[", name, size, alignment);
    bool first = true;
    for (const auto offset : offsets)
    {
        std::printf("%s%zu", first ? "" : ",", offset);
        first = false;
    }
    std::printf("]}%s", last ? "" : ",");
}
}

int RunAbiLayoutProbe() noexcept
{
    using namespace OpenDot::Sol::Runtime;
    std::printf("{\"schema\":1,\"layouts\":[");
    Layout("Header", sizeof(Header), alignof(Header),
           std::array{offsetof(Header, Type), offsetof(Header, Flags), offsetof(Header, Reserved)}, false);
    Layout("TypeDescriptor", sizeof(TypeDescriptor), alignof(TypeDescriptor),
           std::array{offsetof(TypeDescriptor, Size), offsetof(TypeDescriptor, Alignment),
                      offsetof(TypeDescriptor, TypeId), offsetof(TypeDescriptor, Kind),
                      offsetof(TypeDescriptor, LayoutVersion), offsetof(TypeDescriptor, Base),
                      offsetof(TypeDescriptor, ScanShape), offsetof(TypeDescriptor, MethodTable),
                      offsetof(TypeDescriptor, GenericIdentity), offsetof(TypeDescriptor, ExceptionMetadata),
                      offsetof(TypeDescriptor, Reserved)}, false);
    Layout("RootFrame", sizeof(RootFrame), alignof(RootFrame),
           std::array{offsetof(RootFrame, Previous), offsetof(RootFrame, Slots),
                      offsetof(RootFrame, Count), offsetof(RootFrame, Reserved)}, false);
    Layout("InterfaceValue", sizeof(InterfaceValue), alignof(InterfaceValue),
           std::array{offsetof(InterfaceValue, Object), offsetof(InterfaceValue, Table)}, false);
    Layout("InterfaceCell", sizeof(InterfaceCell), alignof(InterfaceCell),
           std::array{offsetof(InterfaceCell, Object), offsetof(InterfaceCell, Value), offsetof(InterfaceCell, Table)},
           false);
    Layout("ReferenceLocation", sizeof(ReferenceLocation), alignof(ReferenceLocation),
           std::array{offsetof(ReferenceLocation, Kind), offsetof(ReferenceLocation, Flags),
                      offsetof(ReferenceLocation, Owner), offsetof(ReferenceLocation, AddressOrOffset),
                      offsetof(ReferenceLocation, CallerRoot)}, true);
    std::printf("]}\n");
    return std::ferror(stdout) ? 1 : 0;
}
