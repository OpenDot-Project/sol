// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Session.h"
#include "../Checked.h"
#include <stdexcept>

namespace OpenDot::Sol::LlvmNative
{
namespace
{
void Require(bool condition, const char *message)
{
    if (!condition)
    {
        throw std::invalid_argument(message);
    }
}
}

std::int64_t Session::ApplyEh(Operation op, const std::vector<std::int64_t> &args, std::int64_t data)
{
    if (op == Operation::Personality)
    {
        Require(args.size() == 2, "Invalid personality arity.");
        auto *function = llvm::dyn_cast<llvm::Function>(Value(args.at(0)));
        auto *personality = llvm::dyn_cast<llvm::Function>(Value(args.at(1)));
        Require(function && personality && personality->getName() == "__CxxFrameHandler3",
                "Windows EH requires the qualified C++ personality.");
        function->setPersonalityFn(personality);
        return 0;
    }
    auto *insertion = Builder.GetInsertBlock();
    Require(insertion && !insertion->hasTerminator(), "No unterminated EH insertion block.");
    auto *function = insertion->getParent();
    Require(function && function->hasPersonalityFn(), "EH operation requires a function personality.");
    auto block = [&](std::int64_t handle) {
        auto &result = *Block(handle);
        Require(result.getParent() == function, "EH destination belongs to a different function.");
        return &result;
    };
    auto pad = [&](std::int64_t handle) {
        auto *result = llvm::dyn_cast<llvm::FuncletPadInst>(Value(handle));
        Require(result && result->getFunction() == function, "Invalid funclet token or ownership.");
        return result;
    };
    auto parent = [&]() -> llvm::Value * {
        if (data)
        {
            return pad(data);
        }
        return llvm::ConstantTokenNone::get(Context);
    };
    if (op == Operation::CatchSwitch)
    {
        Require(args.size() >= 2, "Catch switch needs an unwind destination and a handler.");
        std::vector<llvm::BasicBlock *> handlers;
        for (std::size_t i = 1; i < args.size(); ++i)
        {
            handlers.push_back(block(args.at(i)));
        }
        auto *unwind = args.at(0) ? block(args.at(0)) : nullptr;
        auto &value = *Builder.CreateCatchSwitch(parent(), unwind, CheckedInteger<unsigned>(handlers.size()));
        for (auto *handler : handlers)
        {
            value.addHandler(handler);
        }
        return Add({nullptr, &value});
    }
    if (op == Operation::CatchPad)
    {
        Require(args.size() == 4, "Invalid Windows catch pad arity.");
        auto *dispatch = llvm::dyn_cast<llvm::CatchSwitchInst>(Value(args.at(0)));
        Require(dispatch && dispatch->getFunction() == function, "Invalid catch switch ownership.");
        auto &typeInfo = *Value(args.at(1));
        auto &flags = *Value(args.at(2));
        auto &slot = *Value(args.at(3));
        Require(llvm::isa<llvm::Constant>(&typeInfo) && typeInfo.getType()->isPointerTy()
                && llvm::isa<llvm::ConstantInt>(&flags) && flags.getType()->isIntegerTy(32)
                && slot.getType()->isPointerTy(), "Invalid Windows catch pad operands.");
        return Add({nullptr, Builder.CreateCatchPad(dispatch, {&typeInfo, &flags, &slot})});
    }
    if (op == Operation::CleanupPad)
    {
        Require(args.empty(), "Invalid cleanup pad arity.");
        return Add({nullptr, Builder.CreateCleanupPad(parent(), {})});
    }
    if (op == Operation::CatchReturn)
    {
        Require(args.size() == 2, "Invalid catch return arity.");
        auto *value = llvm::dyn_cast<llvm::CatchPadInst>(pad(args.at(0)));
        Require(value != nullptr, "Catch return requires a catch pad.");
        return Add({nullptr, Builder.CreateCatchRet(value, block(args.at(1)))});
    }
    if (op == Operation::CleanupReturn)
    {
        Require(args.size() == 2, "Invalid cleanup return arity.");
        auto *value = llvm::dyn_cast<llvm::CleanupPadInst>(pad(args.at(0)));
        Require(value != nullptr, "Cleanup return requires a cleanup pad.");
        return Add({nullptr, Builder.CreateCleanupRet(value, args.at(1) ? block(args.at(1)) : nullptr)});
    }
    if (op == Operation::Invoke || op == Operation::IndirectInvoke || op == Operation::FuncletCall)
    {
        const bool indirect = op == Operation::IndirectInvoke;
        const bool invoke = op != Operation::FuncletCall;
        const std::size_t prefix = indirect ? 4 : invoke ? 3 : 1;
        Require(args.size() >= prefix, "Missing EH call operands.");
        auto &callee = *Value(args.at(indirect ? 1 : 0));
        auto *direct = llvm::dyn_cast<llvm::Function>(&callee);
        auto *signature = indirect
                              ? llvm::dyn_cast<llvm::FunctionType>(Type(args.at(0)))
                              : direct
                              ? direct->getFunctionType()
                              : nullptr;
        Require(signature && callee.getType()->isPointerTy() && !signature->isVarArg()
                && signature->getNumParams() == args.size() - prefix,
                "EH call signature arity mismatch.");
        std::vector<llvm::Value *> inputs;
        for (std::size_t i = prefix; i < args.size(); ++i)
        {
            auto &value = *Value(args.at(i));
            Require(value.getType() == signature->getParamType(CheckedInteger<unsigned>(i - prefix)),
                    "EH call argument type mismatch.");
            inputs.push_back(&value);
        }
        std::vector<llvm::OperandBundleDef> bundles;
        if (data)
        {
            bundles.emplace_back("funclet", std::vector<llvm::Value *>{pad(data)});
        }
        if (invoke)
        {
            const std::size_t destinations = indirect ? 2 : 1;
            return Add({nullptr, Builder.CreateInvoke(signature, &callee, block(args.at(destinations)),
                                                      block(args.at(destinations + 1)), inputs, bundles)});
        }
        Require(data != 0, "Funclet call requires a funclet token.");
        return Add({nullptr, Builder.CreateCall(signature, &callee, inputs, bundles)});
    }
    throw std::invalid_argument("Unsupported Windows EH operation.");
}
}
