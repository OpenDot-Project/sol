// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "../runtime/Core.h"
#include <tuple>
#include "../llvm-native/Api.h"
#include "../Checked.h"
#include "../runtime/CheckedAccess.h"
#include <bit>
#include <span>
#include <Windows.h>
#include <array>
#include <cerrno>
#include <cmath>
#include <cstdio>
#include <cwchar>
#include <stdexcept>
#include <string>
#include <vector>

// NOLINTNEXTLINE(bugprone-reserved-identifier, clang-diagnostic-reserved-identifier): Required Windows stack-probe ABI symbol.
extern "C" void __chkstk();

namespace OpenDot::Sol::ExecHost
{
namespace
{
struct PipeCloser
{
    HANDLE Value;

    explicit PipeCloser(HANDLE value) noexcept : Value(value)
    {
    }

    PipeCloser(const PipeCloser &) = delete;
    PipeCloser &operator=(const PipeCloser &) = delete;
    PipeCloser(PipeCloser &&) = delete;
    PipeCloser &operator=(PipeCloser &&) = delete;

    ~PipeCloser() noexcept
    {
        CloseHandle(Value);
    }
};

void ReadExact(HANDLE pipe, void *buffer, DWORD length)
{
    if (!buffer && length)
    {
        throw std::invalid_argument("Missing LLVM bitcode read buffer.");
    }
    std::span pending(static_cast<unsigned char *>(buffer), length);
    while (!pending.empty())
    {
        DWORD received = 0;
        if (!ReadFile(pipe, pending.data(), CheckedInteger<DWORD>(pending.size()), &received, nullptr)
            || received == 0 || received > pending.size())
        {
            throw std::runtime_error("Truncated LLVM bitcode pipe.");
        }
        pending = pending.subspan(received);
    }
}

std::vector<unsigned char> ReadBitcode(const wchar_t *argument)
{
    if (!argument || *argument == L'\0' || std::wcsspn(argument, L"0123456789") != std::wcslen(argument))
    {
        throw std::runtime_error("Invalid LLVM bitcode pipe handle.");
    }
    errno = 0;
    const auto number = std::wcstoull(argument, nullptr, 10);
    if (errno == ERANGE || number == 0 || number == UINTPTR_MAX)
    {
        throw std::runtime_error("Invalid LLVM bitcode pipe handle.");
    }
    const auto pipe = std::bit_cast<HANDLE>(CheckedInteger<std::uintptr_t>(number));
    if (GetFileType(pipe) != FILE_TYPE_PIPE)
    {
        throw std::runtime_error("LLVM bitcode requires an inherited pipe.");
    }
    const PipeCloser owner{pipe};
    std::array<unsigned char, 4> header{};
    ReadExact(pipe, header.data(), CheckedInteger<DWORD>(header.size()));
    static_assert(std::endian::native == std::endian::little);
    const DWORD length = std::bit_cast<DWORD>(header);
    if (length < 4 || length > 64UL * 1024ULL * 1024)
    {
        throw std::runtime_error("LLVM bitcode pipe length is outside the supported bounds.");
    }
    std::vector<unsigned char> bytes(length);
    ReadExact(pipe, bytes.data(), length);
    return bytes;
}

std::vector<SolLlvmSymbol> Bindings()
{
    std::vector<SolLlvmSymbol> symbols;
#define SOL_BIND(name) symbols.push_back({#name, std::bit_cast<std::uintptr_t>(&(name))})
    SOL_BIND(sol_rt_literal);
    SOL_BIND(sol_rt_arguments);
    SOL_BIND(sol_rt_shutdown);
    SOL_BIND(sol_rt_fail);
    SOL_BIND(sol_rt_check_abi);
    SOL_BIND(sol_rt_write_string);
    SOL_BIND(sol_rt_write_integer);
    SOL_BIND(sol_rt_write_float);
    SOL_BIND(sol_rt_newline);
    SOL_BIND(sol_rt_array_length);
    SOL_BIND(sol_rt_string_concat);
    SOL_BIND(sol_rt_string_compare);
    SOL_BIND(sol_rt_string_length);
    SOL_BIND(sol_rt_string_substring);
    SOL_BIND(sol_rt_string_index_of);
    SOL_BIND(sol_rt_string_hash);
    SOL_BIND(sol_gc_push_frame);
    SOL_BIND(sol_gc_pop_frame);
    SOL_BIND(sol_gc_store_ref);
    SOL_BIND(sol_gc_poll);
    SOL_BIND(sol_gc_collect);
    SOL_BIND(sol_rt_array_new);
    SOL_BIND(sol_rt_array_address);
    SOL_BIND(memcpy);
    SOL_BIND(memset);
    SOL_BIND(__chkstk);
#undef SOL_BIND
    symbols.push_back({"fmod", std::bit_cast<std::uintptr_t>(static_cast<double (*)(double, double)>(&std::fmod))});
    symbols.push_back({"fmodf", std::bit_cast<std::uintptr_t>(static_cast<float (*)(float, float)>(&std::fmod))});
    return symbols;
}
}

int Run(int argc, wchar_t **argv)
{
    if (argc < 4 || !argv)
    {
        throw std::runtime_error("Usage: sol-exec-host --bitcode-pipe HANDLE -- [ARG...]");
    }
    const std::span input(argv, argc);
    if (std::wcscmp(Runtime::Detail::CheckedAt(input, 1), L"--bitcode-pipe") != 0
        || std::wcscmp(Runtime::Detail::CheckedAt(input, 3), L"--") != 0)
    {
        throw std::runtime_error("Usage: sol-exec-host --bitcode-pipe HANDLE -- [ARG...]");
    }
    auto bytes = ReadBitcode(Runtime::Detail::CheckedAt(input, 2));
    std::vector<wchar_t *> arguments{Runtime::Detail::CheckedAt(input, 0)};
    for (auto *argument : input.subspan(4))
    {
        arguments.push_back(argument);
    }
    arguments.push_back(nullptr);
    auto symbols = Bindings();
    int exitCode = 0;
    if (!SolLlvmExecute(bytes.data(), bytes.size(), symbols.data(), symbols.size(),
                        CheckedInteger<int>(arguments.size() - 1), arguments.data(), &exitCode))
    {
        throw std::runtime_error(SolLlvmError());
    }
    return exitCode;
}
}

int wmain(int argc, wchar_t **argv)
{
    try
    {
        return OpenDot::Sol::ExecHost::Run(argc, argv);
    }
    catch (const std::exception &error)
    {
        std::ignore = std::fprintf(stderr, "Sol ORC host: %s\n", error.what());
        return 1;
    }
}
