// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Fuzz.h"
#include "../llvm-native/Api.h"
#include "../llvm-native/Operations.h"
#include <Windows.h>
#include <bit>
// strcmp is part of this translation unit's direct contract with the C ABI table.
// NOLINTNEXTLINE(CppUnusedIncludeDirective)
#include <cstring>
#include <filesystem>
#include <initializer_list>
#include <string>
#include <tuple>

namespace OpenDot::Sol::Fuzz
{
namespace
{
using LlvmNative::Operation;
HMODULE Library = nullptr;

struct Functions final
{
    decltype(&SolLlvmCreate) Create{};
    decltype(&SolLlvmDestroy) Destroy{};
    decltype(&SolLlvmError) Error{};
    decltype(&SolLlvmVersion) Version{};
    decltype(&SolLlvmApply) Apply{};
    decltype(&SolLlvmVerify) Verify{};
    decltype(&SolLlvmOutput) Output{};
    decltype(&SolLlvmFree) Free{};
    decltype(&SolLlvmExecute) Execute{};
} Api;

template <class T> T Function(const char *name)
{
    const auto address = GetProcAddress(Library, name);
    Require(address != nullptr, "Required LLVM native export is missing.");
    // GetProcAddress returns FARPROC; the requested export uses the exact declared C ABI signature.
    // NOLINTNEXTLINE(bugprone-bitwise-pointer-cast)
    return std::bit_cast<T>(address);
}

struct Session final
{
    void *Value = Api.Create();

    Session()
    {
        Require(Value != nullptr, "Cannot create LLVM fuzz session.");
    }

    Session(const Session &) = delete;
    Session &operator=(const Session &) = delete;

    ~Session()
    {
        Api.Destroy(Value);
    }

    std::int64_t Apply(Operation op, std::int64_t type = 0, std::initializer_list<std::int64_t> args = {},
                       std::int64_t data = 0, const char *text = "")
    {
        std::int64_t result = 0;
        if (!Api.Apply(Value, static_cast<int>(op), type, args.begin(), args.size(), data, text, &result))
        {
            throw std::runtime_error(Api.Error());
        }
        return result;
    }
};

struct Buffer final
{
    void *Bytes = nullptr;
    std::uint64_t Size = 0;

    Buffer(void *session, int kind)
    {
        if (!Api.Output(session, kind, &Bytes, &Size))
        {
            throw std::runtime_error(Api.Error());
        }
        Require(Bytes && Size >= 4 && Size < std::uint64_t{2} * 1024U * 1024U, "Unbounded/empty LLVM output.");
    }

    Buffer(const Buffer &) = delete;
    Buffer &operator=(const Buffer &) = delete;

    ~Buffer()
    {
        Api.Free(Bytes);
    }
};
}

void LoadBackend()
{
    Require(Library == nullptr, "LLVM fuzz backend already loaded.");
    const auto path = std::filesystem::absolute(L"out/native/backend/OpenDot.Sol.LlvmNative.dll");
    Library = LoadLibraryExW(path.c_str(), nullptr,
                             LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR |
                             LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    Require(Library != nullptr, "Cannot load the qualified LLVM native DLL.");
    Api.Create = Function<decltype(Api.Create)>("SolLlvmCreate");
    Api.Destroy = Function<decltype(Api.Destroy)>("SolLlvmDestroy");
    Api.Error = Function<decltype(Api.Error)>("SolLlvmError");
    Api.Version = Function<decltype(Api.Version)>("SolLlvmVersion");
    Api.Apply = Function<decltype(Api.Apply)>("SolLlvmApply");
    Api.Verify = Function<decltype(Api.Verify)>("SolLlvmVerify");
    Api.Output = Function<decltype(Api.Output)>("SolLlvmOutput");
    Api.Free = Function<decltype(Api.Free)>("SolLlvmFree");
    Api.Execute = Function<decltype(Api.Execute)>("SolLlvmExecute");
    Require(std::strcmp(Api.Version(), "23.1.3") == 0, "LLVM fuzz backend must be exactly 23.1.3.");
}

void UnloadBackend() noexcept
{
    if (Library)
    {
        std::ignore = FreeLibrary(Library);
        Library = nullptr;
        Api = {};
    }
}

void BackendCase(const Input &input)
{
    Session session;
    const auto mode = input[5];
    const std::int32_t a = static_cast<std::int32_t>(Choice(input, 0) % 65536) - 16384;
    const std::int32_t b = mode >= 10 && mode <= 12
                               ? static_cast<std::int32_t>(Choice(input, 1) % 16)
                               : 1 + static_cast<std::int32_t>(Choice(input, 1) % 128);
    const auto ua = std::bit_cast<std::uint32_t>(a), ub = static_cast<std::uint32_t>(b);
    const auto i32 = session.Apply(Operation::IntegerType, 0, {}, 32);
    const auto pointer = session.Apply(Operation::PointerType);
    const auto function = session.Apply(Operation::Function, i32, {i32, pointer}, 0, "wmain");
    const auto entry = session.Apply(Operation::Block, 0, {function}, 0, "entry");
    std::ignore = session.Apply(Operation::Position, 0, {entry});
    const auto av = session.Apply(Operation::Integer, i32, {}, 0, std::to_string(a).c_str());
    const auto bv = session.Apply(Operation::Integer, i32, {}, 0, std::to_string(b).c_str());
    std::int64_t value = av;
    std::int32_t expected = a;
    if (mode <= 12)
    {
        value = session.Apply(static_cast<Operation>(static_cast<int>(Operation::Add) + mode), 0, {av, bv});
        std::uint32_t result = 0;
        switch (mode)
        {
        case 0:
            result = ua + ub;
            break;
        case 1:
            result = ua - ub;
            break;
        case 2:
            result = ua * ub;
            break;
        case 3:
            result = std::bit_cast<std::uint32_t>(a / b);
            break;
        case 4:
            result = ua / ub;
            break;
        case 5:
            result = std::bit_cast<std::uint32_t>(a % b);
            break;
        case 6:
            result = ua % ub;
            break;
        case 7:
            result = ua & ub;
            break;
        case 8:
            result = ua | ub;
            break;
        case 9:
            result = ua ^ ub;
            break;
        case 10:
            result = ua << ub;
            break;
        case 11:
            result = ua >> ub;
            break;
        case 12:
            result = std::bit_cast<std::uint32_t>(a >> b);
            break;
        default:
            break;
        }
        expected = std::bit_cast<std::int32_t>(result);
    }
    else if (mode == 13)
    {
        const auto slot = session.Apply(Operation::Alloca, i32);
        std::ignore = session.Apply(Operation::Store, 0, {av, slot});
        value = session.Apply(Operation::Load, i32, {slot});
    }
    else if (mode == 14)
    {
        const auto yes = session.Apply(Operation::Block, 0, {function}, 0, "yes");
        const auto no = session.Apply(Operation::Block, 0, {function}, 0, "no");
        const auto merge = session.Apply(Operation::Block, 0, {function}, 0, "merge");
        const auto compare = session.Apply(Operation::ICmp, 0, {av, bv}, 2);
        std::ignore = session.Apply(Operation::ConditionalBranch, 0, {compare, yes, no});
        std::ignore = session.Apply(Operation::Position, 0, {yes});
        std::ignore = session.Apply(Operation::Branch, 0, {merge});
        std::ignore = session.Apply(Operation::Position, 0, {no});
        std::ignore = session.Apply(Operation::Branch, 0, {merge});
        std::ignore = session.Apply(Operation::Position, 0, {merge});
        value = session.Apply(Operation::Phi, i32, {av, yes, bv, no});
        expected = a < b ? a : b;
    }
    else if (mode == 15)
    {
        const auto helper = session.Apply(Operation::Function, i32, {i32, i32}, 1, "helper");
        const auto block = session.Apply(Operation::Block, 0, {helper}, 0, "body");
        std::ignore = session.Apply(Operation::Position, 0, {block});
        const auto x = session.Apply(Operation::Parameter, 0, {helper}, 0);
        const auto y = session.Apply(Operation::Parameter, 0, {helper}, 1);
        const auto sum = session.Apply(Operation::Add, 0, {x, y});
        std::ignore = session.Apply(Operation::Return, 0, {sum});
        std::ignore = session.Apply(Operation::Position, 0, {entry});
        value = session.Apply(Operation::Call, 0, {helper, av, bv});
        expected = a + b;
    }
    else if (mode == 16)
    {
        const auto i64 = session.Apply(Operation::IntegerType, 0, {}, 64);
        const auto wide = session.Apply(Operation::ZExt, i64, {av});
        value = session.Apply(Operation::Trunc, i32, {wide});
    }
    else if (mode == 17)
    {
        const auto type = session.Apply(Operation::FloatType);
        const auto left = session.Apply(Operation::Floating, type, {},
                                        std::bit_cast<std::int64_t>(static_cast<double>(a)));
        const auto right = session.Apply(Operation::Floating, type, {},
                                         std::bit_cast<std::int64_t>(static_cast<double>(b)));
        const auto sum = session.Apply(Operation::FAdd, 0, {left, right});
        const auto expectedFloat = session.Apply(Operation::Floating, type, {},
                                                 std::bit_cast<std::int64_t>(static_cast<double>(a + b)));
        const auto equal = session.Apply(Operation::FCmp, 0, {sum, expectedFloat});
        value = session.Apply(Operation::ZExt, i32, {equal});
        expected = 1;
    }
    else if (mode == 18)
    {
        Session foreign;
        const auto type = foreign.Apply(Operation::IntegerType, 0, {}, 32);
        std::int64_t ignored = 0;
        Require(!Api.Apply(session.Value, static_cast<int>(Operation::Zero), type, nullptr, 0, 0, "", &ignored),
                "LLVM bridge accepted a foreign type handle.");
    }
    std::ignore = session.Apply(Operation::Return, 0, {value});
    if (mode == 19)
    {
        const std::int64_t operands[]{av, bv};
        std::int64_t ignored = 0;
        Require(!Api.Apply(session.Value, static_cast<int>(Operation::Add), 0, operands, 2, 0, "", &ignored),
                "LLVM bridge accepted an instruction after a terminator.");
    }
    Require(Api.Verify(session.Value), "Generated LLVM module failed verification.");
    Buffer bitcode(session.Value, 1);
    const auto *bytes = static_cast<const unsigned char *>(bitcode.Bytes);
    Require(bytes[0] == 'B' && bytes[1] == 'C' && bytes[2] == 0xC0 && bytes[3] == 0xDE, "Invalid LLVM bitcode magic.");
    Buffer object(session.Value, 2);
    const auto *coff = static_cast<const unsigned char *>(object.Bytes);
    Require(coff[0] == 0x64 && coff[1] == 0x86, "LLVM output is not x64 COFF.");
    const SolLlvmSymbol symbols[]{{nullptr, 0}};
    wchar_t name[] = L"sol-fuzz";
    wchar_t *arguments[]{name};
    int actual = 0;
    if (!Api.Execute(bytes, bitcode.Size, symbols, 0, 1, arguments, &actual))
    {
        throw std::runtime_error(Api.Error());
    }
    Require(actual == expected, "Native ORC execution differs from the independent integer/branch model.");
}
}
