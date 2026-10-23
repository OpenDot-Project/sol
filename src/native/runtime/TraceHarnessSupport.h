// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include "GcInternal.h"
#include <cstring>
#include <vector>

namespace OpenDot::Sol::Runtime::Test
{
struct Node final
{
    Header Object;
    Header *Left = nullptr;
    Header *Right = nullptr;
    std::uint64_t Value = 0;
};

inline const std::uint32_t NodeOffsets[]{offsetof(Node, Left), offsetof(Node, Right)};
inline const ScanShape NodeShape{NodeOffsets, 2};
inline const TypeDescriptor NodeType{sizeof(Node), 16, 0x541, 0, 0, &sol_rt_object_type,
                                     &NodeShape, nullptr, nullptr, nullptr};
inline const std::uint32_t ElementOffset[]{0};
inline const ScanShape ElementShape{ElementOffset, 1};
inline const TypeDescriptor ArrayType{sizeof(StringArray), 16, 0x542, 2, 0,
                                      &sol_rt_object_type, &ElementShape, nullptr, nullptr, nullptr};

/// Runs the actual allocator/index/bitmap/tracer against isolated native state, without a worker.
/// It allows deterministic budgets and same-span discovery; public concurrency fixtures run separately.
class TraceFixture final
{
    Heap::ThreadState *_previous;

public:
    Heap::State State;

    TraceFixture() : _previous(Heap::CurrentThread)
    {
        State.Threads.push_back(std::make_unique<Heap::ThreadState>());
        Heap::CurrentThread = State.Threads.back().get();
        State.Limit = 64ULL * 1024 * 1024;
    }

    ~TraceFixture()
    {
        Heap::CurrentThread = _previous;
    }

    Header *Allocate(const TypeDescriptor &type, std::uint64_t requested)
    {
        const auto bytes = (requested + 15) & ~std::uint64_t{15};
        std::unique_lock guard(State.Mutex);
        const auto storage = State.Allocator.TryAllocate(bytes, State.Limit, guard);
        if (!storage.Object)
        {
            sol_rt_fail(6);
        }
        std::memset(storage.Object, 0, static_cast<std::size_t>(bytes));
        storage.Object->Type = &type;
        auto entry = std::make_unique<Heap::Allocation>();
        entry->Storage = storage;
        entry->Object = storage.Object;
        entry->Bytes = bytes;
        if (!State.ObjectIdentities.TryNext(entry->Identity))
        {
            sol_rt_fail(6);
        }
        if (storage.Owner)
        {
            entry->Tracing = &State.Allocator.Tracing(storage);
        }
        else
        {
            entry->DedicatedTracing = std::make_unique<Heap::MarkRegion>();
            entry->DedicatedTracing->Initialize(storage.Object, 1, 0);
            entry->Tracing = entry->DedicatedTracing.get();
        }
        entry->Tracing->Black(storage.Slot, State.Epoch);
        State.Index.emplace(storage.Object, entry.get());
        State.Objects.push_back(std::move(entry));
        State.Bytes += bytes;
        return storage.Object;
    }

    Node *NewNode(std::uint64_t value = 0)
    {
        auto *node = reinterpret_cast<Node *>(Allocate(NodeType, sizeof(Node)));
        node->Value = value;
        return node;
    }

    StringArray *NewArray(unsigned count)
    {
        auto *array = reinterpret_cast<StringArray *>(Allocate(ArrayType, sizeof(StringArray) + count * 8ULL));
        array->Length = static_cast<std::int32_t>(count);
        array->Stride = sizeof(Header *);
        array->Element = &NodeType;
        return array;
    }

    void Begin()
    {
        if (!State.Work.Empty())
        {
            sol_rt_fail(7);
        }
        ++State.Epoch;
        State.Current = Heap::Phase::Mark;
    }

    void Root(Header *value)
    {
        Heap::Shade(State, value);
    }

    void Drain(Heap::TraceBudget budget = {})
    {
        std::uint64_t iterations = 0;
        while (!State.Work.Empty())
        {
            (void)Heap::TraceBatch(State, budget, false);
            if (++iterations > 1000000)
            {
                sol_rt_fail(7);
            }
        }
    }

    bool Marked(Header *value) const
    {
        const auto *entry = State.Index.at(value);
        return entry->Tracing->Marked(entry->Storage.Slot, State.Epoch);
    }
};

class PublicFrame final
{
    std::vector<Header *> _slots;
    RootFrame _frame;

public:
    explicit PublicFrame(unsigned count) : _slots(count), _frame{nullptr, _slots.data(), count, 0}
    {
        sol_gc_push_frame(&_frame);
    }

    ~PublicFrame()
    {
        sol_gc_pop_frame(&_frame);
    }

    void Store(unsigned slot, Header *value)
    {
        sol_gc_store_ref(&_slots.at(slot), value);
    }

    Header *Get(unsigned slot) const
    {
        return _slots.at(slot);
    }

    Header **Slot(unsigned slot)
    {
        return &_slots.at(slot);
    }
};

inline GcDiagnosticSnapshot Snapshot()
{
    GcDiagnosticSnapshot snapshot;
    if (!sol_gc_diagnostics(&snapshot, sizeof(snapshot)))
    {
        sol_rt_fail(7);
    }
    return snapshot;
}

inline std::uint64_t Counter(const GcDiagnosticSnapshot &snapshot, GcCounter counter)
{
    return snapshot.Counters[static_cast<std::size_t>(counter)];
}
}
