using Spydate.Decompiler.Native.IR;
using Spydate.Disassembly;

namespace Spydate.Decompiler.Native.Lifting;

/// <summary>Turns one instruction set's discovered function into the shared IR the passes and the emitter read.</summary>
public interface INativeLifter
{
    IrFunction Lift(Function function);
}
