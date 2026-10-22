// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include <array>
#include <cstdint>
#include <cstdio>
#include <string>
#include <unordered_set>

namespace
{
// Two mutators move a reference from right to left, then erase the old edge.
// The marker visits one field per step; roots remain stable through the modeled cycle.
struct State final
{
    std::array<std::array<int, 2>, 8> Edges{{{2, 3}, {4, 5}, {6, -1}, {7, -1}, {-1, -1}, {5, -1}, {-1, -1}, {-1, -1}}};
    std::array<unsigned, 2> Mutators{};
    std::array<unsigned, 8> Cursor{};
    std::uint32_t Marked = 3;
    std::uint32_t Gray = 3;
    bool Sealed = false;
};

void Shade(State &state, int object)
{
    if (object < 0)
    {
        return;
    }
    const auto bit = 1U << object;
    if ((state.Marked & bit) == 0)
    {
        state.Marked |= bit;
        state.Gray |= bit;
    }
}

bool ReachableIsMarked(const State &state)
{
    std::uint32_t reachable = 3;
    for (unsigned pass = 0; pass < 8; ++pass)
    {
        for (unsigned owner = 0; owner < 8; ++owner)
        {
            if ((reachable & (1U << owner)) == 0)
            {
                continue;
            }
            for (int edge : state.Edges[owner])
            {
                if (edge >= 0)
                {
                    reachable |= 1U << edge;
                }
            }
        }
    }
    return (reachable & ~state.Marked) == 0;
}

std::string Key(const State &state)
{
    std::string key;
    for (auto edges : state.Edges)
    {
        for (int edge : edges)
        {
            key.push_back(static_cast<char>(edge + 1));
        }
    }
    for (auto step : state.Mutators)
    {
        key.push_back(static_cast<char>(step));
    }
    for (auto step : state.Cursor)
    {
        key.push_back(static_cast<char>(step));
    }
    key.push_back(static_cast<char>(state.Marked));
    key.push_back(static_cast<char>(state.Gray));
    key.push_back(state.Sealed ? '1' : '0');
    return key;
}

bool Explore(const State &state, bool barrier, std::unordered_set<std::string> &seen)
{
    if (!seen.insert(Key(state)).second)
    {
        return true;
    }
    if (state.Sealed && !ReachableIsMarked(state))
    {
        return false;
    }
    for (unsigned mutator = 0; mutator < 2; ++mutator)
    {
        if (state.Mutators[mutator] >= 2)
        {
            continue;
        }
        State next = state;
        if (next.Mutators[mutator]++ == 0)
        {
            const auto value = next.Edges[mutator][1];
            if (barrier && !next.Sealed)
            {
                Shade(next, value);
            }
            next.Edges[mutator][0] = value;
        }
        else
        {
            next.Edges[mutator][1] = -1;
        }
        if (!Explore(next, barrier, seen))
        {
            return false;
        }
    }
    if (!state.Sealed)
    {
        State next = state;
        if (!next.Gray)
        {
            next.Sealed = true;
        }
        else
        {
            unsigned object = 0;
            while ((next.Gray & (1U << object)) == 0)
            {
                ++object;
            }
            const auto index = next.Cursor[object]++;
            Shade(next, next.Edges[object][index]);
            if (next.Cursor[object] == 2)
            {
                next.Gray &= ~(1U << object);
            }
        }
        if (!Explore(next, barrier, seen))
        {
            return false;
        }
    }
    return true;
}
}

int RunGcModelTests()
{
    std::unordered_set<std::string> states;
    if (!Explore(State{}, true, states))
    {
        return 81;
    }
    const auto count = states.size();
    states.clear();
    if (Explore(State{}, false, states))
    {
        return 82;
    }
    std::fprintf(stderr, "Barrier model: %llu reachable states; missing-barrier counterexample detected\n",
                 static_cast<unsigned long long>(count));
    return 0;
}
