// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "TraceHarnessSupport.h"
#include <array>
#include <cstdio>
#include <random>

using namespace OpenDot::Sol::Runtime;
using namespace OpenDot::Sol::Runtime::Heap;
using namespace OpenDot::Sol::Runtime::Test;

namespace
{
bool CompositeArrayElements()
{
    for (const unsigned stride : {16, 24, 32, 48, 64})
    {
        TraceFixture fixture;
        std::array<std::uint32_t, 2> offsets{0, stride - 8};
        const ScanShape shape{offsets.data(), 2};
        TypeDescriptor type = ArrayType;
        type.ScanShape = &shape;
        constexpr unsigned count = 513;
        auto *array = reinterpret_cast<StringArray *>(fixture.Allocate(
            type, sizeof(StringArray) + count * std::uint64_t{stride}));
        array->Length = count;
        array->Stride = stride;
        std::array<Node *, 17> nodes{};
        for (unsigned index = 0; index < nodes.size(); ++index)
        {
            nodes[index] = fixture.NewNode(index);
        }
        for (unsigned index = 0; index < count; ++index)
        {
            auto *element = reinterpret_cast<char *>(array + 1) + index * std::uint64_t{stride};
            *reinterpret_cast<Header **>(element) = &nodes[index % nodes.size()]->Object;
            *reinterpret_cast<Header **>(element + stride - 8) = &nodes[(index + 7ULL) % nodes.size()]->Object;
        }
        fixture.Begin();
        fixture.Root(&array->Object);
        fixture.Drain({3, 2});
        for (auto *node : nodes)
        {
            if (!fixture.Marked(&node->Object))
            {
                return false;
            }
        }
        const auto snapshot = fixture.State.Cost.Snapshot();
        if (Counter(snapshot, GcCounter::MarkReferenceSlots) != count * 2 + nodes.size() * 2
            || Counter(snapshot, GcCounter::MarkObjects) != nodes.size() + 1
            || Counter(snapshot, GcCounter::MarkMaxReferenceSlots) > 3)
        {
            return false;
        }
    }
    return true;
}

bool EmptyReferenceArrays()
{
    TraceFixture fixture;
    auto *array = fixture.NewArray(0);
    fixture.Begin();
    fixture.Root(&array->Object);
    const auto result = TraceBatch(fixture.State, {1, 1}, false);
    return result.Objects == 1 && result.ReferenceSlots == 0 && fixture.State.Work.Empty()
           && fixture.Marked(&array->Object);
}

bool ScalarLookalikesRemainUnmarked()
{
    TraceFixture fixture;
    auto *hidden = fixture.NewNode(5);
    auto *visible = fixture.NewNode(6);
    visible->Value = reinterpret_cast<std::uintptr_t>(&hidden->Object);
    fixture.Begin();
    fixture.Root(&visible->Object);
    fixture.Drain();
    return fixture.Marked(&visible->Object) && !fixture.Marked(&hidden->Object);
}

bool ArrayPaddingIsNotAReference()
{
    TraceFixture fixture;
    constexpr unsigned count = 25;
    const std::uint32_t offset = 0;
    const ScanShape shape{&offset, 1};
    auto type = ArrayType;
    type.ScanShape = &shape;
    auto *array = reinterpret_cast<StringArray *>(fixture.Allocate(type, sizeof(StringArray) + count * 32));
    array->Length = count;
    array->Stride = 32;
    auto *visible = fixture.NewNode(1);
    auto *hidden = fixture.NewNode(2);
    for (unsigned index = 0; index < count; ++index)
    {
        auto **element = reinterpret_cast<Header **>(reinterpret_cast<char *>(array + 1) + index * 32ULL);
        element[0] = &visible->Object;
        element[1] = &hidden->Object;
        element[2] = &hidden->Object;
        element[3] = &hidden->Object;
    }
    fixture.Begin();
    fixture.Root(&array->Object);
    fixture.Drain({1, 1});
    return fixture.Marked(&visible->Object) && !fixture.Marked(&hidden->Object)
           && Counter(fixture.State.Cost.Snapshot(), GcCounter::MarkReferenceSlots) == count + 2;
}

bool NewBlackAllocationUsesInsertionWork()
{
    TraceFixture fixture;
    auto *white = fixture.NewNode(1);
    fixture.Begin();
    auto *black = fixture.NewNode(2);
    // Allocation is black in the current epoch and is not queued for scanning. Publishing a
    // white target into it therefore depends on the insertion barrier shading that target.
    if (!fixture.Marked(&black->Object) || fixture.Marked(&white->Object) || !fixture.State.Work.Empty())
    {
        return false;
    }
    fixture.Root(&white->Object);
    black->Left = &white->Object;
    fixture.Root(&black->Object);
    fixture.Drain();
    return fixture.Marked(&white->Object) && fixture.Marked(&black->Object)
           && Counter(fixture.State.Cost.Snapshot(), GcCounter::MarkObjects) == 1;
}

bool MultipleEpochGraphs()
{
    TraceFixture fixture;
    std::array<Node *, 257> nodes{};
    for (unsigned index = 0; index < nodes.size(); ++index)
    {
        nodes[index] = fixture.NewNode(index);
    }
    std::mt19937 random(0xE90C4U);
    for (unsigned epoch = 0; epoch < 30; ++epoch)
    {
        std::array<std::array<int, 2>, 257> edges{};
        for (unsigned index = 0; index < nodes.size(); ++index)
        {
            for (unsigned field = 0; field < 2; ++field)
            {
                const auto target = random() % 3 ? static_cast<int>(random() % nodes.size()) : -1;
                edges[index][field] = target;
                auto *value = target < 0 ? nullptr : &nodes[static_cast<unsigned>(target)]->Object;
                (field ? nodes[index]->Right : nodes[index]->Left) = value;
            }
        }
        const unsigned root = random() % static_cast<unsigned>(nodes.size());
        std::array<bool, 257> expected{};
        std::vector<unsigned> pending{root};
        while (!pending.empty())
        {
            const auto index = pending.back();
            pending.pop_back();
            if (expected[index])
            {
                continue;
            }
            expected[index] = true;
            for (const auto target : edges[index])
            {
                if (target >= 0)
                {
                    pending.push_back(static_cast<unsigned>(target));
                }
            }
        }
        fixture.Begin();
        fixture.Root(&nodes[root]->Object);
        fixture.Drain({1 + epoch % 13, 1 + epoch % 7});
        for (unsigned index = 0; index < nodes.size(); ++index)
        {
            if (fixture.Marked(&nodes[index]->Object) != expected[index])
            {
                return false;
            }
        }
    }
    return true;
}

bool AlternatingEpochRoots()
{
    TraceFixture fixture;
    std::array<Node *, 130> nodes{};
    for (unsigned index = 0; index < nodes.size(); ++index)
    {
        nodes[index] = fixture.NewNode(index);
    }
    for (unsigned epoch = 0; epoch < 70; ++epoch)
    {
        fixture.Begin();
        for (unsigned index = epoch % 2; index < nodes.size(); index += 2)
        {
            fixture.Root(&nodes[index]->Object);
        }
        fixture.Drain({4, 2});
        for (unsigned index = 0; index < nodes.size(); ++index)
        {
            if (fixture.Marked(&nodes[index]->Object) != (index % 2 == epoch % 2))
            {
                return false;
            }
        }
    }
    return true;
}
}

int RunTraceLayoutTests()
{
    unsigned index = 0;
    for (auto test : {CompositeArrayElements, EmptyReferenceArrays, ScalarLookalikesRemainUnmarked,
                      ArrayPaddingIsNotAReference, NewBlackAllocationUsesInsertionWork,
                      MultipleEpochGraphs, AlternatingEpochRoots})
    {
        if (!test())
        {
            std::fprintf(stderr, "Trace layout fixture failed: %u\n", index);
            return 233;
        }
        ++index;
    }
    std::fprintf(
        stderr,
        "Trace layouts: composite stride, padding, empty arrays, black insertion, changing epoch graphs passed.\n");
    return 0;
}
