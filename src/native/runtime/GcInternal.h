// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include "Gc.h"
#include <atomic>
#include <condition_variable>
#include <list>
#include <memory>
#include <mutex>
#include <thread>
#include <unordered_map>

namespace OpenDot::Sol::Runtime
{
struct RuntimeHandle final
{
    Header *Value;
    bool Weak;
    bool Closed = false;
};

namespace Heap
{
enum class Phase { Idle, Start, Mark, Terminate, Sweep };

enum class ThreadMode { Running, Parked, Native };

struct ThreadState final
{
    RootFrame *Top = nullptr;
    ThreadMode Mode = ThreadMode::Running;
};

struct Allocation final
{
    Header *Object;
    std::uint64_t Bytes;
    std::uint64_t Mark = 0;
    std::uint64_t Cursor = 0;
    Allocation *NextGray = nullptr;
};

struct State final
{
    std::mutex Mutex;
    std::condition_variable Changed;
    std::list<std::unique_ptr<ThreadState>> Threads;
    std::list<std::unique_ptr<Allocation>> Objects;
    std::unordered_map<Header *, Allocation *> Index;
    // Closed entries are tombstones until shutdown: stale identities cannot alias new handles.
    std::unordered_map<RuntimeHandle *, std::unique_ptr<RuntimeHandle>> Handles;
    std::thread Worker;
    Allocation *Gray = nullptr;
    Phase Current = Phase::Idle;
    std::uint64_t Epoch = 0;
    std::uint64_t Requested = 0;
    std::uint64_t Completed = 0;
    std::uint64_t Allocated = 0;
    std::uint64_t Bytes = 0;
    std::uint64_t Live = 0;
    std::uint64_t Collections = 0;
    std::uint64_t Threshold = 16 * 1024 * 1024;
    std::uint64_t Limit = 0;
    bool Stop = false;
    ~State();
};

State &Get();
extern thread_local ThreadState *CurrentThread;
void Run();
void Shade(State &state, Header *value);
void Roots(State &state);
void RequireManaged();
bool Handshake(Phase phase);
void StartWorker(State &state);
std::uint64_t ReadHeapLimit();
}
}
