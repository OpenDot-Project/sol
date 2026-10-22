// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Api.h"
#include "Support.h"
#pragma warning(push)
#pragma warning(disable: 4244 4702)
#include <lld/Common/Driver.h>
#include <lld/Common/ErrorHandler.h>
#pragma warning(pop)
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <stdexcept>
#include <vector>

LLD_HAS_DRIVER(coff)

extern "C" bool SolLldLink(const char *const *arguments, std::uint64_t count, int *exitCode,
                           void **diagnostics, std::uint64_t *size) noexcept
{
    using namespace OpenDot::Sol::LlvmNative;
    try
    {
        if (!arguments || count == 0 || count > 65536 || !exitCode || !diagnostics || !size)
        {
            throw std::invalid_argument("Invalid embedded LLD invocation.");
        }
        *diagnostics = nullptr;
        *size = 0;
        std::vector<const char *> args{"lld-link"};
        for (std::uint64_t index = 0; index < count; ++index)
        {
            if (!arguments[index])
            {
                throw std::invalid_argument("Null LLD argument.");
            }
            args.push_back(arguments[index]);
        }
        // LLD owns process-global driver state; its re-entry contract is serialized.
        static std::mutex gate;
        const std::lock_guard lock(gate);
        Initialize();
        std::string messages;
        llvm::raw_string_ostream output(messages);
        const lld::DriverDef drivers[]{{lld::WinLink, &lld::coff::link}};
        const auto result = lld::lldMain(args, output, output, drivers);
        if (!result.canRunAgain)
        {
            llvm::errs() << messages;
            llvm::errs().flush();
            // LLD explicitly forbids continued use after unrecoverable driver corruption.
            lld::exitLld(result.retCode == 0 ? 1 : result.retCode);
        }
        *exitCode = result.retCode;
        if (!messages.empty())
        {
            *diagnostics = std::malloc(messages.size());
            if (!*diagnostics)
            {
                throw std::bad_alloc();
            }
            std::memcpy(*diagnostics, messages.data(), messages.size());
            *size = messages.size();
        }
        return true;
    }
    catch (const std::exception &error)
    {
        Error = error.what();
        return false;
    }
}
