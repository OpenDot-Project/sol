// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "MarkWork.h"
#include <array>
#include <cstdio>
#include <random>
#include <vector>

using namespace OpenDot::Sol::Runtime;
using namespace OpenDot::Sol::Runtime::Heap;

namespace
{
int WordBoundaries()
{
    std::array<Header, 4096> objects{};
    MarkRegion region;
    region.Initialize(objects.data(), static_cast<std::uint32_t>(objects.size()), sizeof(Header));
    const std::array<unsigned, 12> slots{0, 1, 31, 32, 62, 63, 64, 65, 127, 128, 4032, 4095};
    for (auto slot : slots)
    {
        if (region.Marked(slot, 1) || !region.Shade(slot, 1) || !region.Marked(slot, 1)
            || region.Shade(slot, 1) || region.Address(slot) != &objects[slot])
        {
            return 200;
        }
    }
    for (auto slot : slots)
    {
        if (!region.HasGray(1) || region.FirstGray(1) != slot)
        {
            return 201;
        }
        region.Complete(slot, 1);
    }
    if (region.HasGray(1))
    {
        return 202;
    }
    for (auto slot : slots)
    {
        if (!region.Marked(slot, 1) || region.Marked(slot, 2))
        {
            return 203;
        }
        region.Black(slot, 2);
        if (!region.Marked(slot, 2) || region.Shade(slot, 2) || region.HasGray(2))
        {
            return 204;
        }
    }
    return 0;
}

int PartialWords()
{
    for (unsigned count : {1U, 2U, 3U, 63U, 64U, 65U, 127U, 128U, 129U, 4095U, 4096U})
    {
        std::vector<Header> objects(count);
        MarkRegion region;
        region.Initialize(objects.data(), count, sizeof(Header));
        for (unsigned slot = count; slot-- > 0;)
        {
            if (!region.Shade(slot, 11))
            {
                return 205;
            }
        }
        for (unsigned slot = 0; slot < count; ++slot)
        {
            if (region.FirstGray(11) != slot || region.Address(slot) != &objects[slot])
            {
                return 206;
            }
            region.Complete(slot, 11);
        }
        if (region.HasGray(11) || region.MetadataBytes() > 2048)
        {
            return 207;
        }
    }
    return 0;
}

int EpochReuse()
{
    std::array<Header, 129> objects{};
    MarkRegion region;
    region.Initialize(objects.data(), static_cast<unsigned>(objects.size()), sizeof(Header));
    for (std::uint64_t epoch = 1; epoch < 80; ++epoch)
    {
        for (unsigned slot = 0; slot < objects.size(); ++slot)
        {
            if (region.Marked(slot, epoch))
            {
                return 208;
            }
            if ((slot + epoch) % 3 == 0)
            {
                region.Black(slot, epoch);
            }
            else
            {
                (void)region.Shade(slot, epoch);
            }
        }
        while (region.HasGray(epoch))
        {
            const auto slot = region.FirstGray(epoch);
            if ((slot + epoch) % 3 == 0)
            {
                return 209;
            }
            region.Complete(slot, epoch);
        }
        for (unsigned slot = 0; slot < objects.size(); ++slot)
        {
            if (!region.Marked(slot, epoch))
            {
                return 210;
            }
        }
    }
    return 0;
}

int RandomBitmapOracle()
{
    std::array<Header, 257> objects{};
    MarkRegion region;
    region.Initialize(objects.data(), static_cast<unsigned>(objects.size()), sizeof(Header));
    std::mt19937 random(0x50A17U);
    for (std::uint64_t epoch = 1; epoch <= 32; ++epoch)
    {
        std::array<bool, 257> marked{}, gray{};
        for (unsigned operation = 0; operation < 3000; ++operation)
        {
            const auto slot = random() % static_cast<unsigned>(objects.size());
            if (random() % 4)
            {
                const bool changed = region.Shade(slot, epoch);
                if (changed == marked[slot])
                {
                    return 211;
                }
                if (!marked[slot])
                {
                    marked[slot] = gray[slot] = true;
                }
            }
            else if (region.HasGray(epoch))
            {
                unsigned expected = 0;
                while (expected < gray.size() && !gray[expected])
                {
                    ++expected;
                }
                if (expected == gray.size() || region.FirstGray(epoch) != expected)
                {
                    return 212;
                }
                region.Complete(expected, epoch);
                gray[expected] = false;
            }
            if (region.Marked(slot, epoch) != marked[slot])
            {
                return 213;
            }
        }
        region.Cancel();
        if (region.HasGray(epoch))
        {
            return 214;
        }
    }
    return 0;
}

int QueueCoalescing()
{
    std::array<Header, 128> objects{};
    MarkRegion region;
    region.Initialize(objects.data(), static_cast<unsigned>(objects.size()), sizeof(Header));
    ReadyWork work;
    for (unsigned slot = 0; slot < objects.size(); ++slot)
    {
        (void)region.Shade(slot, 1);
        work.Publish(region);
    }
    if (work.Count() != 1 || work.Publications() != 1 || work.Coalesced() != 127 || work.Peak() != 1)
    {
        return 215;
    }
    auto *claimed = work.Claim();
    if (claimed != &region || !region.Claimed() || region.Queued() || !work.Empty())
    {
        return 216;
    }
    for (unsigned slot = 0; slot < 64; ++slot)
    {
        region.Complete(slot, 1);
    }
    work.Finish(region, 1);
    if (work.Count() != 1 || !region.Queued())
    {
        return 217;
    }
    (void)work.Claim();
    while (region.HasGray(1))
    {
        region.Complete(region.FirstGray(1), 1);
    }
    work.Finish(region, 1);
    if (!work.Empty() || work.Claims() != 2)
    {
        return 218;
    }
    return 0;
}

int ClaimedPublication()
{
    std::array<Header, 4> objects{};
    MarkRegion region;
    region.Initialize(objects.data(), 4, sizeof(Header));
    ReadyWork work;
    (void)region.Shade(0, 1);
    work.Publish(region);
    (void)work.Claim();
    region.Complete(0, 1);
    // The marker discovers another object in its own span while that span is claimed.
    (void)region.Shade(3, 1);
    work.Publish(region);
    if (!work.Empty() || !region.Claimed())
    {
        return 219;
    }
    work.Finish(region, 1);
    if (work.Claim() != &region || region.FirstGray(1) != 3)
    {
        return 220;
    }
    region.Complete(3, 1);
    work.Finish(region, 1);
    return work.Empty() ? 0 : 221;
}

int FifoAndCancellation()
{
    std::array<Header, 3> objects{};
    std::array<MarkRegion, 3> regions;
    ReadyWork work;
    for (unsigned index = 0; index < regions.size(); ++index)
    {
        regions[index].Initialize(&objects[index], 1, 0);
        (void)regions[index].Shade(0, 9);
        work.Publish(regions[index]);
    }
    for (auto &region : regions)
    {
        if (work.Claim() != &region)
        {
            return 222;
        }
        region.Complete(0, 9);
        work.Finish(region, 9);
    }
    for (auto &region : regions)
    {
        (void)region.Shade(0, 10);
        work.Publish(region);
    }
    work.CancelAll();
    work.ResetCounters();
    for (const auto &region : regions)
    {
        if (region.HasGray(10) || region.Queued() || region.Claimed())
        {
            return 223;
        }
    }
    return work.Empty() && !work.Publications() ? 0 : 224;
}
}

int RunMarkBitmapTests()
{
    for (auto test : {WordBoundaries, PartialWords, EpochReuse, RandomBitmapOracle,
                      QueueCoalescing, ClaimedPublication, FifoAndCancellation})
    {
        if (const int failure = test())
        {
            return failure;
        }
    }
    std::fprintf(
        stderr,
        "Mark bitmap: boundary/epoch/random oracle, span coalescing, claimed recheck and cancellation passed.\n");
    return 0;
}
