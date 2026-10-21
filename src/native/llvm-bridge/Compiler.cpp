// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "NativeCompiler.h"
#include <vcclr.h>
#include <stdexcept>
using namespace System;
using namespace System::Runtime::InteropServices;
using namespace System::Text;

namespace OpenDot::Sol::LlvmBridge
{
public
    ref class Compiler sealed : OpenDot::Sol::Lowering::ILlvmCompiler
{
public:
    virtual property String ^Version
    {
        String ^get()
        {
            return gcnew String(NativeVersion().c_str());
        }
    }

    virtual array<Byte> ^CompileObject(String ^ir)
    {
        ArgumentNullException::ThrowIfNull(ir, "ir");
        auto bytes = Encoding::UTF8->GetBytes(ir);
        if (bytes->Length == 0)
        {
            throw gcnew ArgumentException("LLVM IR cannot be empty.", "ir");
        }
        pin_ptr<Byte> data = &bytes[0];
        try
        {
            const auto object = CompileNative(reinterpret_cast<char *>(data), bytes->Length);
            if (object.size() > Int32::MaxValue)
            {
                throw std::runtime_error("LLVM object exceeds the managed array limit.");
            }
            auto result = gcnew array<Byte>(static_cast<int>(object.size()));
            Marshal::Copy(IntPtr(const_cast<unsigned char *>(object.data())), result, 0, result->Length);
            return result;
        }
        catch (const std::exception &error)
        {
            throw gcnew InvalidOperationException(gcnew String(error.what()));
        }
    }
};
} // namespace OpenDot::Sol::LlvmBridge