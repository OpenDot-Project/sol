// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "TraceHarnessSupport.h"
#include <tuple>
#include <bit>
#include "CheckedAccess.h"
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
        TraceFixture fixture(TestHeapLimit);
        std::array<std::uint32_t, 2> offsets{0, stride - 8};
        const ScanShape shape{offsets.data(), 2};
        TypeDescriptor type = ArrayType;
        type.ScanShape = &shape;
        constexpr unsigned count = 513;
        const std::uint64_t elementStride = stride;
        auto *array = Detail::StorageCast<StringArray>(fixture.Allocate(
            type, sizeof(StringArray) + count * elementStride));
        if (!array)
        {
            sol_rt_fail(7);
        }
        array->Length = count;
        array->Stride = stride;
        std::array<Node *, 17> nodes{};
        for (unsigned index = 0; index < nodes.size(); ++index)
        {
            nodes.at(index) = fixture.NewNode(index);
        }
        const std::span bytes(Detail::StorageCast<std::byte>(array->Data()), count * elementStride);
        for (unsigned index = 0; index < count; ++index)
        {
            const auto offset = index * elementStride;
            *Detail::StorageSlot<Header *>(bytes, offset) = &nodes.at(index % nodes.size())->Object;
            *Detail::StorageSlot<Header *>(bytes, offset + stride - 8) = &nodes.at((index + 7ULL) % nodes.size())->
                Object;
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
        if (Counter(snapshot, GcCounter::MarkReferenceSlots) != count * 2ULL + nodes.size() * 2
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
    TraceFixture fixture(TestHeapLimit);
    auto *array = fixture.NewArray(0);
    fixture.Begin();
    fixture.Root(&array->Object);
    const auto result = TraceBatch(fixture.State, {1, 1}, false);
    return result.Objects == 1 && result.ReferenceSlots == 0 && fixture.State.Work.Empty()
           && fixture.Marked(&array->Object);
}

bool ScalarLookalikesRemainUnmarked()
{
    TraceFixture fixture(TestHeapLimit);
    auto *hidden = fixture.NewNode(5);
    auto *visible = fixture.NewNode(6);
    visible->Value = std::bit_cast<std::uintptr_t>(&hidden->Object);
    fixture.Begin();
    fixture.Root(&visible->Object);
    fixture.Drain();
    return fixture.Marked(&visible->Object) && !fixture.Marked(&hidden->Object);
}

bool ArrayPaddingIsNotAReference()
{
    TraceFixture fixture(TestHeapLimit);
    constexpr unsigned count = 25;
    constexpr std::uint32_t offset = 0;
    const ScanShape shape{&offset, 1};
    auto type = ArrayType;
    type.ScanShape = &shape;
    auto *array = Detail::StorageCast<StringArray>(fixture.Allocate(type, sizeof(StringArray) + count * 32ULL));
    if (!array)
    {
        sol_rt_fail(7);
    }
    array->Length = count;
    array->Stride = 32;
    auto *visible = fixture.NewNode(1);
    auto *hidden = fixture.NewNode(2);
    const std::span bytes(Detail::StorageCast<std::byte>(array->Data()), count * 32ULL);
    for (unsigned index = 0; index < count; ++index)
    {
        const auto byteOffset = index * 32ULL;
        *Detail::StorageSlot<Header *>(bytes, byteOffset) = &visible->Object;
        *Detail::StorageSlot<Header *>(bytes, byteOffset + 8) = &hidden->Object;
        *Detail::StorageSlot<Header *>(bytes, byteOffset + 16) = &hidden->Object;
        *Detail::StorageSlot<Header *>(bytes, byteOffset + 24) = &hidden->Object;
    }
    fixture.Begin();
    fixture.Root(&array->Object);
    fixture.Drain({1, 1});
    return fixture.Marked(&visible->Object) && !fixture.Marked(&hidden->Object)
           && Counter(fixture.State.Cost.Snapshot(), GcCounter::MarkReferenceSlots) == count + 2;
}

bool NewBlackAllocationUsesInsertionWork()
{
    TraceFixture fixture(TestHeapLimit);
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
    TraceFixture fixture(TestHeapLimit);
    std::array<Node *, 257> nodes{};
    for (unsigned index = 0; index < nodes.size(); ++index)
    {
        nodes.at(index) = fixture.NewNode(index);
    }
    // NOLINTNEXTLINE(bugprone-random-generator-seed, cert-msc51-cpp): Fixed seeds make the model regression reproducible.
    std::mt19937 random(0xE90C4U);
    for (unsigned epoch = 0; epoch < 30; ++epoch)
    {
        std::array<std::array<int, 2>, 257> edges{};
        for (unsigned index = 0; index < nodes.size(); ++index)
        {
            for (unsigned field = 0; field < 2; ++field)
            {
                const auto target = random() % 3 ? Detail::Integer<int>(random() % nodes.size()) : -1;
                edges.at(index).at(field) = target;
                auto *value = target < 0 ? nullptr : &nodes.at(Detail::Integer<unsigned>(target))->Object;
                (field ? nodes.at(index)->Right : nodes.at(index)->Left) = value;
            }
        }
        const unsigned root = random() % Detail::Integer<unsigned>(nodes.size());
        std::array<bool, 257> expected{};
        std::vector<unsigned> pending{root};
        while (!pending.empty())
        {
            const auto index = pending.back();
            pending.pop_back();
            if (expected.at(index))
            {
                continue;
            }
            expected.at(index) = true;
            for (const auto target : edges.at(index))
            {
                if (target >= 0)
                {
                    pending.push_back(Detail::Integer<unsigned>(target));
                }
            }
        }
        fixture.Begin();
        fixture.Root(&nodes.at(root)->Object);
        fixture.Drain({1 + epoch % 13, 1 + epoch % 7});
        for (unsigned index = 0; index < nodes.size(); ++index)
        {
            if (fixture.Marked(&nodes.at(index)->Object) != expected.at(index))
            {
                return false;
            }
        }
    }
    return true;
}

bool AlternatingEpochRoots()
{
    TraceFixture fixture(TestHeapLimit);
    std::array<Node *, 130> nodes{};
    for (unsigned index = 0; index < nodes.size(); ++index)
    {
        nodes.at(index) = fixture.NewNode(index);
    }
    for (unsigned epoch = 0; epoch < 70; ++epoch)
    {
        fixture.Begin();
        for (unsigned index = epoch % 2; index < nodes.size(); index += 2)
        {
            fixture.Root(&nodes.at(index)->Object);
        }
        fixture.Drain({4, 2});
        for (unsigned index = 0; index < nodes.size(); ++index)
        {
            if (fixture.Marked(&nodes.at(index)->Object) != (index % 2 == epoch % 2))
            {
                return false;
            }
        }
    }
    return true;
}
}

int RunTraceLayoutTests() noexcept(false)
{
    unsigned index = 0;
    for (auto test : {CompositeArrayElements, EmptyReferenceArrays, ScalarLookalikesRemainUnmarked,
                      ArrayPaddingIsNotAReference, NewBlackAllocationUsesInsertionWork,
                      MultipleEpochGraphs, AlternatingEpochRoots})
    {
        if (!test || !test())
        {
            std::ignore = std::fprintf(stderr, "Trace layout fixture failed: %u\n", index);
            return 233;
        }
        ++index;
    }
    std::ignore = std::fprintf(
        stderr,
        "Trace layouts: composite stride, padding, empty arrays, black insertion, changing epoch graphs passed.\n");
    return 0;
}
