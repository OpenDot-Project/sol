// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Core.h"
#include <atomic>
#include <cstring>
#include <cstdio>
#include <random>
#include <string>
#include <thread>
#include <vector>

using namespace OpenDot::Sol::Runtime;

namespace
{
struct Node final
{
    Header Object;
    Header *Left;
    Header *Right;
    std::uint64_t Number;
};

const std::uint32_t Edges[]{offsetof(Node, Left), offsetof(Node, Right)};
const ScanShape NodeShape{Edges, 2};
const TypeDescriptor NodeType{sizeof(Node), 16, 100, 0, 0, &sol_rt_object_type, &NodeShape, nullptr, nullptr, nullptr};

class Frame final
{
public:
    Header *Slots[8]{};
    RootFrame Root{nullptr, Slots, 8, 0};

    Frame()
    {
        sol_gc_push_frame(&Root);
    }

    ~Frame()
    {
        sol_gc_pop_frame(&Root);
    }

    void Store(unsigned index, Header *value)
    {
        sol_gc_store_ref(&Slots[index], value);
    }
};

Node *NewNode()
{
    return reinterpret_cast<Node *>(sol_gc_alloc(&NodeType, sizeof(Node)));
}

int Strings()
{
    Frame roots;
    const char bytes[] = "\xC4\xB0" "a\0\xF0\x9F\x99\x82";
    auto *value = sol_rt_string_create(bytes, sizeof(bytes) - 1);
    roots.Store(0, &value->Object);
    if (sol_rt_string_length(value) != 4 || value->ByteLength != 8 || sol_rt_string_rune_at(value, 3) != 0x1F642)
    {
        return 20;
    }
    auto *piece = sol_rt_string_substring(value, 2, 2);
    roots.Store(1, &piece->Object);
    if (piece->ByteLength != 5 || piece->Data()[0] != 0 || sol_rt_string_index_of(value, piece, 0) != 2)
    {
        return 21;
    }
    auto *empty = sol_rt_string_create(nullptr, 0);
    roots.Store(2, &empty->Object);
    if (sol_rt_string_index_of(value, empty, 4) != 4 || sol_rt_string_compare(value, value) != 0)
    {
        return 22;
    }
    auto *joined = sol_rt_string_concat(piece, value);
    roots.Store(3, &joined->Object);
    sol_gc_collect();
    if (sol_rt_string_length(joined) != 6 || std::memcmp(joined->Data(), piece->Data(), 5) != 0
        || sol_rt_string_hash(value) != sol_rt_string_hash(value))
    {
        return 23;
    }
    return 0;
}

int Graph()
{
    Frame roots;
    auto *first = NewNode();
    roots.Store(0, &first->Object);
    auto *second = NewNode();
    roots.Store(1, &second->Object);
    first->Number = 42;
    second->Number = 99;
    sol_gc_store_ref(&first->Left, &second->Object);
    sol_gc_store_ref(&second->Right, &first->Object);
    auto *weak = sol_gc_create_handle(&second->Object, true);
    roots.Store(1, nullptr);
    sol_gc_collect();
    if (sol_gc_stats().ObjectCount != 2 || reinterpret_cast<Node *>(first->Left)->Number != 99)
    {
        return 30;
    }
    if (!sol_gc_resolve_handle(weak, &roots.Slots[2]) || roots.Slots[2] != &second->Object)
    {
        return 31;
    }
    auto *strong = sol_gc_create_handle(&first->Object, false);
    roots.Store(0, nullptr);
    roots.Store(2, nullptr);
    sol_gc_collect();
    if (sol_gc_stats().ObjectCount != 2)
    {
        return 32;
    }
    sol_gc_release_handle(strong);
    sol_gc_release_handle(strong);
    if (sol_gc_resolve_handle(strong, &roots.Slots[3]))
    {
        return 33;
    }
    sol_gc_collect();
    if (sol_gc_stats().ObjectCount != 0 || !sol_gc_resolve_handle(weak, &roots.Slots[4]) || roots.Slots[4])
    {
        return 34;
    }
    sol_gc_release_handle(weak);
    return 0;
}

int ArrayGraph()
{
    Frame roots;
    auto *array = sol_rt_array_create(4096, sizeof(Header *), &NodeType, &sol_rt_string_array_type);
    roots.Store(0, &array->Object);
    for (int index = 0; index < array->Length; ++index)
    {
        auto *node = NewNode();
        node->Number = static_cast<std::uint64_t>(index);
        sol_gc_store_ref(reinterpret_cast<Header **>(&array->Data()[index]), &node->Object);
    }
    sol_gc_collect();
    if (sol_gc_stats().ObjectCount != 4097)
    {
        return 40;
    }
    for (int index = 0; index < array->Length; ++index)
    {
        if (reinterpret_cast<Node *>(array->Data()[index])->Number != static_cast<std::uint64_t>(index))
        {
            return 41;
        }
    }
    return 0;
}

int ExceptionRoots()
{
    Frame roots;
    auto *message = sol_rt_string_create("original", 8);
    roots.Store(0, &message->Object);
    auto *cause = sol_rt_exception_create(&sol_rt_exception_type, message, nullptr);
    roots.Store(1, &cause->Object);
    auto *error = sol_rt_exception_create(&sol_rt_runtime_exception_type, message, cause);
    roots.Store(2, &error->Object);
    try
    {
        Frame unwind;
        unwind.Store(0, &error->Object);
        sol_rt_throw(error);
    }
    catch (const ExceptionCarrier &carrier)
    {
        auto copied = carrier;
        for (unsigned index = 0; index < 3; ++index)
        {
            roots.Store(index, nullptr);
        }
        auto *secondary = sol_rt_exception_create(&sol_rt_exception_type, error->Message, nullptr);
        roots.Store(3, &secondary->Object);
        sol_rt_exception_add_suppressed(error, secondary);
        roots.Store(3, nullptr);
        sol_gc_collect();
        if (copied.Value() != error || !sol_rt_is_instance(&error->Object, &sol_rt_exception_type)
            || sol_rt_is_instance(&cause->Object, &sol_rt_runtime_exception_type)
            || error->Cause != cause || error->Message->ByteLength != 8 || !error->Suppressed
            || reinterpret_cast<Throwable *>(error->Suppressed->Data()[0]) != secondary)
        {
            return 50;
        }
        try
        {
            throw;
        }
        catch (const ExceptionCarrier &again)
        {
            if (again.Value() != error)
            {
                return 51;
            }
        }
    }
    sol_gc_collect();
    if (sol_gc_stats().ObjectCount != 0)
    {
        return 52;
    }
    return 0;
}

int Differential()
{
    Frame roots;
    std::mt19937 random(42);
    for (unsigned cycle = 0; cycle < 40; ++cycle)
    {
        auto *head = NewNode();
        roots.Store(0, &head->Object);
        std::vector<Node *> nodes{head};
        std::vector<std::pair<unsigned, unsigned>> edges;
        for (unsigned index = 1; index < 64; ++index)
        {
            auto *node = NewNode();
            nodes.push_back(node);
            const auto parent = random() % index;
            sol_gc_store_ref(&nodes[parent]->Left, &node->Object);
            edges.emplace_back(parent, index);
        }
        std::vector<bool> expected(64);
        expected[0] = true;
        bool changed = true;
        while (changed)
        {
            changed = false;
            // The latest store replaces earlier edges from the same parent.
            for (unsigned index = 0; index < nodes.size(); ++index)
            {
                if (!expected[index])
                {
                    continue;
                }
                auto *target = nodes[index]->Left;
                for (unsigned child = 0; child < nodes.size(); ++child)
                {
                    if (target == &nodes[child]->Object && !expected[child])
                    {
                        expected[child] = true;
                        changed = true;
                    }
                }
            }
        }
        std::uint64_t count = 0;
        for (bool alive : expected)
        {
            if (alive)
            {
                ++count;
            }
        }
        sol_gc_collect();
        if (sol_gc_stats().ObjectCount != count)
        {
            return 60;
        }
        roots.Store(0, nullptr);
        sol_gc_collect();
    }
    return 0;
}

int Concurrent()
{
    Frame roots;
    std::atomic<bool> start = false, stop = false, failed = false;
    std::atomic<unsigned> ready = 0, progress = 0;
    std::vector<std::thread> workers;
    for (unsigned worker = 0; worker < 4; ++worker)
    {
        workers.emplace_back([&] {
            sol_gc_attach();
            {
                Frame frame;
                auto *head = NewNode();
                frame.Store(0, &head->Object);
                const std::string bytes(70000, 'x');
                auto *text = sol_rt_string_create(bytes.data(), static_cast<int>(bytes.size()));
                frame.Store(2, &text->Object);
                unsigned iteration = 0;
                ++ready;
                while (!start.load())
                {
                    sol_gc_poll();
                    std::this_thread::yield();
                }
                while (!stop.load())
                {
                    auto *node = NewNode();
                    frame.Store(1, &node->Object);
                    node->Number = 123;
                    sol_gc_store_ref(&head->Left, &node->Object);
                    if ((iteration++ & 7U) == 0)
                    {
                        auto *copy = sol_rt_string_concat(text, text);
                        frame.Store(3, &copy->Object);
                        if (sol_rt_string_length(copy) != 140000 || sol_rt_string_compare(text, text) != 0
                            || sol_rt_string_index_of(copy, text, 0) != 0)
                        {
                            failed = true;
                        }
                    }
                    sol_gc_poll();
                    if (reinterpret_cast<Node *>(head->Left)->Number != 123)
                    {
                        failed = true;
                    }
                    frame.Store(1, nullptr);
                    ++progress;
                }
            }
            sol_gc_detach();
        });
    }
    while (ready.load() != 4)
    {
        sol_gc_poll();
        std::this_thread::yield();
    }
    std::fprintf(stderr, "Concurrent workers ready\n");
    std::fflush(stderr);
    start = true;
    for (unsigned iteration = 0; iteration < 30; ++iteration)
    {
        sol_gc_collect();
    }
    std::fprintf(stderr, "Concurrent cycles completed: 30\n");
    stop = true;
    // Joining a mutator is a native-safe wait; its registered roots remain visible.
    sol_gc_enter_native();
    for (auto &thread : workers)
    {
        thread.join();
    }
    sol_gc_leave_native();
    sol_gc_collect();
    if (failed || !progress || sol_gc_stats().ObjectCount != 0)
    {
        return 70;
    }
    return 0;
}
}

int RunGcTests()
{
    sol_gc_attach();
    for (auto test : {Strings, Graph, ArrayGraph, ExceptionRoots, Differential, Concurrent})
    {
        std::fprintf(stderr, "Runtime test %llu\n", static_cast<unsigned long long>(sol_gc_stats().CollectionCount));
        const int result = test();
        if (result)
        {
            return result;
        }
        sol_gc_collect();
        if (sol_gc_stats().ObjectCount != 0)
        {
            return 80;
        }
    }
    sol_rt_shutdown();
    return 0;
}
