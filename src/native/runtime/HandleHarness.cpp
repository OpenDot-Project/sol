// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "GcInternal.h"
#include <atomic>
#include <cstdio>
#include <limits>
#include <thread>
#include <vector>

using namespace OpenDot::Sol::Runtime;
using namespace OpenDot::Sol::Runtime::Heap;

namespace
{
std::uint64_t Identity(Header *object)
{
    auto &state = Get();
    const std::lock_guard guard(state.Mutex);
    return state.Index.at(object)->Identity;
}

int ClosedHistory()
{
    sol_gc_attach();
    for (unsigned index = 0; index < 50000; ++index)
    {
        auto *handle = sol_gc_create_handle(nullptr, (index & 1U) != 0);
        sol_gc_release_handle(handle);
        sol_gc_release_handle(handle);
    }
    auto *first = sol_gc_alloc(&sol_rt_object_type, sizeof(Header));
    auto *second = sol_gc_alloc(&sol_rt_object_type, sizeof(Header));
    auto *a = sol_gc_create_handle(first, false);
    auto *b = sol_gc_create_handle(second, false);
    auto *middle = sol_gc_create_handle(nullptr, false);
    auto *head = sol_gc_create_handle(nullptr, false);
    // Exercise head, interior, and tail unlinking without scanning the historical table.
    sol_gc_release_handle(middle);
    sol_gc_release_handle(head);
    const auto before = sol_gc_handle_stats();
    sol_gc_collect();
    const auto after = sol_gc_handle_stats();
    if (after.StrongCount != 2 || after.WeakCount != 0 || after.ClosedCount != 50002
        || after.StrongRootVisits - before.StrongRootVisits != 4 || sol_gc_stats().ObjectCount != 2)
    {
        return 120;
    }
    sol_gc_release_handle(a);
    sol_gc_release_handle(b);
    sol_gc_collect();
    if (sol_gc_stats().ObjectCount != 0 || sol_gc_handle_stats().StrongCount != 0)
    {
        return 121;
    }
    sol_rt_shutdown();
    return 0;
}

int AddressReuse()
{
    sol_gc_attach();
    auto *old = sol_gc_alloc(&sol_rt_object_type, sizeof(Header));
    const auto originalIdentity = Identity(old);
    const auto originalAddress = reinterpret_cast<std::uintptr_t>(old);
    auto *weak = sol_gc_create_handle(old, true);
    sol_gc_collect();
    auto *replacement = sol_gc_alloc(&sol_rt_object_type, sizeof(Header));
    if (reinterpret_cast<std::uintptr_t>(replacement) != originalAddress
        || Identity(replacement) <= originalIdentity)
    {
        return 122;
    }
    Header *slots[2]{};
    RootFrame frame{nullptr, slots, 2, 0};
    sol_gc_push_frame(&frame);
    auto *newWeak = sol_gc_create_handle(replacement, true);
    if (!sol_gc_resolve_handle(weak, &slots[0]) || slots[0]
        || !sol_gc_resolve_handle(newWeak, &slots[1]) || slots[1] != replacement)
    {
        return 123;
    }
    sol_gc_collect();
    if (sol_gc_stats().ObjectCount != 1 || sol_gc_handle_stats().RejectedWeakTargets != 1)
    {
        return 124;
    }
    const auto lastIdentity = Identity(replacement);
    sol_gc_store_ref(&slots[1], nullptr);
    sol_gc_collect();
    if (!sol_gc_resolve_handle(newWeak, &slots[1]) || slots[1]
        || sol_gc_handle_stats().RejectedWeakTargets != 2)
    {
        return 125;
    }
    // A cleared weak record does not repeatedly probe a dead address.
    const auto lookups = sol_gc_handle_stats().WeakLookups;
    if (!sol_gc_resolve_handle(weak, &slots[0]) || sol_gc_handle_stats().WeakLookups != lookups)
    {
        return 126;
    }
    sol_gc_release_handle(weak);
    sol_gc_release_handle(newWeak);
    sol_gc_pop_frame(&frame);
    sol_rt_shutdown();
    sol_gc_attach();
    replacement = sol_gc_alloc(&sol_rt_object_type, sizeof(Header));
    if (Identity(replacement) <= lastIdentity || sol_gc_handle_stats().ClosedCount != 0)
    {
        return 127;
    }
    sol_rt_shutdown();
    return 0;
}

int LiteralsAndInvalidHandles()
{
    sol_gc_attach();
    static Header literal{&sol_rt_object_type, 1, 0};
    Header *root = nullptr;
    RootFrame frame{nullptr, &root, 1, 0};
    sol_gc_push_frame(&frame);
    auto *strong = sol_gc_create_handle(&literal, false);
    auto *weak = sol_gc_create_handle(&literal, true);
    auto *empty = sol_gc_create_handle(nullptr, true);
    sol_gc_collect();
    if (!sol_gc_resolve_handle(weak, &root) || root != &literal
        || !sol_gc_resolve_handle(empty, &root) || root)
    {
        return 128;
    }
    RuntimeHandle unknown;
    sol_gc_store_ref(&root, &literal);
    sol_gc_release_handle(&unknown);
    sol_gc_release_handle(nullptr);
    if (sol_gc_resolve_handle(&unknown, &root) || sol_gc_resolve_handle(nullptr, &root) || root != &literal)
    {
        return 129;
    }
    sol_gc_release_handle(strong);
    if (sol_gc_resolve_handle(strong, &root) || root != &literal)
    {
        return 130;
    }
    sol_gc_release_handle(weak);
    sol_gc_release_handle(empty);
    sol_gc_pop_frame(&frame);
    sol_rt_shutdown();
    return 0;
}

int ConcurrentClose()
{
    sol_gc_attach();
    std::vector<RuntimeHandle *> handles;
    for (unsigned index = 0; index < 4096; ++index)
    {
        auto *object = sol_gc_alloc(&sol_rt_object_type, sizeof(Header));
        handles.push_back(sol_gc_create_handle(object, false));
    }
    Header *root = nullptr;
    RootFrame frame{nullptr, &root, 1, 0};
    sol_gc_push_frame(&frame);
    std::atomic<bool> start = false;
    std::thread closer([&] {
        while (!start.load(std::memory_order_acquire))
        {
            std::this_thread::yield();
        }
        for (auto *handle : handles)
        {
            sol_gc_release_handle(handle);
            sol_gc_release_handle(handle);
        }
    });
    start.store(true, std::memory_order_release);
    for (unsigned index = 0; index < handles.size(); ++index)
    {
        // Closing may win, leaving the old root untouched, or resolution may publish a live object.
        if (sol_gc_resolve_handle(handles[index], &root) && (!root || root->Type != &sol_rt_object_type))
        {
            std::terminate();
        }
        if (index % 128 == 0)
        {
            sol_gc_collect();
        }
    }
    closer.join();
    sol_gc_store_ref(&root, nullptr);
    sol_gc_collect();
    const auto stats = sol_gc_handle_stats();
    if (stats.StrongCount != 0 || stats.WeakCount != 0 || stats.ClosedCount != handles.size()
        || sol_gc_stats().ObjectCount != 0)
    {
        return 131;
    }
    sol_gc_pop_frame(&frame);
    sol_rt_shutdown();
    return 0;
}
}

int RunHandleTests()
{
    IdentitySequence sequence((std::numeric_limits<std::uint64_t>::max)() - 1);
    std::uint64_t identity = 0;
    if (!sequence.TryNext(identity) || identity != (std::numeric_limits<std::uint64_t>::max)()
        || sequence.TryNext(identity) || identity != 0 || sequence.TryNext(identity))
    {
        return 132;
    }
    for (auto test : {ClosedHistory, AddressReuse, LiteralsAndInvalidHandles, ConcurrentClose})
    {
        if (const int result = test())
        {
            return result;
        }
    }
    std::fprintf(
        stderr,
        "Handle inventory: closed-history work, address reuse, literals, concurrent close, identity exhaustion passed.\n");
    return 0;
}
