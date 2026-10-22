// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "../runtime/Core.h"
#include "../llvm-native/Api.h"
#include <Windows.h>
#include <array>
#include <cerrno>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <stdexcept>
#include <string>
#include <vector>

extern "C" void __chkstk();

namespace OpenDot::Sol::ExecHost
{
namespace
{
struct PipeCloser
{
    HANDLE Value;

    ~PipeCloser() noexcept
    {
        CloseHandle(Value);
    }
};

void ReadExact(HANDLE pipe, void *buffer, DWORD length)
{
    auto *next = static_cast<unsigned char *>(buffer);
    while (length != 0)
    {
        DWORD received = 0;
        if (!ReadFile(pipe, next, length, &received, nullptr) || received == 0)
        {
            throw std::runtime_error("Truncated LLVM bitcode pipe.");
        }
        next += received;
        length -= received;
    }
}

std::vector<unsigned char> ReadBitcode(const wchar_t *argument)
{
    if (*argument == L'\0' || std::wcsspn(argument, L"0123456789") != std::wcslen(argument))
    {
        throw std::runtime_error("Invalid LLVM bitcode pipe handle.");
    }
    errno = 0;
    const auto number = std::wcstoull(argument, nullptr, 10);
    if (errno == ERANGE || number == 0 || number == UINTPTR_MAX)
    {
        throw std::runtime_error("Invalid LLVM bitcode pipe handle.");
    }
    const auto pipe = reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(number));
    if (GetFileType(pipe) != FILE_TYPE_PIPE)
    {
        throw std::runtime_error("LLVM bitcode requires an inherited pipe.");
    }
    const PipeCloser owner{pipe};
    std::array<unsigned char, 4> header{};
    ReadExact(pipe, header.data(), static_cast<DWORD>(header.size()));
    const DWORD length = static_cast<DWORD>(header[0]) | (static_cast<DWORD>(header[1]) << 8) |
                         (static_cast<DWORD>(header[2]) << 16) | (static_cast<DWORD>(header[3]) << 24);
    if (length < 4 || length > 64UL * 1024 * 1024)
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
#define SOL_BIND(name) symbols.push_back({#name, reinterpret_cast<std::uintptr_t>(&(name))})
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
    symbols.push_back({"fmod", reinterpret_cast<std::uintptr_t>(static_cast<double (*)(double, double)>(&std::fmod))});
    symbols.push_back({"fmodf", reinterpret_cast<std::uintptr_t>(static_cast<float (*)(float, float)>(&std::fmod))});
    return symbols;
}
}

int Run(int argc, wchar_t **argv)
{
    if (argc < 4 || std::wcscmp(argv[1], L"--bitcode-pipe") != 0 || std::wcscmp(argv[3], L"--") != 0)
    {
        throw std::runtime_error("Usage: sol-exec-host --bitcode-pipe HANDLE -- [ARG...]");
    }
    auto bytes = ReadBitcode(argv[2]);
    std::vector<wchar_t *> arguments{argv[0]};
    for (int index = 4; index < argc; ++index)
    {
        arguments.push_back(argv[index]);
    }
    arguments.push_back(nullptr);
    auto symbols = Bindings();
    int exitCode = 0;
    if (!SolLlvmExecute(bytes.data(), bytes.size(), symbols.data(), symbols.size(),
                        static_cast<int>(arguments.size() - 1), arguments.data(), &exitCode))
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
        std::fprintf(stderr, "Sol ORC host: %s\n", error.what());
        return 1;
    }
}
