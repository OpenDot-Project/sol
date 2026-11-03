// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Fuzz.h"
#include <bit>
// NOLINTNEXTLINE(CppUnusedIncludeDirective): Direct std::memcpy/std::memcmp declarations for COFF records.
#include <cstring>
#include <windows.h>

namespace OpenDot::Sol::Fuzz
{
namespace
{
template <class T> T Read(std::span<const unsigned char> object, std::size_t offset)
{
    Require(offset <= object.size() && sizeof(T) <= object.size() - offset, "Truncated fuzz COFF record.");
    T result{};
    std::memcpy(&result, object.subspan(offset, sizeof(T)).data(), sizeof(T));
    return result;
}

class Code final
{
public:
    explicit Code(std::span<const unsigned char> bytes)
        : _memory(VirtualAlloc(nullptr, bytes.size(), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE))
    {
        if (!_memory)
        {
            throw std::runtime_error("Cannot allocate native fuzz code.");
        }
        std::memcpy(_memory, bytes.data(), bytes.size());
        DWORD old = 0;
        if (!VirtualProtect(_memory, bytes.size(), PAGE_EXECUTE_READ, &old)
            || !FlushInstructionCache(GetCurrentProcess(), _memory, bytes.size()))
        {
            VirtualFree(_memory, 0, MEM_RELEASE);
            throw std::runtime_error("Cannot publish native fuzz code.");
        }
    }

    ~Code() noexcept
    {
        VirtualFree(_memory, 0, MEM_RELEASE);
    }

    Code(const Code &) = delete;
    Code &operator=(const Code &) = delete;
    Code(Code &&) = delete;
    Code &operator=(Code &&) = delete;

    int Invoke(std::size_t offset) const
    {
        const auto address = std::span{static_cast<unsigned char *>(_memory), offset + 1}.subspan(offset).data();
        // The validated relocation-free wmain section has exactly this Windows x64 signature.
        // NOLINTNEXTLINE(bugprone-bitwise-pointer-cast)
        const auto entry = std::bit_cast<int (*)(int, wchar_t **)>(address);
        Require(entry != nullptr, "Missing native fuzz entry.");
        return entry(0, nullptr);
    }

private:
    void *_memory;
};
}

int ExecuteConstantCoff(std::span<const unsigned char> object)
{
    // These immediate-operation cases contain closed constant expressions. O1 folds the entire
    // entry to a relocation-free leaf. Execute it in the test process, outside the LLVM DLL.
    // General COFF loading, EH, external relocations and source-program execution are not modeled.
    const auto header = Read<IMAGE_FILE_HEADER>(object, 0);
    Require(header.Machine == IMAGE_FILE_MACHINE_AMD64 && !header.SizeOfOptionalHeader
            && header.NumberOfSections > 0 && header.NumberOfSections <= 128
            && header.NumberOfSymbols <= 4096, "Unsupported fuzz COFF header.");
    for (std::size_t index = 0; index < header.NumberOfSymbols; ++index)
    {
        const auto symbol = Read<IMAGE_SYMBOL>(object, header.PointerToSymbolTable + index * sizeof(IMAGE_SYMBOL));
        if (std::memcmp(std::span{symbol.N.ShortName}.data(), "wmain\0\0\0", 8) != 0)
        {
            Require(symbol.NumberOfAuxSymbols < header.NumberOfSymbols - index,
                    "Truncated fuzz COFF auxiliary symbols.");
            index += symbol.NumberOfAuxSymbols;
            continue;
        }
        Require(symbol.SectionNumber > 0 && symbol.SectionNumber <= header.NumberOfSections,
                "Invalid fuzz COFF entry section.");
        const auto section = Read<IMAGE_SECTION_HEADER>(object, sizeof(IMAGE_FILE_HEADER)
                                                                + OpenDot::Sol::CheckedInteger<std::size_t>(
                                                                    symbol.SectionNumber - 1) * sizeof(
                                                                    IMAGE_SECTION_HEADER));
        Require((section.Characteristics & IMAGE_SCN_MEM_EXECUTE) != 0 && section.NumberOfRelocations == 0
                && section.SizeOfRawData > 0 && section.SizeOfRawData <= 65536 && symbol.Value < section.SizeOfRawData
                && section.PointerToRawData <= object.size()
                && section.SizeOfRawData <= object.size() - section.PointerToRawData,
                "The constant fuzz entry is not a bounded relocation-free native leaf.");
        const Code code{object.subspan(section.PointerToRawData, section.SizeOfRawData)};
        return code.Invoke(symbol.Value);
    }
    throw std::runtime_error("Native fuzz COFF entry was not found.");
}
}
