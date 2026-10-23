// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include <array>
#include <cstdint>
#include <cstdio>
#include <string>
#include <unordered_set>

namespace
{
enum class Phase : std::uint8_t { Idle, Start, Mark, Terminate, Sweep };

struct State final
{
    Phase Current = Phase::Idle;
    Phase Loaded = Phase::Idle;
    unsigned Mutator = 0;
    unsigned Collector = 0;
    bool Running = true;
    bool InBarrier = false;
    bool Visible = false;
    bool Marked = false;
    bool Sealed = false;
};

std::string Key(const State &state)
{
    return {static_cast<char>(state.Current), static_cast<char>(state.Loaded),
            static_cast<char>(state.Mutator), static_cast<char>(state.Collector),
            state.Running ? 'r' : '-', state.InBarrier ? 'b' : '-',
            state.Visible ? 'v' : '-', state.Marked ? 'm' : '-', state.Sealed ? 's' : '-'};
}

bool Explore(const State &state, bool allowParkInBarrier, std::unordered_set<std::string> &seen)
{
    if (!seen.insert(Key(state)).second)
    {
        return true;
    }
    if (state.Sealed && state.Visible && !state.Marked)
    {
        return false;
    }
    if (state.Mutator < 5)
    {
        auto next = state;
        bool allowed = true;
        switch (state.Mutator)
        {
        case 0:
            next.Loaded = state.Current;
            // A legal reference first exposed after sealing is already retained or born black.
            // The model must not treat resurrection of an unrooted white address as a valid store.
            if (state.Current == Phase::Sweep)
            {
                next.Marked = true;
            }
            next.InBarrier = true;
            if (allowParkInBarrier)
            {
                // Fault injection: pretend the mutator parked, retaining a stale idle phase.
                next.Running = false;
            }
            break;
        case 1:
            if (state.Loaded == Phase::Start || state.Loaded == Phase::Mark || state.Loaded == Phase::Terminate)
            {
                next.Marked = true;
            }
            next.Visible = true;
            next.InBarrier = false;
            break;
        case 2:
            next.Running = false;
            break;
        case 3:
            allowed = state.Current == Phase::Mark || state.Current == Phase::Sweep;
            next.Running = true;
            break;
        case 4:
            next.Running = false;
            break;
        default:
            allowed = false;
            break;
        }
        if (allowed)
        {
            ++next.Mutator;
            if (!Explore(next, allowParkInBarrier, seen))
            {
                return false;
            }
        }
    }
    if (state.Collector < 5)
    {
        auto next = state;
        bool allowed = true;
        switch (state.Collector)
        {
        case 0:
            next.Current = Phase::Start;
            break;
        case 1:
            allowed = !state.Running;
            next.Marked = state.Marked || state.Visible;
            next.Current = Phase::Mark;
            break;
        case 2:
            next.Current = Phase::Terminate;
            break;
        case 3:
            allowed = !state.Running;
            next.Marked = state.Marked || state.Visible;
            break;
        case 4:
            next.Current = Phase::Sweep;
            next.Sealed = true;
            break;
        default:
            allowed = false;
            break;
        }
        if (allowed)
        {
            ++next.Collector;
            if (!Explore(next, allowParkInBarrier, seen))
            {
                return false;
            }
        }
    }
    return true;
}
}

int RunBarrierPhaseModel()
{
    std::unordered_set<std::string> correct;
    if (!Explore(State{}, false, correct))
    {
        return 227;
    }
    std::unordered_set<std::string> broken;
    if (Explore(State{}, true, broken))
    {
        return 228;
    }
    std::fprintf(stderr, "Barrier phase model: %zu states; stale idle phase across park counterexample detected.\n",
                 correct.size());
    return 0;
}
