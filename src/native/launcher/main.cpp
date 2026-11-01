// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include <windows.h>
#include <tuple>
#include <shellapi.h>

#include <cstdio>
#include <bit>
#include <exception>
#include <filesystem>
#include <memory>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace OpenDot::Sol::Launcher
{
namespace
{

class Handle final
{
public:
    explicit Handle(HANDLE value = nullptr) noexcept : value_(value)
    {
    }

    ~Handle() noexcept
    {
        if (value_ && value_ != INVALID_HANDLE_VALUE)
        {
            CloseHandle(value_);
        }
    }

    Handle(const Handle &) = delete;
    Handle &operator=(const Handle &) = delete;
    Handle(Handle &&) = delete;
    Handle &operator=(Handle &&) = delete;

    [[nodiscard]] HANDLE Get() const noexcept
    {
        return value_;
    }

private:
    HANDLE value_;
};

std::wstring Quote(std::wstring_view value)
{
    std::wstring result = L"\"";
    std::size_t slashes = 0;
    for (const wchar_t character : value)
    {
        if (character == L'\\')
        {
            ++slashes;
            continue;
        }
        if (character == L'"')
        {
            result.append(slashes * 2 + 1, L'\\');
        }
        else
        {
            result.append(slashes, L'\\');
        }
        slashes = 0;
        result += character;
    }
    result.append(slashes * 2, L'\\');
    result += L'"';
    return result;
}

constexpr BOOL WINAPI ControlHandler(DWORD event) noexcept
{
    // The child shares the console and handles cancellation. Keep the owning job alive until it exits.
    return event == CTRL_C_EVENT || event == CTRL_BREAK_EVENT;
}

} // namespace

int Run()
{
    constexpr DWORD PathCapacity = 32768;
    std::vector<wchar_t> path(PathCapacity);
    const DWORD length = GetModuleFileNameW(nullptr, path.data(), PathCapacity);
    if (length == 0 || length == path.size())
    {
        return 70;
    }
    const auto root = std::filesystem::path(std::wstring(path.data(), length)).parent_path().parent_path();
    const auto runtime = root / L"runtime" / L"dotnet.exe";
    const auto payload = root / L"managed" / L"OpenDot.Sol.Cli.dll";
    if (!std::filesystem::is_regular_file(runtime) || !std::filesystem::is_regular_file(payload))
    {
        std::ignore = std::fputs("solc: incomplete component; private runtime or managed payload is missing.\n",
                                 stderr);
        return 70;
    }

    int count = 0;
    std::unique_ptr<wchar_t *, decltype(&LocalFree)> arguments(CommandLineToArgvW(GetCommandLineW(), &count),
                                                               &LocalFree);
    if (!arguments || count < 1)
    {
        return 70;
    }
    std::wstring command = Quote(runtime.wstring()) + L" " + Quote(payload.wstring());
    const std::span<wchar_t *> argumentView(arguments.get(), count);
    for (const auto *argument : argumentView.subspan(1))
    {
        command += L" " + Quote(argument);
    }

    for (const auto *key : {L"DOTNET_ROOT", L"DOTNET_ROOT_X64", L"DOTNET_ADDITIONAL_DEPS", L"DOTNET_SHARED_STORE",
                            L"DOTNET_STARTUP_HOOKS", L"DOTNET_ROLL_FORWARD", L"DOTNET_ROLL_FORWARD_TO_PRERELEASE",
                            L"DOTNET_HOST_PATH", L"CORECLR_ENABLE_PROFILING", L"COR_ENABLE_PROFILING"})
    {
        SetEnvironmentVariableW(key, nullptr);
    }
    SetEnvironmentVariableW(L"DOTNET_MULTILEVEL_LOOKUP", L"0");
    SetConsoleCtrlHandler(ControlHandler, TRUE);

    Handle job(CreateJobObjectW(nullptr, nullptr));
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!job.Get() || !SetInformationJobObject(job.Get(), JobObjectExtendedLimitInformation, &limits, sizeof(limits)))
    {
        return 70;
    }
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(runtime.c_str(), command.data(), nullptr, nullptr, TRUE, CREATE_SUSPENDED, nullptr, nullptr,
                        &startup, &process))
    {
        std::ignore = std::fprintf(stderr, "solc: could not start private runtime (Windows error %lu).\n",
                                   GetLastError());
        return 70;
    }
    Handle child(process.hProcess);
    Handle thread(process.hThread);
    if (!AssignProcessToJobObject(job.Get(), child.Get()))
    {
        TerminateProcess(child.Get(), 70);
        return 70;
    }
    if (ResumeThread(thread.Get()) == MAXDWORD)
    {
        return 70;
    }
    if (WaitForSingleObject(child.Get(), INFINITE) != WAIT_OBJECT_0)
    {
        return 70;
    }
    DWORD exitCode = 70;
    if (!GetExitCodeProcess(child.Get(), &exitCode))
    {
        return 70;
    }
    static_assert(sizeof(exitCode) == sizeof(int));
    return std::bit_cast<int>(exitCode);
}

} // namespace OpenDot::Sol::Launcher

int wmain()
{
    try
    {
        return OpenDot::Sol::Launcher::Run();
    }
    catch (const std::exception &error)
    {
        std::ignore = std::fprintf(stderr, "solc: launcher failure: %s\n", error.what());
        return 70;
    }
}
