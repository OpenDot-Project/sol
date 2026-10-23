// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "TraceHarnessSupport.h"
#include <atomic>
#include <chrono>
#include <cstdio>
#include <semaphore>
#include <thread>

using namespace OpenDot::Sol::Runtime;
using namespace OpenDot::Sol::Runtime::Heap;
using namespace OpenDot::Sol::Runtime::Test;

namespace
{
bool IdleDoesNotAcquireHeapMutex()
{
    sol_gc_attach();
    bool okay = true;
    {
        PublicFrame roots(1);
        auto *object = sol_gc_alloc(&NodeType, sizeof(Node));
        roots.Store(0, object);
        sol_gc_collect();
        const auto before = Snapshot();
        std::binary_semaphore locked(0);
        std::binary_semaphore finished(0);
        std::atomic<bool> completedWhileLocked{false};
        std::thread owner([&] {
            const std::lock_guard guard(Get().Mutex);
            locked.release();
            completedWhileLocked.store(finished.try_acquire_for(std::chrono::seconds(5)), std::memory_order_release);
        });
        locked.acquire();
        // This must finish before the helper releases the mutex. The timeout turns a regression
        // into a test failure instead of deadlocking the test process indefinitely.
        for (unsigned index = 0; index < 10000; ++index)
        {
            roots.Store(0, index % 2 ? object : nullptr);
        }
        finished.release();
        owner.join();
        const auto after = Snapshot();
        okay = completedWhileLocked.load(std::memory_order_acquire) && roots.Get(0) == object
               && Counter(after, GcCounter::IdleBarriers) - Counter(before, GcCounter::IdleBarriers) == 10000
               && Counter(after, GcCounter::ActiveBarriers) == Counter(before, GcCounter::ActiveBarriers);
        sol_gc_collect();
        okay = okay && sol_gc_stats().ObjectCount == 1;
    }
    sol_gc_collect();
    sol_gc_detach();
    sol_rt_shutdown();
    return okay;
}

bool StartBarrierPublishesBeforePark()
{
    sol_gc_attach();
    bool okay = true;
    {
        PublicFrame roots(2);
        for (unsigned iteration = 0; iteration < 40; ++iteration)
        {
            auto *target = sol_gc_alloc(&NodeType, sizeof(Node));
            const auto before = Snapshot();
            const auto epoch = sol_gc_request_collection();
            const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
            while (Get().Current.load(std::memory_order_acquire) != Phase::Start)
            {
                // Deliberately stay ManagedRunning. Start cannot pass this thread yet.
                if (std::chrono::steady_clock::now() >= deadline)
                {
                    okay = false;
                    break;
                }
                std::this_thread::yield();
            }
            if (!okay)
            {
                sol_gc_wait(epoch);
                break;
            }
            roots.Store(iteration % 2, target);
            const auto during = Snapshot();
            if (Counter(during, GcCounter::ActiveBarriers) != Counter(before, GcCounter::ActiveBarriers) + 1
                || Counter(during, GcCounter::NewMarks) != Counter(before, GcCounter::NewMarks) + 1)
            {
                okay = false;
            }
            sol_gc_wait(epoch);
            if (roots.Get(iteration % 2) != target || target->Type != &NodeType)
            {
                okay = false;
            }
            if (!okay)
            {
                break;
            }
        }
    }
    sol_gc_collect();
    okay = okay && sol_gc_stats().ObjectCount == 0;
    sol_gc_detach();
    sol_rt_shutdown();
    return okay;
}

bool RootChurnAcrossThreads()
{
    sol_gc_attach();
    std::atomic<unsigned> ready{0};
    std::atomic<unsigned> finished{0};
    std::atomic<bool> valid{true};
    std::array<std::thread, 4> mutators;
    for (unsigned thread = 0; thread < mutators.size(); ++thread)
    {
        mutators[thread] = std::thread([&, thread] {
            sol_gc_attach();
            {
                PublicFrame roots(5);
                ready.fetch_add(1, std::memory_order_release);
                for (unsigned index = 0; index < 2000; ++index)
                {
                    auto *node = reinterpret_cast<Node *>(sol_gc_alloc(&NodeType, sizeof(Node)));
                    node->Value = index + thread * 2000ULL;
                    roots.Store(index % 5, &node->Object);
                    auto *previous = roots.Get((index + 4) % 5);
                    sol_gc_store_ref(&node->Left, previous);
                    // No raw pointer crosses a safepoint without a registered owner.
                    sol_gc_poll();
                    const auto *rooted = reinterpret_cast<const Node *>(roots.Get(index % 5));
                    if (rooted->Value != index + thread * 2000ULL)
                    {
                        valid.store(false, std::memory_order_release);
                    }
                    if (index % 17 == 0)
                    {
                        sol_gc_collect();
                    }
                    if (index % 11 == 0)
                    {
                        roots.Store((index + 2) % 5, nullptr);
                    }
                }
            }
            sol_gc_detach();
            finished.fetch_add(1, std::memory_order_release);
        });
    }
    // Joining in native-safe mode lets the worker finish handshakes for the mutators.
    sol_gc_enter_native();
    for (auto &mutator : mutators)
    {
        mutator.join();
    }
    sol_gc_leave_native();
    sol_gc_collect();
    const auto snapshot = Snapshot();
    const bool okay = valid.load(std::memory_order_acquire) && ready == 4 && finished == 4
                      && sol_gc_stats().ObjectCount == 0 && Counter(snapshot, GcCounter::Collections) > 0
                      && Counter(snapshot, GcCounter::ActiveBarriers)
                      + Counter(snapshot, GcCounter::IdleBarriers) >= 16000;
    sol_gc_detach();
    sol_rt_shutdown();
    return okay;
}
}

int RunBarrierTests()
{
    unsigned index = 0;
    for (auto test : {IdleDoesNotAcquireHeapMutex, StartBarrierPublishesBeforePark, RootChurnAcrossThreads})
    {
        if (!test())
        {
            std::fprintf(stderr, "Barrier fixture failed: %u\n", index);
            return 232;
        }
        ++index;
    }
    std::fprintf(stderr, "Barrier: idle mutex independence, start publication, four-mutator root churn passed.\n");
    return 0;
}
