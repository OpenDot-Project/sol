// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "../runtime/Core.h"
#include "../llvm-native/Api.h"
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <stdexcept>
#include <string>
#include <vector>

extern "C" void __chkstk();

namespace OpenDot::Sol::ExecHost
{
namespace
{
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
    if (argc < 3 || std::wcscmp(argv[2], L"--") != 0)
    {
        throw std::runtime_error("Usage: sol-exec-host MODULE.bc -- [ARG...]");
    }
    std::ifstream source(std::filesystem::path(argv[1]), std::ios::binary | std::ios::ate);
    if (!source || source.tellg() < 4 || source.tellg() > 64LL * 1024 * 1024)
    {
        throw std::runtime_error("Cannot read the bounded LLVM bitcode module.");
    }
    const auto length = static_cast<std::size_t>(source.tellg());
    std::vector<unsigned char> bytes(length);
    source.seekg(0);
    source.read(reinterpret_cast<char *>(bytes.data()), static_cast<std::streamsize>(length));
    if (!source)
    {
        throw std::runtime_error("LLVM bitcode read failed.");
    }
    std::vector<wchar_t *> arguments{argv[0]};
    for (int index = 3; index < argc; ++index)
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
