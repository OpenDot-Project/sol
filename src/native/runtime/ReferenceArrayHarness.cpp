// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Core.h"
#include <cstring>
#include <cwchar>

using namespace OpenDot::Sol::Runtime;

namespace
{
struct Probe final
{
    Header Object;
    std::uint64_t Value;
};

const TypeDescriptor ProbeType{sizeof(Probe), 16, 0x543, 0, 0, &sol_rt_object_type, nullptr, nullptr, nullptr,
                               nullptr};

class Frame final
{
    Header *_slots[4]{};
    RootFrame _frame{nullptr, _slots, 4, 0};

public:
    Frame()
    {
        sol_gc_push_frame(&_frame);
    }

    ~Frame()
    {
        sol_gc_pop_frame(&_frame);
    }

    void Store(unsigned slot, Header *value)
    {
        sol_gc_store_ref(&_slots[slot], value);
    }
};

Probe *NewProbe(std::uint64_t value)
{
    auto *probe = reinterpret_cast<Probe *>(sol_gc_alloc(&ProbeType, sizeof(Probe)));
    probe->Value = value;
    return probe;
}

Header **ReferenceSlot(StringArray *array, int index)
{
    return reinterpret_cast<Header **>(sol_rt_array_address(array, index, sizeof(Header *)));
}

bool HasReferenceArrayDescriptor(const StringArray *array)
{
    if (!array || array->Object.Type != &sol_rt_reference_array_type
        || array->Object.Type->Kind != 2 || array->Object.Type->Size != sizeof(StringArray)
        || array->Element != &sol_rt_object_type || array->Stride != sizeof(Header *))
    {
        return false;
    }
    const auto *shape = static_cast<const ScanShape *>(array->Object.Type->ScanShape);
    return shape && shape->Offsets && shape->Count == 1 && shape->Offsets[0] == 0;
}

int VerifyReferenceArrays()
{
    Frame roots;
    auto *references = sol_rt_array_new(2, 11);
    roots.Store(0, &references->Object);
    if (!HasReferenceArrayDescriptor(references))
    {
        return 1;
    }

    auto *first = NewProbe(101);
    roots.Store(1, &first->Object);
    sol_gc_store_ref(ReferenceSlot(references, 0), &first->Object);
    roots.Store(1, nullptr);

    auto *second = NewProbe(202);
    roots.Store(1, &second->Object);
    sol_gc_store_ref(ReferenceSlot(references, 1), &second->Object);
    roots.Store(1, nullptr);

    sol_gc_collect();
    auto *firstValue = reinterpret_cast<Probe *>(references->Data()[0]);
    auto *secondValue = reinterpret_cast<Probe *>(references->Data()[1]);
    if (sol_gc_stats().ObjectCount != 3 || firstValue->Value != 101 || secondValue->Value != 202)
    {
        return 2;
    }

    auto *replacement = NewProbe(303);
    roots.Store(1, &replacement->Object);
    sol_gc_store_ref(ReferenceSlot(references, 0), &replacement->Object);
    sol_gc_store_ref(ReferenceSlot(references, 1), nullptr);
    roots.Store(1, nullptr);
    sol_gc_collect();
    if (sol_gc_stats().ObjectCount != 2
        || reinterpret_cast<Header *>(references->Data()[0]) != &replacement->Object
        || references->Data()[1] != nullptr || replacement->Value != 303)
    {
        return 3;
    }

    auto *scalar = sol_rt_array_new(1, 6);
    roots.Store(2, &scalar->Object);
    if (!scalar->Object.Type || scalar->Object.Type->ScanShape != nullptr || scalar->Stride != sizeof(std::uint64_t))
    {
        return 4;
    }
    auto *lookalike = NewProbe(404);
    roots.Store(1, &lookalike->Object);
    const auto addressBits = reinterpret_cast<std::uintptr_t>(&lookalike->Object);
    std::memcpy(sol_rt_array_address(scalar, 0, sizeof(addressBits)), &addressBits, sizeof(addressBits));
    roots.Store(1, nullptr);
    sol_gc_collect();
    if (sol_gc_stats().ObjectCount != 3)
    {
        return 5; // Scalar payload bits must not keep an object alive.
    }

    auto *strings = sol_rt_array_new(1, 10);
    roots.Store(3, &strings->Object);
    auto *text = sol_rt_string_create("canonical", 9);
    roots.Store(1, &text->Object);
    if (strings->Object.Type != &sol_rt_string_array_type || strings->Element != &sol_rt_string_type
        || strings->Stride != sizeof(String *))
    {
        return 6;
    }
    sol_gc_store_ref(ReferenceSlot(strings, 0), &text->Object);
    roots.Store(1, nullptr);
    sol_gc_collect();
    if (sol_gc_stats().ObjectCount != 5 || strings->Data()[0]->ByteLength != 9
        || std::memcmp(strings->Data()[0]->Data(), "canonical", 9) != 0)
    {
        return 7; // Kind 10 retains its canonical String descriptor and traced layout.
    }

    roots.Store(0, nullptr);
    roots.Store(2, nullptr);
    roots.Store(3, nullptr);
    sol_gc_collect();
    return sol_gc_stats().ObjectCount == 0 ? 0 : 8;
}
}

int RunReferenceArrayTests()
{
    sol_gc_attach();
    const int result = VerifyReferenceArrays();
    sol_rt_shutdown();
    return result;
}

int RunArrayAccessFailure(const wchar_t *mode)
{
    sol_gc_attach();
    auto *array = sol_rt_array_new(1, 11);
    if (std::wcscmp(mode, L"bounds") == 0)
    {
        (void)sol_rt_array_address(array, 1, sizeof(Header *));
    }
    else if (std::wcscmp(mode, L"negative-index") == 0)
    {
        (void)sol_rt_array_address(array, -1, sizeof(Header *));
    }
    else if (std::wcscmp(mode, L"stride") == 0)
    {
        (void)sol_rt_array_address(array, 0, sizeof(Header *) / 2);
    }
    return 99; // Every invalid index/stride must fail before returning an address.
}
