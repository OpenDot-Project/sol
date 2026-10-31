// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Fuzz.h"
#include <vector>

namespace OpenDot::Sol::Fuzz
{
namespace
{
struct Node final
{
    Header Object;
    Header *Left;
    Header *Right;
    std::uint64_t Cookie;
};

const std::uint32_t Offsets[]{offsetof(Node, Left), offsetof(Node, Right)};
const ScanShape Shape{Offsets, 2};
const TypeDescriptor NodeType{sizeof(Node), 16, 0xF022, 0, 0, &sol_rt_object_type, &Shape, nullptr, nullptr, nullptr};
}

void GcCase(const Input &input, Roots &roots)
{
    const auto mode = input.at(5);
    const unsigned count = mode == 15
                               ? 64
                               : mode == 16
                               ? 1 + Choice(input, 0) % 4
                               : mode == 17
                               ? 4 + Choice(input, 0) % 44
                               : 1 + Choice(input, 0) % 48;
    std::vector<Node *> nodes;
    std::vector<std::array<int, 2>> edges;
    std::vector<RuntimeHandle *> weak;
    for (unsigned i = 0; i < count; ++i)
    {
        auto *node = Detail::StorageCast<Node>(sol_gc_alloc(&NodeType, sizeof(Node)));
        node->Cookie = 0xF0220000ULL + i;
        roots.Set(i, &node->Object);
        nodes.push_back(node);
        weak.push_back(sol_gc_create_handle(&node->Object, true));
    }
    for (unsigned i = 0; i < count; ++i)
    {
        int left = -1, right = -1;
        if (mode <= 1)
        {
            left = i + 1 < count ? OpenDot::Sol::CheckedInteger<int>(i + 1) : -1;
        }
        else if (mode <= 3)
        {
            left = OpenDot::Sol::CheckedInteger<int>((i + 1) % count);
        }
        else if (mode <= 5)
        {
            left = 2 * i + 1 < count ? OpenDot::Sol::CheckedInteger<int>(2 * i + 1) : -1;
            right = 2 * i + 2 < count ? OpenDot::Sol::CheckedInteger<int>(2 * i + 2) : -1;
        }
        else if (mode == 8)
        {
            left = OpenDot::Sol::CheckedInteger<int>(i);
        }
        else if (mode == 9)
        {
        }
        else if (mode == 14)
        {
            left = OpenDot::Sol::CheckedInteger<int>(i % 2 == 0 ? (i + 2) % count : i);
        }
        else if (mode == 17)
        {
            left = i == 0 ? 1 : i <= 2 ? 3 : -1;
            right = i == 0 ? 2 : -1;
        }
        else if (mode == 18)
        {
            left = OpenDot::Sol::CheckedInteger<int>((i + count / 2) % count);
            right = OpenDot::Sol::CheckedInteger<int>((i + 1) % count);
        }
        else
        {
            left = Choice(input, i + 1) % 4 == 0 ? -1 : OpenDot::Sol::CheckedInteger<int>(Choice(input, i + 1) % count);
            right = mode == 6 ? -1 : OpenDot::Sol::CheckedInteger<int>(Choice(input, i + 17) % count);
        }
        edges.push_back({left, right});
        sol_gc_store_ref(&nodes.at(i)->Left,
                         left < 0 ? nullptr : &nodes.at(OpenDot::Sol::CheckedInteger<unsigned>(left))->Object);
        sol_gc_store_ref(&nodes.at(i)->Right,
                         right < 0 ? nullptr : &nodes.at(OpenDot::Sol::CheckedInteger<unsigned>(right))->Object);
    }
    std::vector<unsigned> retained;
    // Finish any allocation-triggered epoch while all nodes are rooted before testing exact reachability.
    sol_gc_collect();
    roots.Clear();
    for (unsigned i = 0; i < count; ++i)
    {
        const bool keep = mode == 3
                              ? false
                              : mode == 1
                              ? i + 1 == count
                              : mode == 5
                              ? i >= count / 2
                              : mode <= 4 || mode == 17 || mode == 19
                              ? i == 0
                              : mode == 10
                              ? i % 3 == 0
                              : Choice(input, i + 33) % 5 == 0;
        if (keep)
        {
            roots.Set(i, &nodes.at(i)->Object);
            retained.push_back(i);
        }
    }
    RuntimeHandle *strong = nullptr;
    if (mode == 12)
    {
        strong = sol_gc_create_handle(&nodes.back()->Object, false);
        retained.push_back(count - 1);
    }
    std::vector<bool> reachable(count);
    while (!retained.empty())
    {
        const auto i = retained.back();
        retained.pop_back();
        if (reachable.at(i))
        {
            continue;
        }
        reachable.at(i) = true;
        for (auto target : edges.at(i))
        {
            if (target >= 0)
            {
                retained.push_back(OpenDot::Sol::CheckedInteger<unsigned>(target));
            }
        }
    }
    sol_gc_collect();
    for (unsigned i = 0; i < count; ++i)
    {
        const bool handleValid = sol_gc_resolve_handle(weak.at(i), &roots.At(127));
        const bool alive = roots.At(127) != nullptr;
        Require(handleValid, "Weak handle became invalid before release.");
        Require(alive == reachable.at(i), "GC differs from independent graph reachability oracle.");
        if (alive)
        {
            Require(Detail::StorageCast<Node>(roots.At(127))->Cookie == 0xF0220000ULL + i,
                    "GC survivor payload changed.");
        }
        roots.Set(127, nullptr);
    }
    roots.Clear();
    if (strong)
    {
        sol_gc_release_handle(strong);
    }
    if (mode == 19)
    {
        unsigned selected = 0;
        for (unsigned i = 0; i < count; ++i)
        {
            if (reachable.at(i))
            {
                selected = i;
            }
        }
        Require(sol_gc_resolve_handle(weak.at(selected), &roots.At(127)) && roots.At(127),
                "Rotation target was not live.");
        roots.Set(0, roots.At(127));
        roots.Set(127, nullptr);
        reachable.assign(count, false);
        retained.push_back(selected);
        while (!retained.empty())
        {
            const auto i = retained.back();
            retained.pop_back();
            if (reachable.at(i))
            {
                continue;
            }
            reachable.at(i) = true;
            for (auto target : edges.at(i))
            {
                if (target >= 0)
                {
                    retained.push_back(OpenDot::Sol::CheckedInteger<unsigned>(target));
                }
            }
        }
        sol_gc_collect();
        for (unsigned i = 0; i < count; ++i)
        {
            const bool handleValid = sol_gc_resolve_handle(weak.at(i), &roots.At(127));
            Require(handleValid && (roots.At(127) != nullptr) == reachable.at(i),
                    "Root rotation reachability mismatch.");
            roots.Set(127, nullptr);
        }
        roots.Clear();
    }
    sol_gc_collect();
    for (auto *handle : weak)
    {
        const bool handleValid = sol_gc_resolve_handle(handle, &roots.At(127));
        Require(handleValid && roots.At(127) == nullptr, "Unrooted object survived a full collection.");
        roots.Set(127, nullptr);
        sol_gc_release_handle(handle);
    }
}
}
