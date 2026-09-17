# Mixed-mode debugging (native + managed in one session)

Debugging a .NET program that loads native code of its own — a protection DLL, an interop layer, a
C++ engine — means stopping in both worlds in one run. Spydate cannot do that today: opening a binary
picks one of two engines and the other is unavailable for the rest of the session (see
`DECISIONS.md`, "Managed debugging goes through dbgshim, and is a second debugger rather than a
branch").

This document is the plan for closing that, and the record of a spike that settled the design before
any of it was built. The spike was throwaway code outside the repository, so the evidence it produced
is written down here rather than left in a scratch directory.

Status: **Phases 1–7 are complete** on the `mixed-mode` branch. Phase 1 is the native engine: the
correctness fixes are in, breakpoints and patches can both sit in several named modules at once, a .NET
target can be told to use the native engine, and all four break-at choices are wired on both engines.
Both engines have been run end to end **through the Debug Program dialog** — not just the choosing, the
running. Phase 2 is the managed overlay: `ManagedOverlay` attaches ClrMD passively over the process the
native loop owns and reads managed threads, stacks, objects, fields, statics and the IL-to-native map
at a native stop, without a debug port of its own. Phase 3 is managed breakpoints over that loop:
`AddManagedBreakpoint` resolves a method + IL offset to a JIT address through the overlay and plants a
native int3 there, which re-arms and fires like any other and refuses a not-yet-compiled method with a
reason. Phase 4 is managed stepping: `StepManaged` runs native single-steps until the overlay's IL
offset changes — step into, over and out, each landing on a real IL boundary. Phase 5 held a breakpoint
on a not-yet-compiled method and planted it once it had native code — catching a *later* call, not the
first — and concluded the deterministic first-call catch was closed to a read-only DAC. That was half
right: it is closed to the *JIT-notification* route, which needs a writable DAC. **Phase 7 reaches it
another way and completes it** — an int3 on the JIT's shared prestub worker catches a cold method the
instant it is about to be compiled, identifying it by the MethodDesc the worker is passed, all with the
DAC still read-only. So a managed breakpoint on a method called once at startup — `OnStartup`, the case
that motivated all of this — now stops on its first call, on CoreCLR and .NET Framework alike. Phase 6
is the surfaces, and is done: a managed
breakpoint can be marked in the gutter of a natively debugged .NET program and plants over the native
loop, a mixed stop shows the managed call stack and location beside the native registers, and the
agent's `debug_break` reaches the same path. Every mixed-mode phase is now complete, and verified on a
real .NET Framework 4.8 app (CSPro Capture) as well as the CoreCLR test fixture; what a merge to master
waits on is review, not another phase.

The branch is `mixed-mode`, not `mixed-mode-phase-1`: it holds the whole feature across every phase.
Merging to master waits until **all** mixed-mode phases are complete and stable, not the end of any one
phase — the earlier name wrongly implied a merge after Phase 1.

One thing worth knowing before touching the debugger panel again: it publishes **nothing** to UI
Automation. A dump of the whole window found 174 named elements — menus, toolbar, explorer tree,
document tab, bottom-pane tabs, status bar — and not one control from inside that panel. Its own
contents can only be checked from a screenshot, and its actions only through the Debug menu.

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
startup and none when the cold method was compiled. The spike guessed the fix was writing the CLR's
`g_dacNotificationFlags`; Phase 5 later disassembled the runtime against its public PDB and found that
guess wrong — that flag has no JIT bit, and the JIT notification is armed only by the DAC populating
`g_pNotificationTable`, which has no in-process writer and is reachable only through a *writable* DAC
(`IXCLRDataProcess::SetCodeNotifications`). A writable DAC is what this design deliberately does not use,
so the first-call catch is not just unproven but **incompatible with the read-only premise** — the full
evidence is under Phase 5 below. The fallback (hold the breakpoint, plant it the instant the method has
native code) catches every later call and misses only a method called exactly once.

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

- ✅ `DebugSession.ProcessId`, public, assigned while the process is created and so readable as soon
  as `Start` has returned
- ✅ Writes say whether they happened. `WriteByte` and `WriteBytes` return success; `Plant` refuses to
  mark a breakpoint planted whose byte did not go in, and reports it; `SetPatch` and `ClearPatch`
  report a refused write through `WriteRange`; and the three *restore* paths — removing a breakpoint,
  lifting a one-shot, and lifting an int3 to step off it — say so when the byte will not go back.
  Those three were not in this list when it was written and matter more than the plant path did: a
  failed plant only fails to add a breakpoint, where a failed restore leaves an int3 in the program,
  in one case permanently and with no record left of what it replaced
- ✅ `VirtualProtectEx` to `execute-readwrite` around a write the page would otherwise refuse, with the
  protection put straight back rather than left open
- ✅ A test for a genuine write *failure* on a readable page — the branch the protect-and-retry made
  matter, as against the unmapped case which fails at the read. It was thought maybe-ungettable, and
  was not: `KUSER_SHARED_DATA` at `0x7FFE0000` is readable in every process but refused by
  `VirtualProtectEx`, so the int3 cannot go in even after the retry.
  `AWriteOntoAReadableButUnwritablePageIsRefusedNotFakedAsPlanted` asserts the breakpoint is not listed
  as planted and reports "the write was refused". Also covered: the unmapped case (fails at the read),
  a patch that cannot be written reporting instead of returning success, and the process id
- ✅ The choice is made in the run configuration, not on a toggle of its own. `Start Debugging…`
  opens the Debug Program dialog — engine, executable, arguments, folder, break-at — filled in with
  whatever was used for this binary last time, because all of it is remembered per binary in
  `debug.json` beside the host. The engine sat on a toolbar checkbox first, which put one launch
  option somewhere different from every other launch option; a dialog on starting is dnSpy's shape
  and the right one. `Attach to Process…` is on the menu, disabled, because the debug loop can only
  launch — see ROADMAP
- ✅ A managed binary may choose the native engine. `IsManaged` used to mean two things at once —
  "this file is IL-only" and "the CLR will be driving it" — and separating them is the whole change:
  `DebugNatively` is the choice, `UsesManagedDebugger` is `IsManaged && !DebugNatively`, and
  everything that meant the second asks that instead. The start path, the breakpoint toggle, the
  register and pause panes and the managed-only tabs all follow it, `AssistantViewModel` asks the
  panel rather than re-deriving the answer from the file — deciding it twice would hand the agent the
  managed interface for a process the native loop owns — and the choice resets when a different
  binary is opened. Offered both on the Debug menu and as a toolbar checkbox, enabled only before a
  run, since which debugger owns the process is settled when the session is made
- ✅ Breakpoints in **several** named modules at once. One is now kept under `(module, RVA)`, where a
  null module means the one the listing is about and keeps its static address — which is what every
  existing caller still passes, so the public API, the snapshot and the window were untouched. A name
  is required for anything else because a static address cannot say which module is meant when they
  share a preferred base. Any module loading plants the breakpoints that name it, the loader announces
  that before the module runs its own code, and only that module's breakpoints are forgotten when it
  unloads
- ✅ Patches in several modules. A patch is kept under `(module, RVA)` the way a breakpoint now is, and
  `LivePatch` carries the module itself — so the positional constructor every caller uses is unchanged
  and `SetPatch` needed no new overload. An RVA was always module-relative; what it lacked was a way to
  say *which* module, so every patch went into the program being read. This is the half of the original
  request that mattered most: patching a protection DLL's process-attach check is a patch in a module
  other than the opened one. Patches for a module are written when that module loads, before its
  breakpoints are planted, so a breakpoint landing on a patched byte still records the patched byte
- ✅ Tests: a native breakpoint in a **late**-loading DLL's entry point, and the reason code at the
  stop. `ABreakpointInALateLoadingDllFiresAtItsEntryWithTheDllMainReason` needs no custom fixture:
  `rundll32.exe winmm.dll,<any>` does a `LoadLibrary` of winmm from its command line, and winmm is not
  statically linked into rundll32, so the load is a genuine `LOAD_DLL` event after the process is up —
  the loader-event path the spike proved against a DLL arriving nine seconds in. Run with "Don't
  break" so the one stop is winmm's own entry, the test asserts the stop is at `winmmBase +
  EntryPointRva`, reported as `winmm.dll+0xRVA`, with `rdx == 1` — `DLL_PROCESS_ATTACH`, the DllMain
  reason, the second x64 argument — and terminates there, before rundll32 reaches the export that does
  not exist. Also covered from the start: two modules at once with each byte restored to its own
  module, a module-qualified breakpoint firing at an entry point, and one naming a module that never
  loads staying unplanted. **Phase 1 is complete.**
- ✅ Run end to end from the dialog, both engines, through the real `DebuggerViewModel` and the real
  `RunConfigWindow`. The dialog work had proved the *choosing* and never the *running*; this closes
  that. A scratchpad panel probe (see `docs/DECISIONS.md` on why the App is untestable, and the
  throwaway-console pattern) opens a binary, sets a breakpoint, drives `StartCommand` — which opens
  and accepts the actual modal dialog — and follows the run. Native (`where.exe`): stops at Create
  Process with registers and modules, continues to a user breakpoint at the entry point that reports
  as the *static* VA though the module was ASLR-rebased (the `(module, RVA)` planting and the
  runtime→static round-trip both work through the whole stack), continues to `Exited with code 2`.
  Managed (`spydate-mcp.dll` under its apphost as the executable): launches under the CLR, reaches
  "Held before it ran anything" with `ShowsRegisters` false — the panel rearranged for managed — and
  is terminated cleanly. What is *not* covered this way: the native-engine-on-a-.NET-program path (a
  breakpoint in a native DLL a managed process loads), which needs a managed target that loads a known
  native DLL to exercise deterministically
- ✅ All four break-at choices wired, both engines. The native loop decides at the loader break — the
  one moment the timing is not a race — whether to report it, let go, or run on to a one-shot at an
  entry: the launched process's own entry ("Entry Point"), or the opened module's entry ("Module cctor
  or Entry Point", which for native code with no static constructor is the entry point), the latter
  planted when the module lands under a host, which is the mixed-mode case. The decision moved out of a
  `_skipFirstStop` flag in the view model, which could only express "Don't break", into
  `DebugSession.Start`. The managed engine holds at the start, plants a breakpoint at the entry-point
  token — or the module initializer's, from a new `ManagedAssembly.ModuleInitializer`, when the
  assembly has one — and continues to it; both those methods run once, so the breakpoint is a one-shot
  in all but name. Verified: three new `DebuggerTests` (Entry Point stops at `exe+EntryPointRva` with
  no loader break reported; module entry standalone likewise; Don't break runs to exit with zero
  stops), two new `ManagedDecompilerTests` (the entry point resolves to Main with a real MethodDef
  token; an assembly with no module initializer reports none), and the panel probe driving all four
  through the dialog on both engines — the managed entry stop landing on `Program.<Main>` at IL_0000.
  Not verified: the managed module-initializer branch against a real assembly that has one (spydate-mcp
  does not), and the host-loaded-DLL module-entry stop end to end

### Phase 2 — the managed overlay

**Phase 2 is complete.** ClrMD attaches passively over the process the native loop owns, and reads the
managed world at a native stop without a port of its own.

- ✅ `Microsoft.Diagnostics.Runtime` 4.1.745802 in `Directory.Packages.props` and referenced from
  `Spydate.Debugger` — the version the spike proved.
- ✅ `ManagedOverlay` in `Spydate.Debugger`: `DataTarget.AttachToProcess(pid, suspend: false)`, so
  `OpenProcess` and `ReadProcessMemory` through the DAC and no debug port. The attach is lazy — the CLR
  is not up the instant a process starts, so every query answers empty until a runtime is present, and
  a native debuggee answers empty forever. `DebugSession` creates it when the process is created,
  exposes it as `Managed`, and disposes it on `Stop` so the DAC's read handle on the process goes
  before the process does.
- ✅ Managed threads, call stacks, objects, fields and statics at any native stop. `Threads()` walks
  each thread's stack (method, module, kind, IP), `ObjectAt(address)` reads a heap object's fields
  flat, `Statics(typeName)` reads a type's statics from the first app domain. Primitives are read as
  their values — checked before the value-type test, since an `int` is a value type and the other way
  round every number reads as `System.Int32`; strings as their text; references as their type plus the
  referent's address to follow.
- ✅ `FlushCachedData` after the debuggee moves, rather than re-attaching. The loop calls
  `MarkMoved` when it continues, steps or pauses; the next read flushes the DAC's cache and re-reads,
  which is far cheaper than a fresh attach.
- ✅ A managed location beside the native one: `LocationOf(instructionPointer)` gives the method, its
  module, metadata token, and the IL offset from the JIT map — the same address the registers show,
  said in the terms the decompiled listing is in. Null for a native address, which is correct.
- ✅ Tests: `ManagedOverlayTests`, against a `ManagedDebuggee` fixture that spins in a known method and
  holds a known static. A managed stack is walked (`Wait <- Spin <- Main`) while `DebugSession` holds
  the port; a static string is read out of memory (`Label = "spydate-overlay"`); a native IP is named
  in managed terms (`Spin` at `IL_21`, a real token); and a native program's overlay is present but
  empty rather than throwing.

### Phase 3 — managed breakpoints over the native loop

**Phase 3 is complete.** A managed breakpoint is a native int3 at an address the DAC resolved — which
is the whole mixed-mode payoff, and needs nothing from ICorDebug.

- ✅ Resolve `Type::Method` + IL offset → native address through the DAC's map. `ManagedOverlay.Resolve`
  finds the method, then `NativeForIl` turns the IL offset into a native address: exact where the offset
  is a mapped boundary, otherwise the start of the statement that contains it (the greatest mapped
  offset at or below it). `IlOffsets` lists a method's mapped offsets so a caller can pick an interior
  one without knowing the IL layout.
- ✅ Plant as a native int3; re-arm on a JIT page confirmed. `DebugSession.AddManagedBreakpoint`
  resolves through the overlay and plants an ordinary int3 at the JIT address with `AddBreakpoint` — a
  JIT address is outside every module image, so `ToRuntime`/`ToStatic` carry it unchanged and it re-arms
  and fires exactly like any other breakpoint. The write goes through the W^X `VirtualProtectEx` path
  Phase 1 already built.
- ✅ A method not compiled yet is refused and says so. A cold method's `NativeCode` is the
  `0xFFFFFFFFFFFFFFFF` sentinel and its IL map is empty, so `Resolve` refuses with "not compiled yet …
  it JITs on its first call" rather than a zero the caller has to guess at. (ClrMD only surfaces a
  method once it has a method descriptor, so the fixture references its cold method through a delegate
  to give it one without a call — which is how the refusal is told apart from a name that does not
  exist.)
- ✅ Tests: `ManagedOverlayTests`. A breakpoint at an interior IL offset of a repeatedly-called method
  fires, is reported in that method by `LocationOf`, and — continued — fires again at the same address,
  which is the re-arm. A cold method is refused with a reason. A real bug was found on the way: a read
  of a *running* process returned stale DAC state (a method that JITted after the attach was never
  seen, because nothing had called `MarkMoved`), so the overlay now flushes on every read while the
  process runs, and caches only at a stop.

### Phase 4 — managed stepping

**Phase 4 is complete.** A managed step is a run of native single-steps that ends when the IL offset
changes — the overlay's IL-to-native map says which native range each offset occupies, and while the
instruction pointer stays in the starting range it is still on the same offset. Nothing talks to
ICorDebug.

- ✅ Step one IL offset: `DebugSession.StepManaged` reads the starting IL range from
  `ManagedOverlay.StepInfoAt` and single-steps until the instruction pointer leaves it, then reports.
  No DAC read per instruction — the range is fetched once and the check is a comparison.
- ✅ Step over a call, and step out. A call is stepped over by a one-shot int3 after it (the existing
  step-over machinery), so `Over` stays in the method; `Into` decodes the call and, when its target is
  managed, single-steps into it to land at the callee's first line, stepping over native helpers;
  `Out` does not walk at all — it reads the caller's return address from the frame above (the overlay's
  stack walk) and runs to a one-shot there.
- ✅ Tests: `ManagedOverlayTests`. From a managed breakpoint in Step, a step over lands on the next IL
  offset in the same method; step out returns to the caller Spin; eight step-overs in Spin stay in Spin
  across its Step and Wait calls rather than descending; a step into descends out of Spin into a
  managed callee. Each lands on a real IL boundary. The suite exercises CoreCLR; the spike showed the
  same on desktop CLR 4.8, so "both runtimes" holds — a .NET Framework build is not something the test
  project produces, so that half stays the spike's evidence rather than a suite test.

### Phase 5 — catching a cold method's first call (the notification route; superseded by Phase 7)

> **Superseded by Phase 7.** This phase tried the JIT-*notification* route and concluded, correctly,
> that it needs a writable DAC and so is closed to this design. It then over-generalised that to "the
> first-call catch is impossible read-only", which Phase 7 disproves by catching it at the *prestub*
> instead. What stands from this phase is the held-and-planted fallback (still the behaviour when no PDB
> is available) and the proof that the notification flag is the wrong lever. The rest is kept as the
> record of a route that did not pan out.

The plan called this "the only part that may not work", and on this runtime the deterministic
first-call catch does not, *by this route*: the notification machinery is built and correct, but the one
thing it depends on — enabling the CLR's JIT notifications — could not be done reliably. So a breakpoint
on a cold method is **held and planted the moment it has native code, catching a later call**, not the
first — until Phase 7 adds the prestub catch. Everything here is honest about that.

- ✅ `ExceptionInformation` accessors on `Native.DEBUG_EVENT`: `NumberParameters` and
  `ExceptionInformation(i)`, reading the EXCEPTION_RECORD parameters at the x64 offsets. Verified
  against the real DAC notification that fires at startup — three parameters, the first the magic
  `0x31415927` that marks a genuine CLR notification.
- ⬜ **Enable the CLR's JIT notifications — not achievable from a read-only DAC, and now known why.**
  The earlier note here guessed the blocker was locating `g_dacNotificationFlags`. That guess was
  wrong, and a second pass settled it against symbols rather than by signature scan. The public
  `coreclr.pdb` for the loaded runtime was fetched from the Microsoft symbol server and read with the
  repository's own `PdbFile` (a 45 MB PDB parsed in ~105 ms, GUID and age matching the image), which
  resolved `?g_dacNotificationFlags@@3IA` to rva `0x446990` exactly. So the flag can be located and
  written. **Writing it does nothing**, because the JIT notification does not read it. Disassembling
  the runtime through the repository's own decoder (Iced), against the PDB's names, shows the flag has
  three readers and no more — `DACNotify::DoModuleLoadNotification` (mask `1`),
  `DoModuleUnloadNotification` (mask `2`) and `DoExceptionCatcherEnterNotification` (mask `8`) — and
  `DACNotify::DoJITNotification` is **not** among them: it has no gate at all and stores its type code
  unconditionally. The six inert `.data` writes of the first attempt were writing a flag with no JIT
  bit in it. The JIT notification is instead gated by a *table*, `g_pNotificationTable`
  (`PTR_JITNotification`, rva `0x446998`): `JITNotifications::IsActive` is "is the table pointer
  non-null" and `JITNotifications::Requested` walks its 24-byte entries (module at `+8`, method token
  at `+0x10`). That table has exactly two references in executable code, both **readers** — its
  constructor and `DACNotifyCompilationFinished` — and **no in-process writer**. It is populated only
  from outside the process, by the DAC's `IXCLRDataProcess::SetCodeNotifications`
  (`CLRDATA_METHNOTIFY_GENERATED = 1`, IID `5c552ab6-…`), which is how SOS's `!bpmd` arms a cold
  method. That call needs a data target whose `ICLRDataTarget::WriteVirtual` works — a **writable**
  DAC — and a writable DAC is exactly what the design excludes: the overlay is read-only, and "read-only
  is enough because the control comes from the other side" is the load-bearing decision of the whole
  approach (see `DECISIONS.md`). So the deterministic first-call catch is not merely unproven, it is
  **incompatible with the read-only-DAC premise**. Reaching it would mean either a second, writable DAC
  instance driving `SetCodeNotifications` (the ICorDebug-shaped machinery this design set out to avoid)
  or hand-building the notification table in the debuggee (an undocumented, version-specific struct in
  memory allocated in the target) — both a great deal of fragile work for the one case the fallback
  misses: a method called exactly once, ever.
- ✅ Decode notification → plant. `DebugSession.OnClrNotification` recognises a real notification by its
  magic and, on a JIT one, re-resolves and plants every held breakpoint whose method now has native
  code. Correct and ready — it fires as the deterministic first-call catch the instant JIT
  notifications can be turned on.
- ✅ Pending managed breakpoints. `AddManagedBreakpoint` on a not-yet-compiled method holds it rather
  than refusing it, and `PlantPending` (which a caller polls, and which the notification handler calls)
  plants it once its method compiles.
- ◑ Tests: `AManagedBreakpointOnAColdMethodIsHeldAndPlantedWhenItCompiles` — a breakpoint set on a cold
  method is held, planted once the method JITs, and fires in it. It catches a *later* call, not the
  first, which is the honest limit: the "stops at the first call" the plan asked for needs JIT
  notifications, and those need a writable DAC the design does not have (above). This is the settled
  ceiling of the read-only approach, not a task still waiting on a flag.

### Phase 6 — the surfaces

**Phase 6 is complete.** The mixed-mode engine is reachable from the window and the agent, and a stop
shows both worlds at once.

- ✅ Panel: a managed breakpoint in native mode. A line clicked in the C# of a .NET program debugged
  natively is a managed breakpoint, not a native int3 at the IL's static address (which is not code
  that runs): `DebuggerViewModel.ToggleBreakpoint` routes a mixed-mode click to `MixedTarget` — the
  declaring type, the method's metadata token, the IL offset — and `DebugSession.AddManagedBreakpoint`
  resolves it through the DAC and plants a native int3 where the JIT put it. A mark set before the run
  is held (through "no CLR yet" and "type not loaded yet" as well as "not compiled yet", since a token
  from the opened assembly is a real method that will arrive) and planted by a pump as its method JITs
  — polling being inherent to the read-only DAC. `ManagedOverlay` resolves by token as well as by name;
  a new engine test sets a breakpoint by token before the CLR is up and watches it planted and fired.
- ✅ Panel: both worlds in one stop. At a mixed stop the DAC walks the managed stack of the thread that
  stopped and fills the Call stack pane beside the native registers, and the status names the stop in
  managed terms — `Stopped at 0x… — Namespace.Type.Method at IL_XXXX`. Locals and the managed Threads
  pane stay hidden there: those are ICorDebug's, which mixed mode does not use. Driven by a panel probe
  reading `Step <- Spin <- Main` back out of the pane at a managed breakpoint it set.
- ✅ MCP tools for native mode on a .NET target, within the manifest budget. No new tool: `debug_break`
  already routes by address, and in mixed mode the address of a managed method becomes a managed
  breakpoint over the native loop — the App wires the agent's native control to the same
  `ToggleBreakpoint` the panel uses. The agent reads the managed location at the stop through the
  status line the snapshot already carries. The one cost is a sentence in `debug_break`'s description,
  which moved the manifest guard from 6,400 to 6,600 on purpose (see `McpContractTests`), the third
  capability earning a sentence rather than a shaved description elsewhere.
- ✅ Tool descriptions that steer an agent to native addresses for native code. `debug_break` now says
  a .NET program debugged natively takes a managed breakpoint at a managed address and a native one
  elsewhere, so the agent aims each kind of address where it belongs.
- ✅ An ADR extending "Managed debugging goes through dbgshim…" with the third option it did not weigh —
  "Mixed mode puts the native loop in charge, and the DAC rides along without a debug port" in
  `DECISIONS.md`.

### Phase 7 — the cold method's first call, caught at the prestub

**Phase 7 is complete, and it reopens what Phase 5 closed.** Phase 5 concluded the first-call catch was
incompatible with a read-only DAC. That ruled out one route — the CLR's JIT notification, which needs a
writable DAC — but not the route a real debugger actually uses. This is that route, and it keeps the DAC
read-only: the DAC names addresses, the native loop does every write.

- ✅ Resolve the JIT prestub from the runtime's own PDB. `CoreClrSymbols` reads the loaded runtime's debug
  directory for its build id, fetches the matching PDB from the Microsoft symbol server once (by the
  build hash — that hash and nothing else leaves the machine), caches it on disk by build, and finds
  `PreStubWorker`'s address in it. Matched on the build GUID, not GUID-and-age: a PE and the PDB the
  server returns for it can carry different ages for one build (clr.dll says 3, its clr.pdb says 4).
  Non-fatal throughout — no PDB, no network, and the catch simply falls back to a later call.
- ✅ Catch the first call. An int3 on `PreStubWorker` is armed while a cold managed breakpoint waits.
  Every method's first JIT passes through it, and the MethodDesc being compiled is the worker's argument
  — rdx on CoreCLR's free `PreStubWorker`, and the resolver falls back to `MethodDesc::DoPrestub` (rcx)
  where a runtime has no free worker. `ManagedOverlay.MethodByHandle` turns that MethodDesc into a type
  and token, and if it is the method wanted, a breakpoint at the prestub's return — where the method now
  has native code — plants the real breakpoint, in time for that same first call to reach it.
- ✅ The details a real runtime forced. The method's native entry is the worker's return value (rax on
  both), used when the DAC has not yet caught up to the fresh code — which it has not on Framework, where
  the worker returns before the MethodDesc shows any native code. And a method's JIT triggers nested
  JITs whose prestub calls return through the same shared address, so the return breakpoint re-arms and
  acts only when the stack has unwound back to the frame the caught method's own prestub was called from.
- ✅ Armed before the method runs, not after. The catch is only as good as its timing: the int3 on
  `PreStubWorker` must be in place before the cold method JITs. Resolving the worker means loading the
  runtime's PDB — ~0.9 s for Framework's 24 MB `clr.pdb`, even warm from the on-disk cache — and if that
  ran on a background thread the process could reach the method first and slip past (the poll-plant
  fallback then catches it a call too late, so a once-called method like `OnStartup` is missed entirely).
  The runtime's own load event is the fix: the whole target is frozen there and no managed code has run
  yet, so the loop resolves the prestub **on that thread, from the on-disk cache only** (never a network
  fetch, which would hang the frozen loop) and arms before letting go. A cold cache — no PDB on disk yet —
  falls through to the background fetch that warms it for next time. `CoreClrSymbols.ResolvePrestub` grew
  an `allowFetch` flag for exactly this: the load-event resolve passes it false.
- ✅ Reached from the panel and the agent with no new surface: it is the same `AddManagedBreakpoint` a
  gutter click already drives, now armed the moment a cold method is held. The panel's pump seeds a
  gutter breakpoint into the session as soon as the run starts — before the runtime loads — so the
  load-event arm finds it waiting.
- ✅ Tests and verification. `AColdMethodCalledOnceIsCaughtAtItsFirstCallViaThePrestub` sets a breakpoint
  on a fixture method called exactly once — the case the fallback can never catch — and stops on it. On
  a real .NET Framework 4.8 app (CSPro Capture), a managed breakpoint on `CSProApp.Main.App.OnStartup`,
  driven natively through the panel, stops in `OnStartup` on its first call — reliably now, where before
  the arming race made it depend on how fast that build reached the method (CSPro does ~11 s of native
  startup before the CLR loads, then reaches `OnStartup` within a few hundred ms — the window the
  background arm was losing). From that first stop, IL stepping advances by IL boundary and a second,
  deeper breakpoint in the same method is hit on continue. The one thing the test cannot do in CI is
  fetch the PDB (a large download), so it skips where no symbols are available, which is also how the
  feature itself degrades — and on a cold cache the very first run of a once-called startup method can
  still miss it, because the PDB has to arrive before the arm; the run after warms the cache and catches.
- ◑ The overlay had to be made thread-safe for this: the loop's prestub dance and the panel's pump read
  the DAC at once, and ClrMD is not safe across threads. Its reads are now serialized. Worth noting as a
  constraint, not a gap.

## 7. Relationship to the existing record

- `DECISIONS.md`, "Managed debugging goes through dbgshim, and is a second debugger rather than a
  branch" — its facts hold. `ICorDebug` does take the port and cannot extend `DebugSession`. What it
  did not consider is an engine that takes **no** port at all, which is what the DAC does.
- `ROADMAP.md`, Phase 5, "Managed inspection without control (CLRMD)" — the premise was right and the
  conclusion was wrong. "No breakpoints and no stepping, so it is not debugging" holds only if ClrMD
  has to do the breaking. It does not: it supplies the address and the native loop breaks. That item
  is the foundation of mixed mode rather than a consolation prize for skipping it.
