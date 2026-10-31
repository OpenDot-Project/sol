// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "TraceHarnessSupport.h"
#include "CheckedAccess.h"
#include <cwchar>

using namespace OpenDot::Sol::Runtime;
using namespace OpenDot::Sol::Runtime::Test;

int RunDescriptorFailure(const wchar_t *mode)
{
    TraceFixture fixture(TestHeapLimit);
    std::uint32_t offset = 0;
    ScanShape shape{&offset, 1};
    TypeDescriptor type = NodeType;
    type.ScanShape = &shape;
    auto *object = [&]() -> Header * {
        if (std::wcscmp(mode, L"null-offsets") == 0)
        {
            shape.Offsets = nullptr;
            return fixture.Allocate(type, sizeof(Node));
        }
        else if (std::wcscmp(mode, L"header-offset") == 0)
        {
            offset = 8;
            return fixture.Allocate(type, sizeof(Node));
        }
        else if (std::wcscmp(mode, L"unaligned-offset") == 0)
        {
            offset = 17;
            return fixture.Allocate(type, sizeof(Node));
        }
        else if (std::wcscmp(mode, L"outside-object") == 0)
        {
            offset = (sizeof(Node) + 15) & ~15U;
            return fixture.Allocate(type, sizeof(Node));
        }
        else if (std::wcscmp(mode, L"descriptor-extent") == 0)
        {
            // Extra aligned allocation bytes cannot extend a fixed object's declared reference fields.
            type.Size = offsetof(Node, Value);
            offset = offsetof(Node, Value);
            return fixture.Allocate(type, sizeof(Node));
        }
        else if (std::wcscmp(mode, L"short-array") == 0)
        {
            type.Kind = 2;
            type.Size = sizeof(Header);
            return fixture.Allocate(type, sizeof(Header));
        }
        else
        {
            type = ArrayType;
            type.ScanShape = &shape;
            auto *array = Detail::StorageCast<StringArray>(fixture.Allocate(type, sizeof(StringArray) + 16));
            array->Length = 2;
            array->Stride = 8;

            if (std::wcscmp(mode, L"negative-length") == 0)
            {
                array->Length = -1;
            }
            else if (std::wcscmp(mode, L"zero-stride") == 0)
            {
                array->Stride = 0;
            }
            else if (std::wcscmp(mode, L"oversized-payload") == 0)
            {
                array->Length = INT32_MAX;
            }
            else if (std::wcscmp(mode, L"element-overlap") == 0)
            {
                offset = 8;
            }
            else if (std::wcscmp(mode, L"element-overflow") == 0)
            {
                offset = UINT32_MAX;
            }
            else
            {
                return nullptr;
            }
            return &array->Object;
        }
    }();
    if (!object)
    {
        return 98;
    }
    fixture.Begin();
    fixture.Root(object);
    fixture.Drain();
    return 99; // A malformed descriptor must fail before any invalid reference load occurs.
}
