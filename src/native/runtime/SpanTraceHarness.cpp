// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "TraceHarnessSupport.h"
#include <tuple>
#include "CheckedAccess.h"
#include <cstdio>
#include <random>

using namespace OpenDot::Sol::Runtime;
using namespace OpenDot::Sol::Runtime::Heap;
using namespace OpenDot::Sol::Runtime::Test;

namespace
{
int DenseSpan()
{
    TraceFixture fixture(TestHeapLimit);
    std::vector<Node *> nodes;
    for (unsigned index = 0; index < 1000; ++index)
    {
        nodes.push_back(fixture.NewNode(index));
    }
    for (std::size_t index = 0; index + 1 < nodes.size(); ++index)
    {
        nodes.at(index)->Left = &nodes.at(index + 1)->Object;
    }
    fixture.Begin();
    fixture.Root(&nodes.at(0)->Object);
    fixture.Drain();
    for (auto *node : nodes)
    {
        if (!fixture.Marked(&node->Object))
        {
            return 229;
        }
    }
    const auto snapshot = fixture.State.Cost.Snapshot();
    if (Counter(snapshot, GcCounter::MarkObjects) != nodes.size()
        || Counter(snapshot, GcCounter::MarkReferenceSlots) != nodes.size() * 2
        || fixture.State.Work.Claims() >= nodes.size())
    {
        return 230;
    }
    return 0;
}

int DisconnectedAndCycles()
{
    TraceFixture fixture(TestHeapLimit);
    std::vector<Node *> nodes;
    for (unsigned index = 0; index < 70; ++index)
    {
        nodes.push_back(fixture.NewNode(index));
    }
    for (unsigned index = 0; index < 35; ++index)
    {
        nodes.at(index)->Left = &nodes.at((index + 1) % 35)->Object;
        nodes.at(index)->Right = &nodes.at((index + 7) % 35)->Object;
    }
    for (unsigned index = 35; index < 70; ++index)
    {
        nodes.at(index)->Left = &nodes.at(35ULL + (index + 1ULL) % 35)->Object;
    }
    fixture.Begin();
    fixture.Root(&nodes.at(0)->Object);
    fixture.Drain(TraceBudget{3, 2});
    for (unsigned index = 0; index < nodes.size(); ++index)
    {
        if (fixture.Marked(&nodes.at(index)->Object) != (index < 35))
        {
            return 231;
        }
    }
    return 0;
}

int LargeArrayResumption()
{
    TraceFixture fixture(TestHeapLimit);
    auto *array = fixture.NewArray(10000);
    if (!array)
    {
        sol_rt_fail(7);
    }
    const std::span elements(Detail::StorageCast<Header *>(array->Data()), Detail::Integer<std::size_t>(array->Length));
    std::vector<Node *> nodes;
    for (unsigned index = 0; index < 300; ++index)
    {
        nodes.push_back(fixture.NewNode(index));
    }
    for (unsigned index = 0; index < 10000; ++index)
    {
        Detail::CheckedAt(elements, index) = &nodes.at(index % nodes.size())->Object;
    }
    fixture.Begin();
    fixture.Root(&array->Object);
    const auto first = TraceBatch(fixture.State, TraceBudget{7, 2}, false);
    if (first.ReferenceSlots != 7 || first.Objects || fixture.State.Index.at(&array->Object)->Cursor != 7)
    {
        return 232;
    }
    fixture.Drain(TraceBudget{7, 2});
    if (fixture.State.Index.at(&array->Object)->Cursor != 10000)
    {
        return 233;
    }
    for (auto *node : nodes)
    {
        if (!fixture.Marked(&node->Object))
        {
            return 234;
        }
    }
    const auto snapshot = fixture.State.Cost.Snapshot();
    if (Counter(snapshot, GcCounter::MarkReferenceSlots) != 10600
        || Counter(snapshot, GcCounter::MarkMaxReferenceSlots) > 7
        || Counter(snapshot, GcCounter::MarkMaxObjects) > 2)
    {
        return 235;
    }
    return 0;
}

int ScalarBudget()
{
    TraceFixture fixture(TestHeapLimit);
    std::vector<Header *> nodes;
    for (unsigned index = 0; index < 500; ++index)
    {
        nodes.push_back(fixture.Allocate(sol_rt_object_type, sizeof(Header)));
    }
    fixture.Begin();
    for (auto *node : nodes)
    {
        fixture.Root(node);
    }
    const auto first = TraceBatch(fixture.State, TraceBudget{64, 11}, false);
    if (first.Objects != 11 || first.ReferenceSlots)
    {
        return 236;
    }
    fixture.Drain(TraceBudget{64, 11});
    const auto snapshot = fixture.State.Cost.Snapshot();
    if (Counter(snapshot, GcCounter::MarkObjects) != 500
        || Counter(snapshot, GcCounter::MarkMaxObjects) != 11 || !fixture.State.Work.Empty())
    {
        return 237;
    }
    return 0;
}

int AssistActualWork()
{
    TraceFixture fixture(TestHeapLimit);
    auto *array = fixture.NewArray(1000);
    if (!array)
    {
        sol_rt_fail(7);
    }
    auto *node = fixture.NewNode(100);
    const std::span elements(Detail::StorageCast<Header *>(array->Data()), Detail::Integer<std::size_t>(array->Length));
    for (unsigned index = 0; index < 1000; ++index)
    {
        Detail::CheckedAt(elements, index) = &node->Object;
    }
    fixture.Begin();
    fixture.Root(&array->Object);
    CurrentThread->Debt.Charge(fixture.State.Epoch, 8192);
    AssistAllocation(fixture.State);
    auto snapshot = fixture.State.Cost.Snapshot();
    if (Counter(snapshot, GcCounter::AssistBatches) != 1
        || Counter(snapshot, GcCounter::AssistReferenceSlots) != 16
        || Counter(snapshot, GcCounter::AssistDebtPaid) != 128)
    {
        return 238;
    }
    fixture.State.Threshold = 1;
    AssistAllocation(fixture.State);
    snapshot = fixture.State.Cost.Snapshot();
    if (Counter(snapshot, GcCounter::AssistBatches) != 2
        || Counter(snapshot, GcCounter::AssistReferenceSlots) != 18
        || Counter(snapshot, GcCounter::AssistObjects) != 1)
    {
        return 239;
    }
    // The newly discovered child region precedes the requeued partial array in the FIFO.
    // After completing that child, a pressure assist resumes the array for its full 64 slots.
    AssistAllocation(fixture.State);
    snapshot = fixture.State.Cost.Snapshot();
    if (Counter(snapshot, GcCounter::AssistBatches) != 3
        || Counter(snapshot, GcCounter::AssistReferenceSlots) != 82)
    {
        return 239;
    }
    fixture.Drain();
    if (!fixture.Marked(&node->Object))
    {
        return 240;
    }
    return 0;
}

int RandomReachability()
{
    for (unsigned seed = 0; seed < 24; ++seed)
    {
        TraceFixture fixture(TestHeapLimit);
        std::mt19937 random(seed + 0xB17U);
        std::vector<Node *> nodes;
        std::array<std::array<int, 2>, 180> edges{};
        for (unsigned index = 0; index < edges.size(); ++index)
        {
            nodes.push_back(fixture.NewNode(index));
        }
        for (unsigned index = 0; index < nodes.size(); ++index)
        {
            for (unsigned field = 0; field < 2; ++field)
            {
                const int target = random() % 5 == 0 ? -1 : Detail::Integer<int>(random() % nodes.size());
                edges.at(index).at(field) = target;
                auto *value = target < 0 ? nullptr : &nodes.at(Detail::Integer<unsigned>(target))->Object;
                (field ? nodes.at(index)->Right : nodes.at(index)->Left) = value;
            }
        }
        std::array<bool, 180> reachable{};
        std::vector<unsigned> pending{0, 17};
        while (!pending.empty())
        {
            const auto current = pending.back();
            pending.pop_back();
            if (reachable.at(current))
            {
                continue;
            }
            reachable.at(current) = true;
            for (const auto target : edges.at(current))
            {
                if (target >= 0)
                {
                    pending.push_back(Detail::Integer<unsigned>(target));
                }
            }
        }
        fixture.Begin();
        fixture.Root(&nodes.at(0)->Object);
        fixture.Root(&nodes.at(17)->Object);
        fixture.Drain(TraceBudget{1 + seed % 9, 1 + seed % 5});
        for (unsigned index = 0; index < nodes.size(); ++index)
        {
            if (fixture.Marked(&nodes.at(index)->Object) != reachable.at(index))
            {
                return 241;
            }
        }
    }
    return 0;
}
}

int RunSpanTraceTests() noexcept(false)
{
    for (auto test : {DenseSpan, DisconnectedAndCycles, LargeArrayResumption,
                      ScalarBudget, AssistActualWork, RandomReachability})
    {
        if (const int failure = test ? test() : 7)
        {
            return failure;
        }
    }
    std::ignore = std::fprintf(
        stderr,
        "Span tracing: dense/cyclic/random oracle, scalar budget, large-array cursors and actual assist work passed.\n");
    return 0;
}
