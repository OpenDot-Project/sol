// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include <array>
#include <cstdint>
#include <cstdio>
#include <string>
#include <unordered_set>
#include <vector>

namespace
{
// Producers publish gray bits while the consumer owns the span. A queued flag is a scheduling
// hint, not the sole source of truth: the resident gray bitmap survives claim/removal.
struct State final
{
    std::uint8_t Marked = 1;
    std::uint8_t Gray = 1;
    std::uint8_t Scanned = 0;
    std::array<std::uint8_t, 2> Producer{};
    bool Queued = true;
    bool Claimed = false;
    bool Scanning = false;
    bool Processed = false;
    std::uint8_t Current = 0;
    unsigned Steps = 0;
};

struct Audit final
{
    std::unordered_set<std::string> Seen;
    std::vector<std::string> Counterexample;
    std::uint64_t Terminal = 0;
    std::uint64_t ClaimedPublications = 0;
};

std::string Key(const State &state)
{
    std::string key;
    key.push_back(static_cast<char>(state.Marked));
    key.push_back(static_cast<char>(state.Gray));
    key.push_back(static_cast<char>(state.Scanned));
    for (auto step : state.Producer)
    {
        key.push_back(static_cast<char>(step));
    }
    key.push_back(state.Queued ? 'q' : '-');
    key.push_back(state.Claimed ? 'c' : '-');
    key.push_back(state.Scanning ? 's' : '-');
    key.push_back(state.Processed ? 'p' : '-');
    key.push_back(static_cast<char>(state.Current));
    return key;
}

void Publish(State &state, unsigned object)
{
    const auto bit = static_cast<std::uint8_t>(1U << object);
    if (!(state.Marked & bit))
    {
        state.Marked |= bit;
        state.Gray |= bit;
        if (!state.Claimed)
        {
            state.Queued = true;
        }
    }
}

bool Lost(const State &state)
{
    return state.Gray && !state.Queued && !state.Claimed;
}

bool Explore(const State &state, bool recheck, Audit &audit, std::vector<std::string> &path)
{
    if (!audit.Seen.insert(Key(state)).second)
    {
        return true;
    }
    if (Lost(state) || (state.Scanned & ~state.Marked))
    {
        audit.Counterexample = path;
        return false;
    }
    if (state.Steps > 50)
    {
        return false;
    }
    bool advanced = false;
    const auto visit = [&](State next, const char *action) {
        ++next.Steps;
        path.emplace_back(action);
        const bool okay = Explore(next, recheck, audit, path);
        path.pop_back();
        return okay;
    };
    for (unsigned producer = 0; producer < state.Producer.size(); ++producer)
    {
        if (state.Producer[producer] < 2)
        {
            auto next = state;
            ++next.Producer[producer];
            if (state.Producer[producer] == 0)
            {
                if (state.Claimed)
                {
                    ++audit.ClaimedPublications;
                }
                Publish(next, producer + 1);
            }
            else
            {
                // Repeat publication must neither duplicate a queue node nor lose a gray bit.
                Publish(next, producer + 1);
            }
            advanced = true;
            if (!visit(next, producer ? "producer 1 publishes" : "producer 0 publishes"))
            {
                return false;
            }
        }
    }
    if (state.Queued && !state.Claimed)
    {
        auto next = state;
        next.Queued = false;
        next.Claimed = true;
        next.Processed = false;
        advanced = true;
        if (!visit(next, "consumer claims span"))
        {
            return false;
        }
    }
    if (state.Claimed && !state.Scanning && state.Gray)
    {
        auto next = state;
        unsigned object = 0;
        while (!(state.Gray & (1U << object)))
        {
            ++object;
        }
        next.Scanning = true;
        next.Current = static_cast<std::uint8_t>(object);
        advanced = true;
        if (!visit(next, "consumer chooses a gray object"))
        {
            return false;
        }
    }
    if (state.Scanning)
    {
        auto next = state;
        const auto bit = static_cast<std::uint8_t>(1U << state.Current);
        next.Gray &= static_cast<std::uint8_t>(~bit);
        next.Scanned |= bit;
        next.Scanning = false;
        next.Processed = true;
        advanced = true;
        if (!visit(next, "consumer completes object"))
        {
            return false;
        }
    }
    if (state.Claimed && !state.Scanning && state.Processed)
    {
        auto next = state;
        next.Claimed = false;
        if (recheck && next.Gray)
        {
            next.Queued = true;
        }
        advanced = true;
        if (!visit(next, "consumer finishes and rechecks gray bits"))
        {
            return false;
        }
    }
    if (!advanced)
    {
        ++audit.Terminal;
        if (state.Gray || state.Scanned != state.Marked)
        {
            audit.Counterexample = path;
            return false;
        }
    }
    return true;
}
}

int RunReadyWorkModel()
{
    std::vector<std::string> path;
    Audit correct;
    if (!Explore(State{}, true, correct, path) || !correct.Terminal || !correct.ClaimedPublications)
    {
        return 225;
    }
    Audit broken;
    if (Explore(State{}, false, broken, path) || broken.Counterexample.empty())
    {
        return 226;
    }
    std::fprintf(
        stderr,
        "Ready work model: %zu states, %llu terminal schedules; missing-recheck counterexample (%zu steps) detected.\n",
        correct.Seen.size(), static_cast<unsigned long long>(correct.Terminal), broken.Counterexample.size());
    return 0;
}
