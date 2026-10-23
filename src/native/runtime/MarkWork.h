// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include "SolRT.h"
#include <cstdint>
#include <memory>

namespace OpenDot::Sol::Runtime::Heap
{
class ReadyWork;

/// Resident span work. All mutation is serialized by the heap mutex, including claimed-span shading.
/// Word epochs avoid clearing every span or every word during a root handshake.
class MarkRegion final
{
    struct Word final
    {
        std::uint64_t Epoch = 0;
        std::uint64_t Marked = 0;
        std::uint64_t Gray = 0;
    };

    Header *_base = nullptr;
    std::unique_ptr<Word[]> _words;
    std::uint32_t _slots = 0;
    std::uint32_t _stride = 0;
    std::uint64_t _grayEpoch = 0;
    std::uint64_t _grayWords = 0;
    MarkRegion *_next = nullptr;
    bool _queued = false;
    bool _claimed = false;
    Word &ForWrite(std::uint32_t slot, std::uint64_t epoch);
    void CheckSlot(std::uint32_t slot) const;
    friend class ReadyWork;

public:
    MarkRegion() = default;
    ~MarkRegion();
    MarkRegion(const MarkRegion &) = delete;
    MarkRegion &operator=(const MarkRegion &) = delete;
    void Initialize(Header *base, std::uint32_t slots, std::uint32_t stride);
    void SetBase(Header *base);
    void Black(std::uint32_t slot, std::uint64_t epoch);
    bool Shade(std::uint32_t slot, std::uint64_t epoch);
    bool Marked(std::uint32_t slot, std::uint64_t epoch) const;
    bool HasGray(std::uint64_t epoch) const noexcept;
    std::uint32_t FirstGray(std::uint64_t epoch) const;
    void Complete(std::uint32_t slot, std::uint64_t epoch);
    Header *Address(std::uint32_t slot) const;
    void Cancel() noexcept;
    std::uint32_t SlotCount() const noexcept;
    std::uint64_t MetadataBytes() const noexcept;
    bool Queued() const noexcept;
    bool Claimed() const noexcept;
};

/// Intrusive FIFO of spans, not individual objects. No queue allocation occurs during shading.
class ReadyWork final
{
    MarkRegion *_head = nullptr;
    MarkRegion *_tail = nullptr;
    std::uint64_t _queued = 0;
    std::uint64_t _claims = 0;
    std::uint64_t _publications = 0;
    std::uint64_t _coalesced = 0;
    std::uint64_t _peak = 0;

public:
    void Publish(MarkRegion &region);
    MarkRegion *Claim();
    void Finish(MarkRegion &region, std::uint64_t epoch);
    void CancelAll() noexcept;
    bool Empty() const noexcept;
    std::uint64_t Count() const noexcept;
    std::uint64_t Claims() const noexcept;
    std::uint64_t Publications() const noexcept;
    std::uint64_t Coalesced() const noexcept;
    std::uint64_t Peak() const noexcept;
    void ResetCounters();
};
}
