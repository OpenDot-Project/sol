// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Bindings.h"
#include "../runtime/Core.h"
#include "../Checked.h"
#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <span>
#include <stdexcept>
#include <windows.h>
#include <bcrypt.h>

extern "C"
{
extern const unsigned char sol_program_payload[];
extern const std::uint64_t sol_program_payload_size;
extern const unsigned char sol_program_jit_sha256[32];
extern const unsigned char sol_program_runtime_sha256[32];
}

namespace OpenDot::Sol::OrcBootstrap
{
namespace
{
using CopyError = decltype(&SolJitCopyError);
std::atomic<std::uint64_t> Engine{0};
CopyError ReadError = nullptr;

class Root final
{
public:
    explicit Root(Runtime::Header *value) noexcept : _value(value), _frame{nullptr, &_value, 1, 0}
    {
        RuntimeApi.PushFrame(&_frame);
    }

    ~Root() noexcept
    {
        RuntimeApi.PopFrame(&_frame);
    }

    Root(const Root &) = delete;
    Root &operator=(const Root &) = delete;
    Root(Root &&) = delete;
    Root &operator=(Root &&) = delete;

private:
    Runtime::Header *_value;
    Runtime::RootFrame _frame;
};

template <class T> T Export(HMODULE module, const char *name)
{
    const auto address = GetProcAddress(module, name);
    if (!address)
    {
        throw std::runtime_error("SolJit.dll is missing a required protocol export.");
    }
    // GetProcAddress returns FARPROC; T is the exact C ABI signature of this named export.
    // NOLINTNEXTLINE(bugprone-bitwise-pointer-cast)
    return std::bit_cast<T>(address);
}

void VerifyFile(const std::filesystem::path &path, std::span<const unsigned char, 32> expected)
{
    // The CNG pseudo-handle needs no close, and Hash permits streaming without copying an entire DLL.
    BCRYPT_HASH_HANDLE hash = nullptr;
    if (BCryptCreateHash(BCRYPT_SHA256_ALG_HANDLE, &hash, nullptr, 0, nullptr, 0, 0) < 0)
    {
        throw std::runtime_error("Cannot initialize sidecar SHA-256 verification.");
    }
    struct HashOwner final
    {
        BCRYPT_HASH_HANDLE Value;

        explicit HashOwner(BCRYPT_HASH_HANDLE value) noexcept : Value(value)
        {
        }

        HashOwner(const HashOwner &) = delete;
        HashOwner &operator=(const HashOwner &) = delete;
        HashOwner(HashOwner &&) = delete;
        HashOwner &operator=(HashOwner &&) = delete;

        ~HashOwner() noexcept
        {
            BCryptDestroyHash(Value);
        }
    };
    const HashOwner owner{hash};
    std::ifstream file(path, std::ios::binary);
    if (!file)
    {
        throw std::runtime_error("A required native sidecar is missing.");
    }
    std::array<unsigned char, 4096> buffer{};
    while (file)
    {
        void *bytes = buffer.data();
        file.read(static_cast<char *>(bytes), buffer.size());
        const auto count = file.gcount();
        if (count > 0 && BCryptHashData(hash, buffer.data(), CheckedInteger<ULONG>(count), 0) < 0)
        {
            throw std::runtime_error("Cannot hash a native sidecar.");
        }
    }
    if (!file.eof())
    {
        throw std::runtime_error("Cannot read a native sidecar.");
    }
    std::array<unsigned char, 32> actual{};
    if (BCryptFinishHash(hash, actual.data(), CheckedInteger<ULONG>(actual.size()), 0) < 0
        || !std::equal(actual.begin(), actual.end(), expected.begin()))
    {
        throw std::runtime_error("Native sidecar SHA-256 mismatch; deploy the matching Sol executable and sidecars.");
    }
}

[[noreturn]] void CompilationFailure()
{
    std::array<char, 1024> error{};
    if (!ReadError || !ReadError(Engine.load(std::memory_order_acquire), error.data(), error.size()))
    {
        RuntimeApi.Fail(9);
        std::abort();
    }
    // Invoked after the LLVM DLL callback has returned, on the generated caller's runtime stack.
    auto *message = RuntimeApi.StringCreate(error.data(),
                                            CheckedInteger<int>(std::char_traits<char>::length(error.data())));
    const Root root{&message->Object};
    auto *failure = RuntimeApi.ExceptionCreate(RuntimeApi.RuntimeExceptionType, message, nullptr);
    RuntimeApi.Throw(failure);
    std::abort();
}
}

int Run(int argc, wchar_t **argv)
{
    std::array<wchar_t, 32768> location{};
    const auto length = GetModuleFileNameW(nullptr, location.data(), CheckedInteger<DWORD>(location.size()));
    if (!length || length >= location.size())
    {
        throw std::runtime_error("Cannot locate the Sol executable.");
    }
    const auto directory = std::filesystem::path(location.data()).parent_path();
    const auto jitPath = directory / L"SolJit.dll";
    VerifyFile(jitPath, sol_program_jit_sha256);
#ifndef SOL_RT_STATIC
    const auto runtimePath = directory / L"SolRT.dll";
    VerifyFile(runtimePath, sol_program_runtime_sha256);
    const auto runtime = LoadLibraryExW(runtimePath.c_str(), nullptr,
                                        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!runtime)
    {
        throw std::runtime_error("Cannot load the matching native SolRT.dll.");
    }
#else
    const HMODULE runtime = nullptr;
#endif
    // Engines, their DLL and registered code are intentionally retained until process exit.
    const auto module = LoadLibraryExW(jitPath.c_str(), nullptr,
                                       LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module)
    {
        throw std::runtime_error("Cannot load the matching plain-native SolJit.dll.");
    }
    if (Export<decltype(&SolJitProtocol)>(module, "SolJitProtocol")() != 0x534F4C4A00000001ULL)
    {
        throw std::runtime_error("SolJit protocol mismatch.");
    }
    const auto open = Export<decltype(&SolJitOpen)>(module, "SolJitOpen");
    const auto lastError = Export<decltype(&SolLlvmError)>(module, "SolLlvmError");
    ReadError = Export<CopyError>(module, "SolJitCopyError");
    if (!open || !lastError)
    {
        throw std::runtime_error("Invalid JIT API exports.");
    }
    auto symbols = Bindings(runtime);
#ifdef SOL_RT_STATIC
    constexpr std::uint32_t flags = 1;
#else
    constexpr std::uint32_t flags = 0;
#endif
    const SolJitOptions options{0x534F4C0000000002ULL, std::bit_cast<std::uint64_t>(&CompilationFailure),
                                RuntimeApi.EnterNative, RuntimeApi.LeaveNative, flags, sizeof(SolJitOptions)};
    RuntimeApi.Attach();
    RuntimeApi.EnterNative();
    std::uint64_t engine = 0, entry = 0;
    const bool opened = open(&sol_program_payload[0], sol_program_payload_size, symbols.data(), symbols.size(),
                             &options, &engine, &entry);
    RuntimeApi.LeaveNative();
    if (!opened)
    {
        throw std::runtime_error(lastError());
    }
    Engine.store(engine, std::memory_order_release);
    const auto main = std::bit_cast<int (*)(int, wchar_t **)>(entry);
    if (!main)
    {
        throw std::runtime_error("The lazy program entry is missing.");
    }
    return main(argc, argv);
}
}

int wmain(int argc, wchar_t **argv)
{
    try
    {
        return OpenDot::Sol::OrcBootstrap::Run(argc, argv);
    }
    catch (const std::exception &error)
    {
        return std::fprintf(stderr, "Sol ORC executable: %s\n", error.what()) < 0 ? 2 : 1;
    }
    catch (...)
    {
        return std::fputs("Sol: unhandled program exception.\n", stderr) < 0 ? 2 : 1;
    }
}
