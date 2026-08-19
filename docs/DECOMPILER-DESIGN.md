# Native decompiler design

Spydate's native decompiler turns discovered x86/x64 functions into pseudo‑C.
It is deliberately built as a classic multi‑stage pipeline so each stage can be
improved independently.

```
DecodedInstruction[] (per BasicBlock)
        │  X86Lifter
        ▼
IrFunction { IrBlock[] { IrStatement[] } }      ← "low IR": explicit registers/flags/memory
        │  passes: DeadFlagElimination, CopyPropagation, StackVarNaming, …
        ▼
IrFunction (simplified)
        │  Structurer                           ← blocks → if/else/while/do
        ▼
PseudoCEmitter
        ▼
string (pseudo-C)
```

## 1. IR

Namespace `Spydate.Decompiler.Native.IR`.

### Expressions (`IrExpr`, immutable records)

| Type | Example text | Notes |
|------|--------------|-------|
| `IrConst(long Value, int Bits)` | `0x10` | signed value, width in bits |
| `IrReg(string Name, int Bits)` | `eax`, `rsp` | architectural register (sub‑registers normalised where sensible) |
| `IrTemp(int Id, int Bits)` | `t3` | lifter‑introduced temporary |
| `IrLocal(string Name, int Bits, long FrameOffset)` | `local_28`, `arg_0` | stack slot named by frame offset (relative to entry `rsp`; 0 = return address) |
| `IrAddressOf(IrLocal Local, int Bits)` | `&local_28` | address of a stack slot (from `lea`) |
| `IrMem(IrExpr Address, int Bits)` | `*(uint32_t*)(rbp - 8)` | memory access |
| `IrUnary(IrUnaryOp Op, IrExpr Operand)` | `-x`, `~x`, `!c` | |
| `IrBinary(IrBinaryOp Op, IrExpr Left, IrExpr Right)` | `a + b`, `a << 2` | arithmetic, bitwise, comparisons |
| `IrCast(IrExpr Operand, int Bits, bool Signed)` | `(int64_t)x` | zero/sign extension, truncation |
| `IrCall(IrExpr Target, IrExpr[] Args)` | `CreateFileW(...)` | target may be `IrSymbol` |
| `IrSymbol(string Name, ulong Va)` | `kernel32!ExitProcess`, `sub_401000` | resolved symbolic address |
| `IrCondition(IrCondCode Cc, IrExpr Left, IrExpr Right)` | `a < b` | produced by folding `cmp/test` + `jcc`/`setcc`/`cmovcc` |

### Statements (`IrStmt`)

| Type | Pseudo‑C |
|------|----------|
| `IrAssign(IrExpr Dst, IrExpr Src)` | `eax = ebx + 4;` |
| `IrStore(IrExpr Address, IrExpr Value, int Bits)` | `*(int*)(rsp+8) = eax;` |
| `IrCallStmt(IrCall Call, IrExpr? Result)` | `eax = sub_401000();` |
| `IrReturn(IrExpr? Value)` | `return eax;` |
| `IrGoto(ulong TargetVa)` | `goto loc_401020;` |
| `IrBranch(IrExpr Cond, ulong TargetVa, ulong FallthroughVa)` | `if (eax == 0) goto loc_401020;` |
| `IrLabel(ulong Va)` | `loc_401020:` |
| `IrAsm(string Text)` | `__asm { cpuid }` — unsupported instruction passthrough |
| `IrComment(string Text)` | `// ...` |
| `IrNop` | (elided) |

`IrBlock` = `StartVa`, `List<IrStmt>`, `Successors`. `IrFunction` = entry VA,
name, blocks, parameters (heuristic), warnings.

## 2. Lifting (`X86Lifter`)

One instruction → zero or more statements. Conventions:

- Registers are named by their Iced name (`eax`, `rax`, `r8d`, `xmm0`).
  Partial writes to 32‑bit registers on x64 are lifted as full 64‑bit
  zero‑extending writes only when Iced reports that semantics (they always do
  for 32‑bit GPR destinations); otherwise as plain assignments to the named
  sub‑register. Full register‑aliasing analysis is a later pass.
- Memory operands become `IrMem(addr, bits)`; RIP‑relative addressing is
  folded to the absolute VA and resolved through the symbol table into
  `IrSymbol` when a symbol exists.
- `push` / `pop` are lifted as explicit `rsp` adjustments plus a store/load,
  which `StackFramePass` later collapses into named slots.
- Displacements are sign-extended from the *address size* (32-bit code:
  `[ebp-0x19]`, not `[ebp+0xFFFFFFE7]`); `fs:`/`gs:` accesses become
  `fs_base + …` / `gs_base + …`.
- Flags: the lifter remembers the **last flag‑setting instruction in the block**
  (`cmp`, `test`, `sub`, `add`, `and`, `or`, `xor`, `inc`, `dec`) and folds it
  into the following `jcc` / `setcc` / `cmovcc` as an `IrCondition`. If no
  producer is known it emits `IrCondition` on a pseudo `flags` register.
- Supported now (see `X86Lifter` for the exact switch):
  `mov movzx movsx movsxd lea add sub adc sbb and or xor not neg inc dec
  shl sal shr sar rol ror imul mul idiv div cmp test push pop call ret jmp
  jcc setcc cmovcc nop leave xchg cdq cqo cdqe cwde int3 hlt`.
  Everything else → `IrAsm(text)` + warning.

## 3. Passes (`Spydate.Decompiler.Native.Passes`)

Interface `IIrPass { void Run(IrFunction f); }`. Order matters and is defined in
`NativeDecompiler.DefaultPasses`:

1. `StackFramePass` — simulates the stack pointer through the CFG (push/pop,
   `sub/add rsp`, `mov rbp,rsp`, `mov r11,rsp`, `lea rbp,[rsp+x]`, x86
   stdcall-vs-cdecl cleanup heuristic) so every stack slot gets a frame offset
   relative to the entry `rsp`: `local_XX` below the return address, `arg_XX`
   above it, `&local_XX` for `lea`. Removes stack bookkeeping and frame-pointer
   setup, elides `push rbx … pop rbx` spill/restore pairs of callee-saved
   registers, drops junk `pop ecx` cleanup pops, and **recovers call
   arguments**: on x64 the contiguous prefix of `rcx, rdx, r8, r9` defined since
   the previous call (scanning back through single-predecessor blocks; incoming
   registers fill gaps in the entry block); on x86 the values pushed since the
   previous call (forwarded into the call when unchanged in between, otherwise
   the named slot is passed).
2. `GlobalNamingPass` — replaces absolute addresses with names, using a
   `GlobalNames` view of the image (symbols, discovered functions, scanned
   strings). `*(uint32_t*)(0x14003A100)` becomes `data_14003A100`, a store to
   one becomes an assignment to the name, an immediate that points at data
   becomes `&data_XXXX`, one that points at a function becomes `sub_XXXX`, and
   one that points at text becomes the text: `GetModuleHandleW(L"ntdll.dll")`.
   Strings are only recognised outside executable sections — x64 prologue bytes
   spell `"@SVWH"` in ASCII, and naming a call target after them would be worse
   than useless. Runs before copy propagation so a literal reaches the call that
   uses it.
3. `ReachingValues` + `CopyPropagationPass` — two-pass forward substitution: cheap
   values (constants, registers, symbols, locals) are forwarded to every reader,
   complex expressions only to a single reader, and a call result only into the
   very next statement. Tracks kills with register aliasing (`al` vs `eax` vs
   `rax`, x64 32-bit zero-extension), invalidates memory-reading values on
   stores/calls, treats caller-saved registers as clobbered by calls, keeps
   locals alive across calls (callees may read them), and removes definitions
   whose readers were all replaced and that are dead afterwards (redefined,
   clobbered, or a register at `return`; on x86 `ecx`/`edx` are kept as
   possible fastcall arguments). A call result only moves into its reader when
   the result register is dead afterwards: otherwise the call would be left
   behind as well as moved, and the output would show it happening twice.
   `ReachingValues` carries values into a block from outside it: a value survives a
   join only when every predecessor agrees on it and it is one that cannot change
   behind our back (constant, register, frame address, literal — never a memory read
   or a call). Predecessors not yet processed, i.e. the back edge of a loop, count as
   agreeing on nothing, which costs some propagation at loop headers and needs no
   fixpoint. This is the sound half of SSA; phi nodes and renaming are not built.
4. `AlgebraicSimplificationPass` — constant folding, `(x - 40) + 40 → x`,
   `x + 0 → x`, `x * 0`, `x & 0`, no-op casts, drops `mov edi, edi`-style self
   assignments.
5. `DeadCodeEliminationPass` — liveness over the whole function (backward fixpoint,
   registers keyed by their widest alias), removing assignments nothing reads again
   and dropping the result register of a call nobody consumes. Conservative where it
   has to be: a call keeps the argument registers live (on x86 always, on x64 only
   when argument recovery found nothing), a partial write such as `al` never kills
   the register it sits in, and a block whose successors are unknown — an unresolved
   indirect jump — is treated as though every register were live after it.

Planned: full SSA (cross-block propagation), return‑value inference, type
propagation from imports (uses `PeImage.Imports` names + a small Win32 API type
database), switch‑table recovery, recognition of `__SEH_prolog4`/`__EH_prolog`
frame helpers, x86 fastcall/thiscall register arguments.

## 4. Structuring

Namespace `Spydate.Decompiler.Native.Structuring`. `Structurer.Structure(IrFunction)`
returns a `CStmt` tree (`CSeq`, `CIf`, `CLoop`, `CBreak`, `CContinue`, `CGoto`,
`CLabel`, `CRaw`) that the emitter prints directly.

The shape comes from two dominance relations, computed with Cooper‑Harvey‑Kennedy
over the block graph and over its reverse:

- **Conditionals.** A two‑way branch joins at its immediate post‑dominator. That
  node becomes the region's *follow*; each arm is emitted up to it, and the code
  after the `if` continues there. An arm that reaches the follow immediately is
  dropped, so a one‑armed `if` has no `else` (the condition is inverted when it is
  the *then* arm that is empty).
- **Switches.** A block ending in `IrSwitch` (from a table `FunctionDiscovery`
  recovered) joins at its post-dominator too. Indices that share a target share an
  arm; arms are emitted in address order, so an arm that runs off its end falls into
  the next one exactly as C says it does. Inside an arm `break` means the switch, so
  an edge that would leave an enclosing loop keeps a `goto` instead.
- **Loops.** A back edge whose target dominates its source heads a natural loop,
  whose body is everything reaching the latch without passing the header. A header
  holding nothing but its test becomes `while (cond)`; a latch that
  post‑dominates the header becomes `do { } while (cond)` — that test guarantees
  no path leaves the loop early, which is exactly what makes moving the test to
  the bottom sound. Anything else is `while (true)` with the most‑used exit as the
  follow. Inside a loop, an edge to the header is `continue` and one to the follow
  is `break`.

Everything else keeps a `goto`: irreducible regions, jumps that leave a loop
sideways, tail jumps out of the function, and nesting past a depth cap. Only the
blocks a surviving `goto` targets get a `loc_XXXX:` label.

Three invariants make the output safe to read, and are asserted over every
function of both notepads in `StructuringTests`:

1. every block is emitted exactly once — never duplicated, never dropped
   (blocks no structure reached are appended at the end);
2. control falls out of a region only through the follow it was built around;
3. every surviving `goto` has a label to land on.

## 5. Emission (`PseudoCEmitter`)

Deterministic text with 4‑space indent, one statement per line and the source
VA as a trailing comment. The body is the `CStmt` tree, printed with Allman
braces; a lone `if` in an `else` arm chains into `else if`. `arg_XX` slots become
parameters in the signature, remaining referenced slots are declared as locals
with their `[sp±offset]`. Constants below 256 print in decimal, larger ones as
`0x..`. The header comment says how many edges kept a `goto`, so an unusually
tangled function is visible without reading it. Style is
closer to Ghidra/IDA than to compiled C — it prioritises being read alongside
the disassembly.

## 6. Managed decompilation

`ManagedDecompiler` wraps `ICSharpCode.Decompiler.CSharp.CSharpDecompiler` with
`DecompilerSettings` tuned for readability (latest C# version, `ThrowOnAssemblyResolveErrors=false`).
`UniversalAssemblyResolver` searches the file's folder and the current runtime
directory. IL text comes from `ICSharpCode.Decompiler.Disassembler.ReflectionDisassembler`.
