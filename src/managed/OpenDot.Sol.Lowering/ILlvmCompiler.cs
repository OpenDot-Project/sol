// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
namespace OpenDot.Sol.Lowering;

/// <summary>Native LLVM operations. Semantic and instruction-selection policy stays in managed lowering.</summary>
public interface ILlvmCompiler
{
    string Version { get; }
    byte[] CompileObject(string llvmIr);
}
