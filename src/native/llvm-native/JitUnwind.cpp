// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "JitUnwind.h"
#include "../Checked.h"
#pragma warning(push)
#pragma warning(disable: 4244 4702)
#include <llvm/ExecutionEngine/JITLink/COFF.h>
#include <llvm/ADT/iterator.h>
#include <llvm/ExecutionEngine/JITLink/x86_64.h>
#include <llvm/Support/Endian.h>
#pragma warning(pop)
#include <windows.h>
#include <algorithm>
#include <array>
#include <cstdlib>
// NOLINTNEXTLINE(CppUnusedIncludeDirective): Direct standard-library contract, including MSVC intrinsics.
#include <cstring>
#include <functional>
#include <limits>
#include <iterator>
#include <map>
#include <mutex>
#include <set>
#include <span>
#include <string_view>
#include <vector>

namespace OpenDot::Sol::LlvmNative
{
namespace
{
using llvm::jitlink::LinkGraph;
using llvm::jitlink::Symbol;
using llvm::orc::ExecutorAddr;
using llvm::orc::MaterializationResponsibility;
using llvm::orc::ResourceKey;
using llvm::operator|;
using llvm::operator&;

llvm::Error Failure(const char *message)
{
    return llvm::make_error<llvm::StringError>(message, llvm::inconvertibleErrorCode());
}

struct Region final
{
    std::uint64_t Begin, End;
    std::span<const char> Bytes;
    std::uint64_t Flags;

    bool Executable() const noexcept
    {
        return (Flags & 1) != 0;
    }

    bool Unwind() const noexcept
    {
        return (Flags & 2) != 0;
    }
};

const Region *FindRegion(const std::vector<Region> &regions, std::uint64_t address, std::uint64_t bytes)
{
    const auto after = std::upper_bound(regions.begin(), regions.end(), address,
                                        [](std::uint64_t value, const Region &region) {
                                            return value < region.Begin;
                                        });
    if (after == regions.begin())
    {
        return nullptr;
    }
    const auto &region = *std::prev(after);
    return address <= region.End && bytes <= region.End - address ? &region : nullptr;
}

bool NamedSection(llvm::StringRef name, llvm::StringRef prefix)
{
    return name == prefix || (name.consume_front(prefix) && name.starts_with("$"));
}

/// Only graph-local metadata and executable ranges can be referenced by Windows unwind RVAs.
llvm::Error ValidateUnwind(const std::vector<Region> &regions, std::uint64_t base, std::uint32_t initial)
{
    std::set<std::uint32_t> visited;
    auto offset = initial;
    for (unsigned depth = 0; depth < 128; ++depth)
    {
        if (offset % 4 || !visited.insert(offset).second)
        {
            return Failure("Cyclic or unaligned Windows unwind information.");
        }
        const auto *region = FindRegion(regions, base + offset, 4);
        if (!region || !region->Unwind())
        {
            return Failure("Windows unwind information is outside .xdata.");
        }
        const auto local = CheckedInteger<std::size_t>(base + offset - region->Begin);
        std::array<unsigned char, 4> header{};
        std::memcpy(header.data(), region->Bytes.subspan(local, header.size()).data(), header.size());
        const unsigned version = header.at(0) & 7;
        const unsigned flags = header.at(0) >> 3;
        const auto codeBytes = (4U + 2U * header.at(2) + 3U) & ~3U;
        const bool chained = (flags & 4) != 0;
        if ((version != 1 && version != 2) || flags > 7 || (chained && (flags & 3)))
        {
            return Failure("Unsupported Windows unwind version or flags.");
        }
        const auto total = codeBytes + (chained ? 12U : (flags & 3) ? 4U : 0U);
        if (!FindRegion(regions, base + offset, total))
        {
            return Failure("Truncated Windows unwind information.");
        }
        if (chained)
        {
            RUNTIME_FUNCTION next{};
            std::memcpy(&next, region->Bytes.subspan(local + codeBytes, sizeof(next)).data(), sizeof(next));
            const auto *code = FindRegion(regions, base + next.BeginAddress,
                                          next.EndAddress >= next.BeginAddress
                                              ? next.EndAddress - next.BeginAddress
                                              : 0);
            if (!code || !code->Executable() || next.EndAddress <= next.BeginAddress)
            {
                return Failure("Chained unwind code range is invalid.");
            }
            offset = next.UnwindData;
            continue;
        }
        if (flags & 3)
        {
            const auto handler = llvm::support::endian::read32le(region->Bytes.subspan(local + codeBytes, 4).data());
            const auto *code = FindRegion(regions, base + handler, 1);
            if (!code || !code->Executable())
            {
                return Failure("Windows unwind handler is not graph-local executable code.");
            }
        }
        return llvm::Error::success();
    }
    return Failure("Windows unwind chain exceeds the dependency depth limit.");
}

llvm::Error PrepareGraph(LinkGraph &graph)
{
    auto *imageBase = llvm::jitlink::GetImageBaseSymbol{}(graph);
    if (imageBase)
    {
        if (!imageBase->isExternal())
        {
            return Failure("A JIT module cannot define its own COFF image base.");
        }
        graph.makeAbsolute(*imageBase, ExecutorAddr());
    }
    if (graph.findSectionByName("$__SOL_POINTERS") || graph.findSectionByName("$__SOL_STUBS"))
    {
        return Failure("A JIT module uses a reserved linker section.");
    }
    // Snapshot the edges before inserting pointer/stub blocks into the graph.
    std::vector<std::reference_wrapper<llvm::jitlink::Edge>> edges;
    for (auto &block : llvm::make_pointee_range(graph.blocks()))
    {
        for (auto &edge : block.edges())
        {
            const auto &target = edge.getTarget();
            const std::string_view kind(graph.getEdgeKindName(edge.getKind()));
            const bool personality = target.hasName() && *target.getName() == "__CxxFrameHandler3";
            bool directBranch = false;
            if (kind == "PCRel32" && !block.isZeroFill() && edge.getOffset() > 0
                && edge.getOffset() <= block.getSize())
            {
                const auto content = block.getContent();
                const auto instruction = std::span{content.data(), content.size()}
                                         .subspan(edge.getOffset() - 1, 1).front();
                directBranch = instruction == '\xE8' || instruction == '\xE9';
            }
            if (target.isExternal() && (target.isCallable() || personality || directBranch)
                && (kind == "PCRel32" || kind == "Pointer32NB"))
            {
                edges.emplace_back(edge);
            }
        }
    }
    if (edges.empty())
    {
        return llvm::Error::success();
    }
    auto &pointers = graph.createSection("$__SOL_POINTERS", llvm::orc::MemProt::Read);
    auto &stubs = graph.createSection("$__SOL_STUBS", llvm::orc::MemProt::Read | llvm::orc::MemProt::Exec);
    std::map<Symbol *, Symbol *> targets;
    for (auto wrapped : edges)
    {
        auto &edge = wrapped.get();
        auto &target = edge.getTarget();
        auto [position, inserted] = targets.try_emplace(&target, nullptr);
        if (inserted)
        {
            auto &pointer = llvm::jitlink::x86_64::createAnonymousPointer(graph, pointers, &target);
            position->second = &llvm::jitlink::x86_64::createAnonymousPointerJumpStub(graph, stubs, pointer);
        }
        edge.setTarget(*position->second);
    }
    return llvm::Error::success();
}

llvm::Error AssignImageBase(LinkGraph &graph)
{
    auto *imageBase = llvm::jitlink::GetImageBaseSymbol{}(graph);
    if (!imageBase)
    {
        return llvm::Error::success();
    }
    std::uint64_t first = std::numeric_limits<std::uint64_t>::max(), last = 0;
    for (const auto &block : llvm::make_pointee_range(graph.blocks()))
    {
        const auto address = block.getAddress().getValue();
        if (block.getSize() > std::numeric_limits<std::uint64_t>::max() - address)
        {
            return Failure("Overflow in JIT code allocation range.");
        }
        first = std::min(first, address);
        last = std::max(last, address + block.getSize());
    }
    if (!last || first > last || last - first > std::numeric_limits<std::uint32_t>::max())
    {
        return Failure("COFF JIT graph exceeds the image-relative address range.");
    }
    imageBase->getAddressable().setAddress(ExecutorAddr(first));
    return llvm::Error::success();
}

struct Registration final
{
    std::vector<RUNTIME_FUNCTION> Functions;
    ResourceKey Key = 0;
    std::uint64_t Registered = 0;
    Registration() = default;
    Registration(const Registration &) = delete;
    Registration &operator=(const Registration &) = delete;
    Registration(Registration &&) = delete;
    Registration &operator=(Registration &&) = delete;

    ~Registration()
    {
        if (Registered && !RtlDeleteFunctionTable(Functions.data()))
        {
            std::abort();
        }
    }
};

class WindowsUnwindPlugin final : public llvm::orc::LinkGraphLinkingLayer::Plugin
{
    std::shared_ptr<JitCodeLifetime> _lifetime;
    std::mutex _gate;
    std::map<MaterializationResponsibility *, std::unique_ptr<Registration>> _pending;
    std::vector<std::unique_ptr<Registration>> _live;

    llvm::Error Register(MaterializationResponsibility &responsibility, LinkGraph &graph)
    {
        std::vector<Region> regions;
        auto registration = std::make_unique<Registration>();
        for (const auto &section : graph.sections())
        {
            const bool executable = (section.getMemProt() & llvm::orc::MemProt::Exec) != llvm::orc::MemProt::None;
            for (const auto *block : section.blocks())
            {
                if (block->getSize() == 0)
                {
                    continue;
                }
                if (block->isZeroFill() && NamedSection(section.getName(), ".xdata"))
                {
                    return Failure("COFF unwind information cannot be zero-fill storage.");
                }
                const auto begin = block->getAddress().getValue();
                const auto content = block->isZeroFill() ? llvm::ArrayRef<char>{} : block->getContent();
                regions.push_back({begin, begin + block->getSize(), std::span{content.data(), content.size()},
                                   (executable ? 1U : 0U) | (NamedSection(section.getName(), ".xdata") ? 2U : 0U)});
                if (!NamedSection(section.getName(), ".pdata"))
                {
                    continue;
                }
                if (block->isZeroFill() || block->getSize() % sizeof(RUNTIME_FUNCTION))
                {
                    return Failure("Misaligned COFF .pdata record size.");
                }
                const auto count = block->getSize() / sizeof(RUNTIME_FUNCTION);
                const auto previous = registration->Functions.size();
                registration->Functions.resize(previous + CheckedInteger<std::size_t>(count));
                std::memcpy(std::span{registration->Functions}.subspan(previous).data(), block->getContent().data(),
                            CheckedInteger<std::size_t>(block->getSize()));
            }
        }
        if (registration->Functions.empty())
        {
            return llvm::Error::success();
        }
        const auto *imageBase = llvm::jitlink::GetImageBaseSymbol{}(graph);
        if (!imageBase)
        {
            return Failure("Windows unwind metadata has no COFF image base.");
        }
        const auto base = imageBase->getAddress().getValue();
        std::sort(regions.begin(), regions.end(), [](const Region &left, const Region &right) {
            return left.Begin < right.Begin;
        });
        std::sort(registration->Functions.begin(), registration->Functions.end(),
                  [](const RUNTIME_FUNCTION &left, const RUNTIME_FUNCTION &right) {
                      return left.BeginAddress < right.BeginAddress;
                  });
        std::uint32_t previous = 0;
        bool first = true;
        for (const auto &function : registration->Functions)
        {
            const auto *code = FindRegion(regions, base + function.BeginAddress,
                                          function.EndAddress >= function.BeginAddress
                                              ? function.EndAddress - function.BeginAddress
                                              : 0);
            if (!code || !code->Executable() || function.EndAddress <= function.BeginAddress
                || (!first && function.BeginAddress == previous))
            {
                return Failure("Invalid or duplicate Windows runtime function range.");
            }
            if (auto error = ValidateUnwind(regions, base, function.UnwindData))
            {
                return error;
            }
            first = false;
            previous = function.BeginAddress;
        }
        const std::lock_guard guard(_gate);
        if (_pending.contains(&responsibility))
        {
            return Failure("Duplicate JIT unwind registration.");
        }
        if (!RtlAddFunctionTable(registration->Functions.data(), CheckedInteger<DWORD>(registration->Functions.size()),
                                 base))
        {
            return Failure("Windows rejected the JIT unwind function table.");
        }
        registration->Registered = 1;
        _pending.emplace(&responsibility, std::move(registration));
        return llvm::Error::success();
    }

public:
    explicit WindowsUnwindPlugin(std::shared_ptr<JitCodeLifetime> lifetime) : _lifetime(std::move(lifetime))
    {
    }

    void modifyPassConfig(MaterializationResponsibility &responsibility, LinkGraph &,
                          llvm::jitlink::PassConfiguration &configuration) override
    {
        configuration.PostPrunePasses.push_back(PrepareGraph);
        configuration.PostAllocationPasses.push_back(AssignImageBase);
        configuration.PostFixupPasses.push_back([this, &responsibility](LinkGraph &graph) {
            return Register(responsibility, graph);
        });
    }

    llvm::Error notifyEmitted(MaterializationResponsibility &responsibility) override
    {
        return responsibility.withResourceKeyDo([&](ResourceKey key) {
            const std::lock_guard guard(_gate);
            const auto position = _pending.find(&responsibility);
            if (position != _pending.end())
            {
                position->second->Key = key;
                _live.push_back(std::move(position->second));
                _pending.erase(position);
                _lifetime->UnwindTables.fetch_add(1, std::memory_order_relaxed);
            }
        });
    }

    llvm::Error notifyFailed(MaterializationResponsibility &responsibility) override
    {
        const std::lock_guard guard(_gate);
        _pending.erase(&responsibility);
        return llvm::Error::success();
    }

    llvm::Error notifyRemovingResources(llvm::orc::JITDylib &, ResourceKey key) override
    {
        const std::lock_guard guard(_gate);
        const bool present = std::any_of(_live.begin(), _live.end(), [key](const auto &record) noexcept {
            return record->Key == key;
        });
        if (present && _lifetime->Published.load(std::memory_order_acquire))
        {
            return Failure("Published JIT code has process lifetime and cannot be unloaded.");
        }
        std::erase_if(_live, [key](const auto &record) noexcept {
            return record->Key == key;
        });
        return llvm::Error::success();
    }

    void notifyTransferringResources(llvm::orc::JITDylib &, ResourceKey destination, ResourceKey source) override
    {
        const std::lock_guard guard(_gate);
        for (const auto &record : _live)
        {
            if (record->Key == source)
            {
                record->Key = destination;
            }
        }
    }
};
}

std::shared_ptr<llvm::orc::LinkGraphLinkingLayer::Plugin> CreateWindowsUnwindPlugin(
    std::shared_ptr<JitCodeLifetime> lifetime)
{
    return std::make_shared<WindowsUnwindPlugin>(std::move(lifetime));
}
}
