// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include "GcDiagnostics.h"
#include "Gc.h"
#include <cstdint>
#include <string>
#include <vector>

namespace OpenDot::Sol::Runtime::Test
{
enum class ProfileScenario { Dense, Sparse, Wide, Churn, Roots, Concurrent };

struct ProfileOptions final
{
    ProfileScenario Scenario = ProfileScenario::Dense;
    unsigned Nodes = 10000;
    unsigned Cycles = 5;
    unsigned Roots = 32;
    unsigned Threads = 1;
    unsigned Seed = 1;
};

struct ProfileCheckpoint final
{
    std::uint64_t Cycle;
    std::uint64_t ExpectedObjects;
    std::uint64_t ActualObjects;
    std::uint64_t CommittedBytes;
    std::uint64_t LiveBytes;
    std::uint64_t Epoch;
};

/// Each allocation call is timed, including polling, assist, cache misses, and any wait.
/// Samples live in native harness memory and are not charged to the guest heap limit.
struct AllocationTimings final
{
    std::vector<std::uint64_t> Nanoseconds;
    std::uint64_t Total = 0;
    std::uint64_t Maximum = 0;
    void Record(std::uint64_t duration);
    void Merge(AllocationTimings &&other);
    std::uint64_t Percentile(unsigned percentile) const;
};

struct ProfileResult final
{
    ProfileOptions Options;
    AllocationTimings Allocations;
    std::vector<ProfileCheckpoint> Checkpoints;
    GcDiagnosticSnapshot Diagnostics;
    AllocatorStats Allocator{};
    ThreadAllocationStats Cache{};
    GcStats Heap{};
    std::vector<GcEvent> Events;
    std::uint64_t ElapsedNanoseconds = 0;
    std::uint64_t HeapLimit = 0;
    unsigned LogicalProcessors = 0;
    bool AddressSanitized = false;
};

const char *ScenarioName(ProfileScenario scenario);
bool ParseProfileOptions(int count, wchar_t **arguments, ProfileOptions &options);
bool ExecuteProfile(ProfileResult &result);
std::string SerializeProfile(const ProfileResult &result);
int RunGcProfile(int count, wchar_t **arguments);
}
