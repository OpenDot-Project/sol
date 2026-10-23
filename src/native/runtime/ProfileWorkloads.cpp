// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "GcProfile.h"
#include "TraceHarnessSupport.h"
#include <atomic>
#include <random>
#include <thread>

namespace OpenDot::Sol::Runtime::Test
{
namespace
{
Node *AllocateNode(AllocationTimings &timings, std::uint64_t value, unsigned extra = 0)
{
    const auto started = Heap::MonotonicNanoseconds();
    auto *node = reinterpret_cast<Node *>(sol_gc_alloc(&NodeType, sizeof(Node) + extra));
    timings.Record(Heap::ElapsedNanoseconds(started));
    node->Value = value;
    return node;
}

bool Checkpoint(ProfileResult &result, unsigned cycle, std::uint64_t expected)
{
    sol_gc_collect();
    const auto stats = sol_gc_stats();
    const auto allocator = sol_gc_allocator_stats();
    result.Checkpoints.push_back({cycle, expected, stats.ObjectCount, allocator.CommittedBytes,
                                  stats.LiveBytes, stats.Epoch});
    return stats.ObjectCount == expected;
}

bool Linked(ProfileResult &result, bool sparse)
{
    const auto &options = result.Options;
    for (unsigned cycle = 0; cycle < options.Cycles; ++cycle)
    {
        {
            PublicFrame roots(options.Roots);
            Node *tail = nullptr;
            for (unsigned index = 0; index < options.Nodes; ++index)
            {
                // Sparse objects use a larger slot class. The pointer fields and payload stay
                // identical, isolating address density from descriptor reference count.
                auto *node = AllocateNode(result.Allocations, index, sparse ? 2000 : 0);
                sol_gc_store_ref(&node->Left, tail ? &tail->Object : nullptr);
                roots.Store(0, &node->Object);
                tail = node;
            }
            for (unsigned root = 1; root < options.Roots; ++root)
            {
                roots.Store(root, roots.Get(0));
            }
            if (!Checkpoint(result, cycle, options.Nodes))
            {
                return false;
            }
            auto *node = reinterpret_cast<Node *>(roots.Get(0));
            for (unsigned remaining = options.Nodes; remaining; --remaining)
            {
                if (!node || node->Value != remaining - 1)
                {
                    return false;
                }
                node = reinterpret_cast<Node *>(node->Left);
            }
            if (node)
            {
                return false;
            }
        }
        if (!Checkpoint(result, cycle, 0))
        {
            return false;
        }
    }
    return true;
}

bool Wide(ProfileResult &result)
{
    const auto &options = result.Options;
    for (unsigned cycle = 0; cycle < options.Cycles; ++cycle)
    {
        {
            PublicFrame roots(options.Roots);
            const auto started = Heap::MonotonicNanoseconds();
            auto *array = reinterpret_cast<StringArray *>(sol_gc_alloc(
                &ArrayType, sizeof(StringArray) + options.Nodes * 8ULL));
            result.Allocations.Record(Heap::ElapsedNanoseconds(started));
            array->Length = static_cast<std::int32_t>(options.Nodes);
            array->Stride = 8;
            array->Element = &NodeType;
            roots.Store(0, &array->Object);
            auto **slots = reinterpret_cast<Header **>(array + 1);
            for (unsigned index = 0; index < options.Nodes; ++index)
            {
                auto *node = AllocateNode(result.Allocations, index);
                sol_gc_store_ref(&slots[index], &node->Object);
            }
            for (unsigned root = 1; root < options.Roots; ++root)
            {
                roots.Store(root, roots.Get(0));
            }
            if (!Checkpoint(result, cycle, options.Nodes + 1ULL))
            {
                return false;
            }
            for (unsigned index = 0; index < options.Nodes; ++index)
            {
                const auto *node = reinterpret_cast<const Node *>(slots[index]);
                if (!node || node->Value != index)
                {
                    return false;
                }
            }
        }
        if (!Checkpoint(result, cycle, 0))
        {
            return false;
        }
    }
    return true;
}

bool Churn(ProfileResult &result)
{
    const auto &options = result.Options;
    std::mt19937 random(options.Seed);
    for (unsigned cycle = 0; cycle < options.Cycles; ++cycle)
    {
        {
            PublicFrame roots(options.Roots);
            for (unsigned index = 0; index < options.Nodes; ++index)
            {
                // No links: exactly the non-null rolling root slots must survive a collection.
                auto *node = AllocateNode(result.Allocations, index, (random() % 32) * 16);
                roots.Store(index % options.Roots, &node->Object);
            }
            const auto expected = (std::min)(options.Roots, options.Nodes);
            if (!Checkpoint(result, cycle, expected))
            {
                return false;
            }
            for (unsigned slot = 0; slot < expected; ++slot)
            {
                const auto *node = reinterpret_cast<const Node *>(roots.Get(slot));
                if (!node || node->Value >= options.Nodes || node->Value % options.Roots != slot)
                {
                    return false;
                }
            }
        }
        if (!Checkpoint(result, cycle, 0))
        {
            return false;
        }
    }
    return true;
}

bool RootFrames(ProfileResult &result)
{
    const auto &options = result.Options;
    for (unsigned cycle = 0; cycle < options.Cycles; ++cycle)
    {
        {
            // Nested frames stress frame traversal separately from graph traversal. Nodes form
            // one chain; adding duplicate roots increases snapshot work without increasing live bytes.
            PublicFrame outer(options.Roots);
            PublicFrame inner(options.Roots);
            for (unsigned index = 0; index < options.Nodes; ++index)
            {
                auto *node = AllocateNode(result.Allocations, index);
                sol_gc_store_ref(&node->Left, inner.Get(0));
                inner.Store(0, &node->Object);
            }
            for (unsigned slot = 0; slot < options.Roots; ++slot)
            {
                outer.Store(slot, inner.Get(0));
                inner.Store(slot, inner.Get(0));
            }
            if (!Checkpoint(result, cycle, options.Nodes))
            {
                return false;
            }
        }
        if (!Checkpoint(result, cycle, 0))
        {
            return false;
        }
    }
    return true;
}

bool Concurrent(ProfileResult &result)
{
    const auto &options = result.Options;
    std::vector<AllocationTimings> timings(options.Threads);
    std::vector<std::thread> workers;
    std::atomic<bool> valid{true};
    std::atomic<unsigned> ready{0};
    std::atomic<bool> begin{false};
    for (unsigned thread = 0; thread < options.Threads; ++thread)
    {
        workers.emplace_back([&, thread] {
            sol_gc_attach();
            {
                PublicFrame roots(options.Roots);
                sol_gc_enter_native();
                ready.fetch_add(1, std::memory_order_release);
                while (!begin.load(std::memory_order_acquire))
                {
                    std::this_thread::yield();
                }
                sol_gc_leave_native();
                for (unsigned cycle = 0; cycle < options.Cycles; ++cycle)
                {
                    for (unsigned index = 0; index < options.Nodes; ++index)
                    {
                        const auto value = (static_cast<std::uint64_t>(thread) << 32) | index;
                        auto *node = AllocateNode(timings[thread], value);
                        roots.Store(index % options.Roots, &node->Object);
                        if (index % 127 == 0)
                        {
                            sol_gc_collect();
                        }
                        const auto *rooted = reinterpret_cast<const Node *>(roots.Get(index % options.Roots));
                        if (!rooted || rooted->Value != value)
                        {
                            valid.store(false, std::memory_order_release);
                        }
                    }
                }
            }
            sol_gc_detach();
        });
    }
    sol_gc_enter_native();
    while (ready.load(std::memory_order_acquire) != options.Threads)
    {
        std::this_thread::yield();
    }
    begin.store(true, std::memory_order_release);
    for (auto &worker : workers)
    {
        worker.join();
    }
    sol_gc_leave_native();
    for (auto &timing : timings)
    {
        result.Allocations.Merge(std::move(timing));
    }
    return valid.load(std::memory_order_acquire) && Checkpoint(result, options.Cycles, 0);
}
}

bool ExecuteProfile(ProfileResult &result)
{
    switch (result.Options.Scenario)
    {
    case ProfileScenario::Dense:
        return Linked(result, false);
    case ProfileScenario::Sparse:
        return Linked(result, true);
    case ProfileScenario::Wide:
        return Wide(result);
    case ProfileScenario::Churn:
        return Churn(result);
    case ProfileScenario::Roots:
        return RootFrames(result);
    case ProfileScenario::Concurrent:
        return Concurrent(result);
    }
    return false;
}
}
