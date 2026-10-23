// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "MarkWork.h"
#include <algorithm>
#include <bit>

namespace OpenDot::Sol::Runtime::Heap
{
MarkRegion::~MarkRegion()
{
    if (_queued || _claimed || _grayWords)
    {
        sol_rt_fail(7);
    }
}

void MarkRegion::Initialize(Header *base, std::uint32_t slots, std::uint32_t stride)
{
    if (_words || !slots || slots > 4096 || (slots > 1 && (!stride || stride % 16)))
    {
        sol_rt_fail(7);
    }
    _words = std::make_unique<Word[]>((slots + 63) / 64);
    _base = base;
    _slots = slots;
    _stride = stride;
}

void MarkRegion::SetBase(Header *base)
{
    if (!_words || _base || !base)
    {
        sol_rt_fail(7);
    }
    _base = base;
}

void MarkRegion::CheckSlot(std::uint32_t slot) const
{
    if (!_words || slot >= _slots)
    {
        sol_rt_fail(7);
    }
}

MarkRegion::Word &MarkRegion::ForWrite(std::uint32_t slot, std::uint64_t epoch)
{
    CheckSlot(slot);
    auto &word = _words[slot / 64];
    if (word.Epoch != epoch)
    {
        word.Epoch = epoch;
        word.Marked = 0;
        word.Gray = 0;
    }
    return word;
}

void MarkRegion::Black(std::uint32_t slot, std::uint64_t epoch)
{
    auto &word = ForWrite(slot, epoch);
    const auto bit = std::uint64_t{1} << (slot % 64);
    if (word.Gray & bit)
    {
        sol_rt_fail(7);
    }
    word.Marked |= bit;
}

bool MarkRegion::Shade(std::uint32_t slot, std::uint64_t epoch)
{
    auto &word = ForWrite(slot, epoch);
    const auto bit = std::uint64_t{1} << (slot % 64);
    if (word.Marked & bit)
    {
        return false;
    }
    if (_grayEpoch != epoch)
    {
        if (_queued || _claimed || _grayWords)
        {
            sol_rt_fail(7);
        }
        _grayEpoch = epoch;
    }
    word.Marked |= bit;
    word.Gray |= bit;
    _grayWords |= std::uint64_t{1} << (slot / 64);
    return true;
}

bool MarkRegion::Marked(std::uint32_t slot, std::uint64_t epoch) const
{
    CheckSlot(slot);
    const auto &word = _words[slot / 64];
    return word.Epoch == epoch && (word.Marked & (std::uint64_t{1} << (slot % 64))) != 0;
}

bool MarkRegion::HasGray(std::uint64_t epoch) const noexcept
{
    return _grayEpoch == epoch && _grayWords != 0;
}

std::uint32_t MarkRegion::FirstGray(std::uint64_t epoch) const
{
    if (!HasGray(epoch))
    {
        sol_rt_fail(7);
    }
    const auto index = static_cast<std::uint32_t>(std::countr_zero(_grayWords));
    const auto &word = _words[index];
    if (word.Epoch != epoch || !word.Gray)
    {
        sol_rt_fail(7);
    }
    const auto slot = index * 64 + static_cast<std::uint32_t>(std::countr_zero(word.Gray));
    CheckSlot(slot);
    return slot;
}

void MarkRegion::Complete(std::uint32_t slot, std::uint64_t epoch)
{
    CheckSlot(slot);
    auto &word = _words[slot / 64];
    const auto bit = std::uint64_t{1} << (slot % 64);
    if (_grayEpoch != epoch || word.Epoch != epoch || !(word.Gray & bit))
    {
        sol_rt_fail(7);
    }
    word.Gray &= ~bit;
    if (!word.Gray)
    {
        _grayWords &= ~(std::uint64_t{1} << (slot / 64));
    }
}

Header *MarkRegion::Address(std::uint32_t slot) const
{
    CheckSlot(slot);
    if (!_base)
    {
        sol_rt_fail(7);
    }
    return reinterpret_cast<Header *>(reinterpret_cast<char *>(_base) + static_cast<std::uint64_t>(slot) * _stride);
}

void MarkRegion::Cancel() noexcept
{
    for (std::uint32_t index = 0; index < (_slots + 63) / 64; ++index)
    {
        _words[index].Gray = 0;
    }
    _grayWords = 0;
}

std::uint32_t MarkRegion::SlotCount() const noexcept
{
    return _slots;
}

std::uint64_t MarkRegion::MetadataBytes() const noexcept
{
    return sizeof(*this) + ((_slots + 63) / 64) * sizeof(Word);
}

bool MarkRegion::Queued() const noexcept
{
    return _queued;
}

bool MarkRegion::Claimed() const noexcept
{
    return _claimed;
}

void ReadyWork::Publish(MarkRegion &region)
{
    if (region._queued || region._claimed)
    {
        ++_coalesced;
        return;
    }
    if (!region._grayWords)
    {
        sol_rt_fail(7);
    }
    region._next = nullptr;
    region._queued = true;
    if (_tail)
    {
        _tail->_next = &region;
    }
    else
    {
        _head = &region;
    }
    _tail = &region;
    ++_queued;
    ++_publications;
    _peak = (std::max)(_peak, _queued);
}

MarkRegion *ReadyWork::Claim()
{
    auto *region = _head;
    if (!region)
    {
        return nullptr;
    }
    _head = region->_next;
    if (!_head)
    {
        _tail = nullptr;
    }
    region->_next = nullptr;
    region->_queued = false;
    region->_claimed = true;
    --_queued;
    ++_claims;
    return region;
}

void ReadyWork::Finish(MarkRegion &region, std::uint64_t epoch)
{
    if (!region._claimed || region._queued)
    {
        sol_rt_fail(7);
    }
    region._claimed = false;
    // Shading while claimed does not enqueue. The recheck is required even with one marker.
    if (region.HasGray(epoch))
    {
        Publish(region);
    }
}

void ReadyWork::CancelAll() noexcept
{
    while (_head)
    {
        auto *region = _head;
        _head = region->_next;
        region->_queued = false;
        region->_next = nullptr;
        region->Cancel();
    }
    _tail = nullptr;
    _queued = 0;
}

bool ReadyWork::Empty() const noexcept
{
    return !_head;
}

std::uint64_t ReadyWork::Count() const noexcept
{
    return _queued;
}

std::uint64_t ReadyWork::Claims() const noexcept
{
    return _claims;
}

std::uint64_t ReadyWork::Publications() const noexcept
{
    return _publications;
}

std::uint64_t ReadyWork::Coalesced() const noexcept
{
    return _coalesced;
}

std::uint64_t ReadyWork::Peak() const noexcept
{
    return _peak;
}

void ReadyWork::ResetCounters()
{
    if (!Empty())
    {
        sol_rt_fail(7);
    }
    _claims = _publications = _coalesced = _peak = 0;
}
}
