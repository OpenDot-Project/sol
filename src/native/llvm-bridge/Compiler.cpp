// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "../llvm-native/Api.h"
#include <msclr/lock.h>
#include <vcclr.h>
#include <string>
#include <vector>
using namespace System;
using namespace System::Runtime::InteropServices;
using namespace System::Text;
using namespace OpenDot::Sol::Lowering;

namespace OpenDot::Sol::LlvmBridge
{
public ref class Module sealed : ILlvmModule
{
private:
    void *_session;
    Object ^_gate = gcnew Object();

    void Check()
    {
        if (!_session)
        {
            throw gcnew ObjectDisposedException("LLVM module");
        }
    }

    static void CheckResult(bool result)
    {
        if (!result)
        {
            throw gcnew InvalidOperationException(Marshal::PtrToStringUTF8(IntPtr(const_cast<char *>(SolLlvmError()))));
        }
    }

    array<Byte> ^Output(int kind)
    {
        msclr::lock guard(_gate);
        Check();
        void *bytes = nullptr;
        std::uint64_t size = 0;
        CheckResult(SolLlvmOutput(_session, kind, &bytes, &size));
        try
        {
            if (size > Int32::MaxValue)
            {
                throw gcnew InvalidOperationException("LLVM output exceeds the managed array limit.");
            }
            auto result = gcnew array<Byte>(static_cast<int>(size));
            Marshal::Copy(IntPtr(bytes), result, 0, result->Length);
            return result;
        }
        finally
        {
            SolLlvmFree(bytes);
        }
    }

public:
    Module()
    {
        _session = SolLlvmCreate();
        CheckResult(_session != nullptr);
    }

    ~Module()
    {
        msclr::lock guard(_gate);
        this->!Module();
    }

    !Module()
    {
        auto *session = _session;
        _session = nullptr;
        if (session)
        {
            SolLlvmDestroy(session);
        }
    }

    virtual Int64 Apply(LlvmOperation operation, Int64 type, array<Int64> ^operands, Int64 data, String ^text)
    {
        ArgumentNullException::ThrowIfNull(operands, "operands");
        ArgumentNullException::ThrowIfNull(text, "text");
        msclr::lock guard(_gate);
        Check();
        auto bytes = Encoding::UTF8->GetBytes(text + "\0");
        pin_ptr<Byte> name = &bytes[0];
        pin_ptr<Int64> values = operands->Length ? &operands[0] : nullptr;
        std::int64_t result = 0;
        CheckResult(SolLlvmApply(_session, static_cast<int>(operation), type, values, operands->Length, data,
                                 reinterpret_cast<char *>(name), &result));
        return result;
    }

    virtual Int64 CreateString(array<Byte> ^utf8)
    {
        ArgumentNullException::ThrowIfNull(utf8, "utf8");
        msclr::lock guard(_gate);
        Check();
        pin_ptr<Byte> bytes = utf8->Length ? &utf8[0] : nullptr;
        std::int64_t result = 0;
        CheckResult(SolLlvmString(_session, bytes, utf8->Length, &result));
        return result;
    }

    virtual void Verify()
    {
        msclr::lock guard(_gate);
        Check();
        CheckResult(SolLlvmVerify(_session));
    }

    virtual String ^Print()
    {
        return Encoding::UTF8->GetString(Output(0));
    }

    virtual array<Byte> ^Bitcode()
    {
        return Output(1);
    }

    virtual array<Byte> ^CompileObject()
    {
        return Output(2);
    }
};

public ref class Compiler sealed : ILlvmCompiler, ILldLinker
{
public:
    virtual property String ^Version
    {
        String ^get()
        {
            return Marshal::PtrToStringUTF8(IntPtr(const_cast<char *>(SolLlvmVersion())));
        }
    }

    virtual ILlvmModule ^CreateModule()
    {
        return gcnew Module();
    }

    virtual int LinkWindows(array<String ^> ^arguments, [Out] String ^%diagnostics)
    {
        ArgumentNullException::ThrowIfNull(arguments, "arguments");
        std::vector<std::string> storage;
        for each (String ^argument in arguments)
        {
            ArgumentNullException::ThrowIfNull(argument, "argument");
            if (argument->IndexOf(L'\0') >= 0)
            {
                throw gcnew ArgumentException("LLD argument contains NUL.");
            }
            auto bytes = Encoding::UTF8->GetBytes(argument);
            if (bytes->Length == 0)
            {
                storage.emplace_back();
            }
            else
            {
                pin_ptr<Byte> value = &bytes[0];
                storage.emplace_back(reinterpret_cast<char *>(value), static_cast<std::size_t>(bytes->Length));
            }
        }
        std::vector<const char *> args;
        for (const auto &argument : storage)
        {
            args.push_back(argument.c_str());
        }
        void *messages = nullptr;
        std::uint64_t size = 0;
        int code = 1;
        try
        {
            if (!SolLldLink(args.data(), args.size(), &code, &messages, &size))
            {
                throw gcnew InvalidOperationException(
                    Marshal::PtrToStringUTF8(IntPtr(const_cast<char *>(SolLlvmError()))));
            }
            if (size > Int32::MaxValue)
            {
                throw gcnew InvalidOperationException("LLD diagnostic limit exceeded.");
            }
            diagnostics = size == 0
                              ? String::Empty
                              : Marshal::PtrToStringUTF8(IntPtr(messages), static_cast<int>(size));
            return code;
        }
        finally
        {
            SolLlvmFree(messages);
        }
    }
};
}
