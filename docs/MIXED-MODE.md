# Mixed-mode debugging (native + managed in one session)

Debugging a .NET program that loads native code of its own — a protection DLL, an interop layer, a
C++ engine — means stopping in both worlds in one run. Spydate cannot do that today: opening a binary
picks one of two engines and the other is unavailable for the rest of the session (see
`DECISIONS.md`, "Managed debugging goes through dbgshim, and is a second debugger rather than a
branch").

This document is the plan for closing that, and the record of a spike that settled the design before
any of it was built. The spike was throwaway code outside the repository, so the evidence it produced
is written down here rather than left in a scratch directory.

Status: planned. Nothing in this document is implemented yet.

## 1. The constraint that shapes everything

A Windows process has exactly one **debug port**. A second `DebugActiveProcess` on the same pid is
refused. That is the whole of the difficulty, and it is worth separating from a claim it is often
confused with:

- One process can have only one **debugger** — true, and unavoidable.
- One process can have only one **engine** — false. Visual Studio has run several at once since
  2012: a single component owns the OS debug connection, and the managed, native and script engines
  are interpreters layered on the one event stream.

Spydate's two sessions are mutually exclusive not because two engines are impossible but because
*each of them calls the OS attach itself*: `ManagedDebugSession` reaches `DebugActiveProcess` during
runtime startup, and `DebugSession` owns its own `WaitForDebugEvent` loop. Two attachers, so the
second loses.

The runtime then removes the easy way out:

- On **.NET Framework**, `ICorDebug` supports *interop debugging* — it owns the native loop itself and
  forwards native events to an unmanaged callback. Full control of both worlds, one engine.
- On **.NET Core and later**, interop debugging **was removed**. `ICorDebug` will neither share the
  port nor hand over native events. A native `int3` under it arrives as an event it does not forward,
  goes unhandled second-chance, and kills the process.

Spydate has to work on both, which rules out interop. So the only runtime-agnostic arrangement is the
remaining one: **the native loop owns the single debug port, and managed understanding is layered on
top of it.**

## 2. The design

`DebugSession` keeps the port and everything it already does — `int3` breakpoints by static address,
live patches by RVA re-applied on every module load, registers, memory, single-step, step-over,
WOW64. Managed meaning is supplied by the **DAC**, through `Microsoft.Diagnostics.Runtime` (ClrMD),
which attaches *passively*: `OpenProcess` and `ReadProcessMemory`, **no debug port**. That is what
lets it sit beside a native debugger instead of competing with it.

```
                    one OS debug port
                            │
                    DebugSession (Win32 debug loop)          ← owns stop/continue/step, writes int3
                            │
            ┌───────────────┴───────────────┐
            │                               │
   native addressing                  ManagedOverlay (ClrMD / DAC)
   module + RVA                       passive reads: no debug port
   registers, memory                  threads, stacks, objects, fields
            │                               │
            └───────────────┬───────────────┘
                            ▼
             a managed breakpoint is a native int3
             at an address the DAC resolved
```

The consequence worth stating plainly, because it is the point: **ClrMD being read-only does not
matter.** It never writes anything. It supplies an *address*; the native loop does the breaking. That
is how managed control is obtained without `ICorDebug`.

- **Managed breakpoint** = resolve `method + IL offset` → native address through the DAC's
  IL-to-native map, then plant a native `int3` there.
- **Managed step** = native single-steps until the DAC says the IL offset changed.
- **Managed inspection** = stacks, objects and fields read straight out of memory by the DAC.

What does not come along is `funceval` — running a property getter in the debuggee is an `ICorDebug`
feature with no DAC equivalent. In exchange, fields are read directly out of memory, which for an
obfuscated target is usually the more truthful answer than running its code.

## 3. What the spike proved (September 2026)

A throwaway probe drove the real `DebugSession` against purpose-built debuggees on .NET 10 and .NET
Framework 4.8. Every claim below was observed, not reasoned about.

### Core design — passes

| Claim | Evidence |
|---|---|
| The DAC reads a process whose debug port the native loop owns | 6 managed frames walked, incl. `Debuggee.Program.Main()` |
| The DAC maps method + IL offset → native address | `Work` `IL_0001` → native `…E05` |
| A JIT code page can be made writable | `execute-read`/*mapped* → `execute-readwrite` |
| The `int3` reaches the code | byte `0x8B` → `0xCC` |
| It fires, at the DAC-chosen address | stop reported exactly at `…E05` |
| Managed context *and* native registers at that stop | `Debuggee.Program.Work(Int32)` with `rcx=0x24` |
| The breakpoint re-arms and fires again | two distinct hits at the same address |

`rcx=0x24` is 36 decimal — the loop counter passed as `value`. A managed argument, read from a native
register, at a breakpoint the DAC placed.

### Runtime independence — passes

Desktop CLR **4.8.9345** behaved identically to CoreCLR **10.0.1126** through the same code path:
DAC attach, IL map, `int3`, fire, step. Nothing in the design is Core-only.

### Managed stepping — passes on both runtimes

`IL_0001 → IL_0005` in 3 native instructions on .NET 10; `IL_0004 → IL_0008` in 1 on Framework, each
landing on a real IL boundary rather than mid-statement.

### Native code in a module that is not loaded yet — passes

This is the case the whole exercise started from — a protection DLL that checks for a debugger in its
own entry point before anything else runs.

```
winmm.dll: imageBase 0x180000000  entryRva 0xD1B0  -> static entry 0x18000D1B0
+9428ms [module] winmm.dll loaded at 0x7FFBF8AE0000; 1 breakpoint armed
+9436ms [stopped] breakpoint at 0x18000D1B0
registers: rcx=0x7FFBF8AE0000  rdx=0x1
```

A breakpoint set **before launch**, on a module that did not exist; correctly unplanted while the
module was missing; armed the instant the loader announced the mapping; the module **relocated**
(file `0x180000000`, loaded `0x7FFBF8AE0000`) and the translation carried it; stopped in `DllMain`
**before its body ran**, with `rdx = 1` — `DLL_PROCESS_ATTACH`.

So the machinery for this already exists and is correct. What is missing is only the plumbing that
lets it be used on a .NET target at all.

## 4. What the spike did not prove

**A cold managed method cannot have its first call caught.** Before the JIT runs, ClrMD reports
`NativeCode` as `0xFFFFFFFFFFFFFFFF` — a sentinel, not a precode address — and `ILOffsetMap` is
empty, so there is no address to put an `int3` at. Polling noticed the compilation only at
**+9359ms**, which is *after* the call that caused it: that call ran unwatched. Everything after the
first call behaves normally, and a breakpoint at an interior IL offset of a once-cold method fires
as usual.

The deterministic route is the CLR's **DAC notification** mechanism — exception `0x04242420`, which is
how SOS's `!bpmd` breaks on a method that has not been jitted. The native loop already receives that
exception, but the spike measured notifications as **off by default**: one arrived at +135ms during
startup and none when the cold method was compiled. Turning them on means writing the CLR's
`g_dacNotificationFlags` in the target, which needs that global located (symbols, or a signature
scan). This is the one genuinely unproven part of the plan, and the most expensive.

**Only one target module was exercised.** The spike translated addresses for a single module
(`winmm.dll`). `DebugSession` keeps one `_target` and one `LoadedBase`, so breakpoints across several
native modules at once — the actual requirement, where three different DLLs are of interest — is not
covered by anything proven here.

## 5. Findings that are work items in their own right

Things the spike turned up in existing code, each worth fixing regardless of mixed mode:

- **`WriteByte` discards failure.** `DebugSession.WriteByte` checks `WriteProcessMemory` only to
  decide whether to flush the instruction cache, so `Plant` reported a breakpoint as planted when the
  write had silently done nothing. The first probe run believed in a breakpoint that was not there.
  This is exactly the failure this codebase guards against elsewhere.
- **JIT pages need unprotecting.** Since .NET 8, W^X maps JIT code `execute-read` through a double
  mapping, and a plain `WriteProcessMemory` is refused. `VirtualProtectEx` to `execute-readwrite`
  fixes it — the same treatment managed IL already gets. `DOTNET_EnableWriteXorExecute=0` also works
  but only on launch, so it is not a substitute.
- **No public `ProcessId`.** The DAC attaches by pid and `DebugSession` does not expose one; the probe
  had to find it by elimination.
- **`Reportable()` returns null outside the image,** so a stop in JITted code carries no listing
  address. Managed stops need a managed-shaped location beside the native one.
- **`ClrMethod.GetILOffset` is unreliable at a boundary.** It treats range ends as inclusive, so at
  the first byte of a range the *previous* entry wins: `GetILOffset(start)` returned `0` where the map
  said `1`, and `GetILOffset(start + 1)` returned `1`. Use an own half-open `[Start, End)` lookup.
- **IL maps contain zero-width and negative entries.** Framework produced `B20..B20` and `B2C..B2C`,
  and both runtimes emit `-1`/`-2`/`-3` for prologue, epilogue and unmapped code. Half-open lookups
  handle the empty ranges naturally; the negative offsets must be filtered before being treated as IL.
- **`PlantAll` is correctly guarded** by `if (!TargetLoaded) return;`. Worth recording because without
  it, planting at the loader break would write an `int3` at an untranslated address — possibly inside
  whichever module happens to occupy the unloaded module's preferred base.

## 6. Plan

### Phase 1 — the native engine, offered for a managed target

The smallest change that delivers native breakpoints and patches in a .NET process.

- ⬜ `DebugSession.ProcessId`, public, from the launch
- ⬜ `WriteByte` returns success; `Plant` refuses to record a breakpoint whose byte was not written
- ⬜ `VirtualProtectEx` to `execute-readwrite` around writes into executable pages, protection restored
- ⬜ A managed binary may choose the native engine: the host wires `Debug` rather than `ManagedDebug`
- ⬜ Breakpoints and patches in **several** named modules at once — per-module load bases instead of
  one `_target`/`LoadedBase`
- ⬜ Tests: a native breakpoint in a late-loading DLL's entry point, and the reason code at the stop

### Phase 2 — the managed overlay

- ⬜ `Microsoft.Diagnostics.Runtime` in `Directory.Packages.props` (per ADR‑005)
- ⬜ `ManagedOverlay` in `Spydate.Debugger`: passive attach by pid, disposed with the session
- ⬜ Managed threads, call stacks, objects, fields and statics at any native stop
- ⬜ `FlushCachedData` after the debuggee moves, rather than re-attaching
- ⬜ A managed location beside the native one in the snapshot
- ⬜ Tests: a managed stack walked while the native loop holds the port

### Phase 3 — managed breakpoints over the native loop

- ⬜ Resolve `Type::Method` + IL offset → native address through the DAC's map, own half-open lookup
- ⬜ Plant as a native `int3`; confirm re-arm on a JIT page
- ⬜ A method that is not compiled yet is refused *and says so*, rather than failing quietly
- ⬜ Tests: break at an interior IL offset; hit it twice

### Phase 4 — managed stepping

- ⬜ Step one IL offset: native single-steps until the IL offset changes
- ⬜ Step over a call, and step out
- ⬜ Tests: a step lands on an IL boundary, on both runtimes

### Phase 5 — catching a cold method's first call (optional)

The only part that may not work. Nothing in phases 1–4 depends on it, so it can land late or never.

- ⬜ `ExceptionInformation` accessors on `Native.DEBUG_EVENT`
- ⬜ Locate and set the CLR's DAC JIT-notification flag in the target
- ⬜ Decode notification → `ClrRuntime.GetMethodByHandle(methodDesc)` → plant at the new code
- ⬜ Tests: a breakpoint asked for before first call stops *at* that first call

### Phase 6 — the surfaces

- ⬜ MCP tools for native mode on a .NET target, within the manifest budget (see `MCP.md`)
- ⬜ Tool descriptions that steer an agent to native addresses for native code
- ⬜ Panel: both worlds in one stop
- ⬜ An ADR extending "Managed debugging goes through dbgshim…" with the third option it did not weigh

## 7. Relationship to the existing record

- `DECISIONS.md`, "Managed debugging goes through dbgshim, and is a second debugger rather than a
  branch" — its facts hold. `ICorDebug` does take the port and cannot extend `DebugSession`. What it
  did not consider is an engine that takes **no** port at all, which is what the DAC does.
- `ROADMAP.md`, Phase 5, "Managed inspection without control (CLRMD)" — the premise was right and the
  conclusion was wrong. "No breakpoints and no stepping, so it is not debugging" holds only if ClrMD
  has to do the breaking. It does not: it supplies the address and the native loop breaks. That item
  is the foundation of mixed mode rather than a consolation prize for skipping it.
