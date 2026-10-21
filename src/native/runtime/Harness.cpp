// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "SolRT.h"
#include "Gc.h"
#include <cstring>
#include <cwchar>

using namespace OpenDot::Sol::Runtime;
int RunGcTests();
int RunGcModelTests();

int wmain(int argc, wchar_t **argv)
{
    if (argc == 2 && std::wcscmp(argv[1], L"heap-pressure") == 0)
    {
        sol_gc_attach();
        sol_gc_alloc(&sol_rt_object_type, 20 * 1024 * 1024);
        return 90;
    }
    wchar_t executable[] = L"harness.exe";
    wchar_t first[] = L"Sol";
    wchar_t second[] = L"\u0130stanbul";
    wchar_t empty[] = L"";
    wchar_t *arguments[]{executable, first, second, empty};
    for (int iteration = 0; iteration < 100; ++iteration)
    {
        auto array = sol_rt_arguments(4, arguments);
        if (reinterpret_cast<std::uintptr_t>(array) % 16 != 0 || sol_rt_array_length(array) != 3 || array->Stride != 8)
        {
            return 2;
        }
        auto value = sol_rt_array_get(array, 0);
        if (value->Object.Type != &sol_rt_string_type || value->ByteLength != 3 ||
            std::memcmp(value->Data(), "Sol", 3) != 0)
        {
            return 3;
        }
        auto unicode = sol_rt_array_get(array, 1);
        const char expected[] = "\xC4\xB0stanbul";
        if (unicode->ByteLength != sizeof(expected) - 1 ||
            std::memcmp(unicode->Data(), expected, sizeof(expected) - 1) != 0)
        {
            return 4;
        }
        if (sol_rt_array_get(array, 2)->ByteLength != 0)
        {
            return 5;
        }
        sol_rt_shutdown();
    }
    struct alignas(16) Literal
    {
        String Value;
        char Bytes[4];
    } literal{};
    literal.Value.ByteLength = 4;
    std::memcpy(literal.Bytes, "a\0bc", 4);
    auto value = sol_rt_literal(&literal.Value);
    if (value->Object.Type != &sol_rt_string_type || std::memcmp(value->Data(), "a\0bc", 4) != 0)
    {
        return 6;
    }
    const int model = RunGcModelTests();
    return model ? model : RunGcTests();
}