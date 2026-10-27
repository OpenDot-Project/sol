// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "Session.h"
// Upstream LLVM SDK templates trigger these MSVC diagnostics; first-party code keeps /W4 /WX.
#pragma warning(push)
#pragma warning(disable: 4244 4702)
#include <llvm/ADT/APFloat.h>
#include <llvm/Config/llvm-config.h>
#include <llvm/IR/Constants.h>
#include <llvm/IR/Verifier.h>
#include <llvm/MC/TargetRegistry.h>
#include <llvm/Support/TargetSelect.h>
#pragma warning(pop)
#include <atomic>
#include <bit>
#include <cstdlib>
#include <mutex>
#include <stdexcept>

namespace OpenDot::Sol::LlvmNative
{
namespace
{
std::atomic<std::uint32_t> NextIdentity{1};
std::once_flag Initialized;

void Require(bool condition, const char *message)
{
    if (!condition)
    {
        throw std::invalid_argument(message);
    }
}
}

void Initialize()
{
    static_assert(LLVM_VERSION_MAJOR == 23 && LLVM_VERSION_MINOR == 1 && LLVM_VERSION_PATCH == 3);
    std::call_once(Initialized, [] {
        // Native invariant failures must report to stderr and fail, never block CI or users on a CRT dialog.
        _set_error_mode(_OUT_TO_STDERR);
        _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
        if (llvm::InitializeNativeTarget() || llvm::InitializeNativeTargetAsmPrinter())
        {
            throw std::runtime_error("LLVM native target initialization failed.");
        }
    });
}

Session::Session() : Module(std::make_unique<llvm::Module>("sol-module", Context)), Builder(Context),
                     Identity(NextIdentity.fetch_add(1))
{
    Require(Identity < 0x80000000U, "LLVM session identity limit exceeded.");
    Initialize();
    std::string error;
    auto *target = llvm::TargetRegistry::lookupTarget(llvm::Triple("x86_64-pc-windows-msvc"), error);
    if (!target)
    {
        throw std::runtime_error(error);
    }
    Machine.reset(target->createTargetMachine(llvm::Triple("x86_64-pc-windows-msvc"), "x86-64", "",
                                              llvm::TargetOptions(), std::nullopt));
    if (!Machine)
    {
        throw std::runtime_error("Cannot create LLVM target machine.");
    }
    Module->setTargetTriple(llvm::Triple("x86_64-pc-windows-msvc"));
    Module->setDataLayout(Machine->createDataLayout());
}

void Session::CheckThread() const
{
    Require(Thread == std::this_thread::get_id(), "LLVM session belongs to another thread.");
}

Entry &Session::Get(std::int64_t handle)
{
    CheckThread();
    const auto token = static_cast<std::uint64_t>(handle);
    const auto index = static_cast<std::uint32_t>(token);
    Require(token >> 32 == Identity && index > 0 && index < Entries.size(), "Foreign or stale LLVM handle.");
    return Entries[index];
}

llvm::Type *Session::Type(std::int64_t handle)
{
    auto *value = Get(handle).Type;
    Require(value != nullptr, "Expected LLVM type handle.");
    return value;
}

llvm::Value *Session::Value(std::int64_t handle)
{
    auto *value = Get(handle).Value;
    Require(value != nullptr, "Expected LLVM value handle.");
    return value;
}

llvm::BasicBlock *Session::Block(std::int64_t handle)
{
    auto *value = Get(handle).Block;
    Require(value != nullptr, "Expected LLVM block handle.");
    return value;
}

std::int64_t Session::Add(Entry entry)
{
    Require(Entries.size() < 10000000, "LLVM handle inventory limit exceeded.");
    Entries.push_back(entry);
    return static_cast<std::int64_t>((std::uint64_t{Identity} << 32) | (Entries.size() - 1));
}

void Verify(llvm::Module &module)
{
    std::string error;
    llvm::raw_string_ostream output(error);
    if (llvm::verifyModule(module, &output))
    {
        throw std::runtime_error(error);
    }
}

std::int64_t Session::Apply(Operation op, std::int64_t type, const std::vector<std::int64_t> &args, std::int64_t data,
                            const char *text)
{
    CheckThread();
    auto count = [&](std::size_t n) {
        Require(args.size() == n, "Invalid LLVM operation arity.");
    };
    auto val = [&](std::size_t i) {
        Require(i < args.size(), "Missing LLVM operand.");
        return Value(args[i]);
    };
    auto integerData = [&] {
        Require(data >= 0 && data <= 1000000, "Invalid LLVM size/index.");
        return static_cast<unsigned>(data);
    };
    if ((op >= Operation::Alloca && op <= Operation::FPExt) || op == Operation::IndirectCall)
    {
        Require(Builder.GetInsertBlock() && !Builder.GetInsertBlock()->hasTerminator(),
                "No unterminated LLVM insertion block.");
    }
    switch (op)
    {
    case Operation::VoidType:
        count(0);
        return Add({llvm::Type::getVoidTy(Context)});
    case Operation::IntegerType:
        count(0);
        Require(data == 1 || data == 8 || data == 16 || data == 32 || data == 64, "Unsupported LLVM integer width.");
        return Add({llvm::IntegerType::get(Context, integerData())});
    case Operation::FloatType:
        count(0);
        return Add({llvm::Type::getFloatTy(Context)});
    case Operation::DoubleType:
        count(0);
        return Add({llvm::Type::getDoubleTy(Context)});
    case Operation::PointerType:
        count(0);
        return Add({llvm::PointerType::getUnqual(Context)});
    case Operation::ArrayType:
        count(0);
        Require(Type(type)->isSized(), "Unsized array element.");
        return Add({llvm::ArrayType::get(Type(type), integerData())});
    case Operation::StructType: {
        std::vector<llvm::Type *> types;
        for (auto arg : args)
        {
            Require(Type(arg)->isSized(), "Unsized struct member.");
            types.push_back(Type(arg));
        }
        return Add({llvm::StructType::get(Context, types)});
    }
    case Operation::Integer: {
        count(0);
        auto *integer = llvm::dyn_cast<llvm::IntegerType>(Type(type));
        Require(integer && text && *text, "Invalid integer constant.");
        llvm::StringRef spelling(text);
        Require(spelling.size() <= 128, "Integer spelling exceeds the LLVM bridge limit.");
        const bool negative = spelling.consume_front("-");
        Require(!spelling.empty() && spelling.find_first_not_of("0123456789") == llvm::StringRef::npos,
                "Invalid integer digits.");
        llvm::APInt value;
        Require(!spelling.getAsInteger(10, value) && value.getActiveBits() <= integer->getBitWidth(),
                "Integer exceeds its LLVM width.");
        value = value.zextOrTrunc(integer->getBitWidth());
        if (negative)
        {
            value = -value;
        }
        return Add({nullptr, llvm::ConstantInt::get(Context, value)});
    }
    case Operation::Floating:
        count(0);
        Require(Type(type)->isFloatingPointTy(), "Expected floating type.");
        return Add({nullptr, llvm::ConstantFP::get(Type(type), std::bit_cast<double>(data))});
    case Operation::Zero:
        count(0);
        Require(Type(type)->isSized(), "Unsized zero.");
        return Add({nullptr, llvm::Constant::getNullValue(Type(type))});
    case Operation::Function: {
        Require(text && *text && Module->getNamedValue(text) == nullptr && (data >= 0 && data <= 2),
                "Invalid or duplicate LLVM function.");
        Require(llvm::FunctionType::isValidReturnType(Type(type)), "Invalid function return type.");
        std::vector<llvm::Type *> parameters;
        for (auto arg : args)
        {
            Require(llvm::FunctionType::isValidArgumentType(Type(arg)), "Invalid parameter type.");
            parameters.push_back(Type(arg));
        }
        auto *signature = llvm::FunctionType::get(Type(type), parameters, false);
        auto *function = llvm::Function::Create(
            signature, data == 1 ? llvm::GlobalValue::InternalLinkage : llvm::GlobalValue::ExternalLinkage, text,
            *Module);
        if (data == 2)
        {
            function->setDLLStorageClass(llvm::GlobalValue::DLLExportStorageClass);
        }
        if (std::string_view(text) == "sol_rt_fail")
        {
            function->addFnAttr(llvm::Attribute::NoReturn);
        }
        return Add({nullptr, function});
    }
    case Operation::FunctionType: {
        Require(llvm::FunctionType::isValidReturnType(Type(type)), "Invalid indirect function result type.");
        std::vector<llvm::Type *> parameters;
        parameters.reserve(args.size());
        for (auto arg : args)
        {
            Require(llvm::FunctionType::isValidArgumentType(Type(arg)), "Invalid indirect function parameter type.");
            parameters.push_back(Type(arg));
        }
        return Add({llvm::FunctionType::get(Type(type), parameters, false)});
    }
    case Operation::Parameter: {
        count(1);
        auto *function = llvm::dyn_cast<llvm::Function>(val(0));
        Require(function && data >= 0 && static_cast<std::uint64_t>(data) < function->arg_size(),
                "Invalid LLVM parameter.");
        return Add({nullptr, function->getArg(integerData())});
    }
    case Operation::Block: {
        count(1);
        auto *function = llvm::dyn_cast<llvm::Function>(val(0));
        Require(function != nullptr, "Expected function.");
        auto *block = llvm::BasicBlock::Create(Context, text, function);
        return Add({nullptr, nullptr, block});
    }
    case Operation::Position:
        count(1);
        Builder.SetInsertPoint(Block(args[0]));
        return 0;
    case Operation::Alloca:
        count(0);
        Require(Type(type)->isSized(), "Unsized allocation.");
        {
            auto *value = Builder.CreateAlloca(Type(type));
            if (data)
            {
                Require(data == 8 || data == 16, "Invalid alloca alignment.");
                value->setAlignment(llvm::Align(data));
            }
            return Add({nullptr, value});
        }
    case Operation::Load:
        count(1);
        Require(val(0)->getType()->isPointerTy() && Type(type)->isSized(), "Invalid load.");
        {
            auto *value = Builder.CreateLoad(Type(type), val(0));
            if (data)
            {
                Require(Type(type)->isPointerTy(), "Atomic loads require references.");
                value->setAtomic(llvm::AtomicOrdering::Acquire);
                value->setAlignment(llvm::Align(8));
            }
            return Add({nullptr, value});
        }
    case Operation::Store:
        count(2);
        Require(val(1)->getType()->isPointerTy() && !val(0)->getType()->isVoidTy(), "Invalid store.");
        return Add({nullptr, Builder.CreateStore(val(0), val(1))});
    case Operation::Gep: {
        Require(args.size() >= 2 && val(0)->getType()->isPointerTy() && Type(type)->isSized(), "Invalid GEP.");
        std::vector<llvm::Value *> indices;
        for (std::size_t i = 1; i < args.size(); ++i)
        {
            Require(val(i)->getType()->isIntegerTy(), "Invalid GEP index.");
            indices.push_back(val(i));
        }
        Require(llvm::GetElementPtrInst::getIndexedType(Type(type), indices) != nullptr, "Invalid GEP shape.");
        return Add({nullptr, Builder.CreateGEP(Type(type), val(0), indices)});
    }
    case Operation::StructGep: {
        count(1);
        auto *structure = llvm::dyn_cast<llvm::StructType>(Type(type));
        Require(structure && data >= 0 && data < structure->getNumElements() && val(0)->getType()->isPointerTy(),
                "Invalid struct GEP.");
        return Add({nullptr, Builder.CreateStructGEP(structure, val(0), integerData())});
    }
    case Operation::Call: {
        Require(!args.empty(), "Missing call target.");
        auto *function = llvm::dyn_cast<llvm::Function>(val(0));
        Require(function && function->arg_size() == args.size() - 1, "Call signature arity mismatch.");
        std::vector<llvm::Value *> inputs;
        for (std::size_t i = 1; i < args.size(); ++i)
        {
            Require(val(i)->getType() == function->getFunctionType()->getParamType(static_cast<unsigned>(i - 1)),
                    "Call type mismatch.");
            inputs.push_back(val(i));
        }
        return Add({nullptr, Builder.CreateCall(function, inputs)});
    }
    case Operation::IndirectCall: {
        Require(!args.empty(), "Missing indirect call target.");
        auto *signature = llvm::dyn_cast<llvm::FunctionType>(Type(type));
        Require(signature && val(0)->getType()->isPointerTy()
                    && signature->getNumParams() == args.size() - 1,
                "Indirect call signature or target mismatch.");
        std::vector<llvm::Value *> inputs;
        inputs.reserve(args.size() - 1);
        for (std::size_t i = 1; i < args.size(); ++i)
        {
            Require(val(i)->getType() == signature->getParamType(static_cast<unsigned>(i - 1)),
                    "Indirect call argument type mismatch.");
            inputs.push_back(val(i));
        }
        return Add({nullptr, Builder.CreateCall(signature, val(0), inputs)});
    }
    case Operation::Return:
        Require(args.size() <= 1, "Invalid return.");
        return Add({nullptr, args.empty() ? Builder.CreateRetVoid() : Builder.CreateRet(val(0))});
    case Operation::Branch:
        count(1);
        return Add({nullptr, Builder.CreateBr(Block(args[0]))});
    case Operation::ConditionalBranch:
        count(3);
        Require(val(0)->getType()->isIntegerTy(1), "Invalid branch condition.");
        return Add({nullptr, Builder.CreateCondBr(val(0), Block(args[1]), Block(args[2]))});
    case Operation::Unreachable:
        count(0);
        return Add({nullptr, Builder.CreateUnreachable()});
    case Operation::Phi: {
        Require(args.size() % 2 == 0 && Type(type)->isFirstClassType(), "Invalid phi type or arity.");
        auto *phi = Builder.CreatePHI(Type(type), static_cast<unsigned>(args.size() / 2));
        for (std::size_t i = 0; i < args.size(); i += 2)
        {
            Require(val(i)->getType() == Type(type), "Invalid phi type.");
            phi->addIncoming(val(i), Block(args[i + 1]));
        }
        return Add({nullptr, phi});
    }
    case Operation::Extract: {
        count(1);
        auto *structure = llvm::dyn_cast<llvm::StructType>(val(0)->getType());
        Require(structure && data >= 0 && data < structure->getNumElements(), "Invalid extraction.");
        return Add({nullptr, Builder.CreateExtractValue(val(0), integerData())});
    }
    default:
        break;
    }
    if (op == Operation::FNeg)
    {
        count(1);
        Require(val(0)->getType()->isFloatingPointTy(), "Invalid floating negation.");
        return Add({nullptr, Builder.CreateFNeg(val(0))});
    }
    if (op >= Operation::ZExt && op <= Operation::FPExt)
    {
        count(1);
        llvm::Instruction::CastOps cast = op == Operation::ZExt
                                              ? llvm::Instruction::ZExt
                                              : op == Operation::SExt
                                              ? llvm::Instruction::SExt
                                              : op == Operation::Trunc
                                              ? llvm::Instruction::Trunc
                                              : llvm::Instruction::FPExt;
        Require(llvm::CastInst::castIsValid(cast, val(0), Type(type)), "Invalid LLVM cast.");
        return Add({nullptr, Builder.CreateCast(cast, val(0), Type(type))});
    }
    count(2);
    Require(val(0)->getType() == val(1)->getType(), "LLVM binary operand type mismatch.");
    if (op == Operation::ICmp || op == Operation::FCmp)
    {
        if (op == Operation::ICmp)
        {
            constexpr llvm::CmpInst::Predicate predicates[]{llvm::CmpInst::ICMP_EQ, llvm::CmpInst::ICMP_NE,
                                                            llvm::CmpInst::ICMP_SLT, llvm::CmpInst::ICMP_SGT,
                                                            llvm::CmpInst::ICMP_SLE, llvm::CmpInst::ICMP_SGE,
                                                            llvm::CmpInst::ICMP_EQ, llvm::CmpInst::ICMP_NE,
                                                            llvm::CmpInst::ICMP_ULT, llvm::CmpInst::ICMP_UGT,
                                                            llvm::CmpInst::ICMP_ULE, llvm::CmpInst::ICMP_UGE};
            Require(data >= 0 && data < 12 && val(0)->getType()->isIntegerTy(), "Invalid integer predicate.");
            return Add({nullptr, Builder.CreateICmp(predicates[data], val(0), val(1))});
        }
        constexpr llvm::CmpInst::Predicate predicates[]{llvm::CmpInst::FCMP_OEQ, llvm::CmpInst::FCMP_UNE,
                                                        llvm::CmpInst::FCMP_OLT, llvm::CmpInst::FCMP_OGT,
                                                        llvm::CmpInst::FCMP_OLE, llvm::CmpInst::FCMP_OGE};
        Require(data >= 0 && data < 6 && val(0)->getType()->isFloatingPointTy(), "Invalid floating predicate.");
        return Add({nullptr, Builder.CreateFCmp(predicates[data], val(0), val(1))});
    }
    constexpr llvm::Instruction::BinaryOps binary[]{llvm::Instruction::Add, llvm::Instruction::Sub,
                                                    llvm::Instruction::Mul, llvm::Instruction::SDiv,
                                                    llvm::Instruction::UDiv, llvm::Instruction::SRem,
                                                    llvm::Instruction::URem, llvm::Instruction::And,
                                                    llvm::Instruction::Or, llvm::Instruction::Xor,
                                                    llvm::Instruction::Shl, llvm::Instruction::LShr,
                                                    llvm::Instruction::AShr, llvm::Instruction::FAdd,
                                                    llvm::Instruction::FSub, llvm::Instruction::FMul,
                                                    llvm::Instruction::FDiv, llvm::Instruction::FRem};
    const auto index = static_cast<int>(op) - static_cast<int>(Operation::Add);
    Require(index >= 0 && index < static_cast<int>(std::size(binary)), "Unsupported LLVM operation.");
    Require(index < 13 ? val(0)->getType()->isIntegerTy() : val(0)->getType()->isFloatingPointTy(),
            "Invalid LLVM binary operand category.");
    return Add({nullptr, Builder.CreateBinOp(binary[index], val(0), val(1))});
}
}
