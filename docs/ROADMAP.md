# Roadmap

Legend: ✅ done · 🚧 in progress · ⬜ planned

## Phase 0 — Foundation ✅
- ✅ Solution layout, central package management, docs, AGENTS.md
- ✅ `Spydate.Core`: PE parser (DOS/NT/optional headers, data directories,
  sections, imports, delay imports, exports, CLR header, debug/CodeView,
  x64 exception table), RVA/VA/offset mapping, `SpanReader`, `SymbolTable`
- ✅ `Spydate.Disassembly`: Iced x86/x64 decoder wrapper, symbol‑aware
  formatting, recursive‑descent function discovery, basic blocks + CFG,
  whole-image discovery seeded from entry/exports/`.pdata`
- ✅ `Spydate.Decompiler`: native IR, `X86Lifter` (core integer subset),
  StackFrame (locals/args/call arguments) / CopyPropagation /
  AlgebraicSimplification passes, `PseudoCEmitter` (goto‑based);
  managed `ManagedAssembly` + `ManagedDecompiler` (C# / IL)
- ✅ `Spydate.App`: dense IDE shell (menu bar, toolbar, explorer + output tool
  windows, square document tabs, status bar) on an in-house compact dark theme;
  documents (overview, headers, sections, imports, exports, hex, functions,
  disassembly, pseudo‑C, C#/IL), go-to address/symbol, output log and warnings
  panel, drag & drop, command-line file argument
- ✅ Tests for PE parsing, disassembly, lifting, real-binary smoke tests (x64 + x86)

## Phase 1 — Native analysis depth ✅
- ✅ Base relocations, TLS (callbacks as function seeds), load config (Control Flow Guard
  and SafeSEH tables as seeds), resource tree, Rich header
- ✅ `RUNTIME_FUNCTION` end addresses as function bounds, with a sweep of the bytes
  the recursive descent never reached
- ✅ ARM64 unwind format: 8-byte .pdata entries, packed and .xdata forms, both reduced
  to the same begin/end shape the x64 table produces (ARM64 disassembly is Phase 4)
- ✅ Resource leaves decoded: version blocks, manifests (dark XML highlighting) and
  string tables open as text; version info also appears in the overview
- ✅ Rich header: ids, build numbers and object counts are reported, and the checksum is
  recomputed from the DOS stub to prove the header is the linker's own. Product ids are
  **not** mapped to tool names: the table is undocumented and unverifiable here, and a
  wrong toolchain label is worse than none (see DECISIONS.md)
- ✅ Gap sweeping: after seeds and calls are exhausted, the leftover bytes of executable
  sections are scanned for prologues (x86 notepad: 606 → 677 functions)
- ✅ No-return functions (`ExitProcess`, `__fastfail`, `abort`, …, and thunks that
  tail-jump to one) end a code path instead of decoding the bytes after the call
- ✅ CRT helpers named from the load config (`__security_cookie`, `_guard_check_icall`,
  `_guard_dispatch_icall`) and from instruction signatures (`__chkstk`/`_chkstk`,
  `__security_check_cookie`, `__SEH_prolog4`, `__EH_prolog`)
- ✅ Cross‑references (calls, jumps, reads, writes, address-taken, IAT usage),
  Xrefs panel and a per-function reference count
- ✅ String scanning (ASCII + UTF‑16, both parities) with a Strings document
- ✅ Strings linked to the code that references them: reference counts and a
  "referenced only" filter in the Strings view, the Xrefs panel following the
  selected string, and string literals annotated inline in disassembly
- ✅ PDB symbol loading: MSF container, info stream identity (GUID/age must match the
  image's CodeView record), S_PUB32 publics and per-module S_GPROC32/S_LPROC32
  procedures — the latter naming file-local functions and carrying their code size —
  mapped through the section table and used as discovery seeds
- ✅ Authenticode summary: the certificate table decoded to signer, issuer, serial,
  digest algorithm, validity window and RFC 3161 timestamp (described, not verified)

## Phase 2 — Decompiler quality ✅
- ✅ Cross-block propagation: values every predecessor agrees on reach a block from
  outside it, so a register set in one block reads as its value in the next. Only
  values that cannot change behind the analysis (constants, registers, frame
  addresses, literals) cross a boundary; a loop header inherits nothing rather than
  guessing. Phi nodes and full SSA renaming are still not built
- ✅ Dead-code elimination over whole-function liveness: assignments no one reads
  again go, and a call result nobody wants loses its `rax =`. A call keeps its
  argument registers alive, and a block with unknown successors keeps everything
- ✅ Return-value inference: a function that never writes the accumulator was
  returning whatever its caller left there, so it is typed `void` and its `ret`
  loses the value (notepad: 49 of 672 functions on x86, 15 of 520 on x64)
- ✅ x86 `__fastcall` / `__thiscall`: the question is put to the callee — a function
  whose entry block reads `ecx` before writing it was handed something in it — so
  calls gain their register arguments and the function itself declares them, under
  the register's own name (notepad x86: 208 thiscall-shaped, 81 fastcall-shaped)
- ✅ Float arguments: the callee is asked which of `xmm0`-`xmm3` it reads, so a
  double passed in `xmm1` is looked for there instead of in `rdx`, where it never
  was. `ldexp(double, int)` comes back with the first slot float and the second
  not — a distinction no call site can make on its own (ucrtbase x64, first 1200
  functions: 54 arguments now shown in the xmm register they arrive in)
- ✅ Control‑flow structuring: `if`/`else if`/`else`, `while`, `do`/`while`,
  `break`/`continue`, from dominators and post-dominators. Edges no structure
  covers keep a `goto`, and only those blocks keep a label; every block is still
  emitted exactly once (asserted over every function of both notepads)
- ✅ Switch statements: a recovered table lifts to `IrSwitch` and structures into
  `switch`/`case`, with arms in address order so a body that runs off its end falls
  through as C says it does, and `break` inside an arm meaning the switch
- ✅ Global data named instead of printed as addresses: `data_XXXX` (or the
  symbol), `&data_XXXX` for a pointer, `sub_XXXX` for a function pointer, and the
  text itself for a string literal
- ✅ Import signatures read from the DLLs on disk, and still not from a
  hand-typed table. An x86 `__stdcall` export states its stack argument count in
  its own `ret N`; api set names (`api-ms-win-core-*`) are redirected through
  `apisetschema.dll`, and one-instruction export thunks are followed, without
  which four fifths of a modern binary's imports resolve to nothing. x86 notepad:
  292 of 307 imports read, 144 calls given arguments they were missing and 169
  relieved of pushes belonging to an outer call. x64 notepad: 296 calls gained
  arguments, none lost. Argument *names* and types beyond integer-versus-float
  are still not claimed — they are not in the binary (see DECISIONS.md)
- ✅ Switch-table recovery: the 32-bit `jmp [idx*4 + table]` form and the 64-bit
  `lea base,[rip+X]` / `mov e,[base+idx*4+rva]` / `add`/`jmp` form, bounded by the
  range check in front of them and validated entry by entry; the case bodies are
  then followed as part of the function (kernel32 x64: 8 tables, shell32: 14)
- ✅ Scalar SSE lifting: `addsd`/`mulss`/… as arithmetic, `cvtsi2sd`/`cvttsd2si` as
  casts to and from `float`/`double`, `sqrtsd` as a call, `comisd` as a comparison,
  and the VEX three-operand forms. The packed forms stay as inline asm rather than
  pretending a vector is a number (notepad x86: 202 → 162 unlifted instructions)

## Phase 3 — UI 🚧
- ✅ Control-flow graph (F7, or View ▸ Control-flow graph): a box per basic block
  laid out with the entry at the top and control running downwards, edges coloured by
  how control got there — fall-through, branch taken, jump, loop, switch arm. Clicking
  a block points the Xrefs panel at it and lets it be renamed or commented like any
  other address; Ctrl+scroll zooms; "Export SVG" writes the same drawing to a file.
  A function past 600 blocks says so instead of drawing something unreadable
- ✅ Navigation history: back and forward over everywhere you have been, with the
  next stop named in the tooltip. A closed tab is reopened from what it was, so
  going back does not dead-end
- ⬜ Search (bytes / text / symbol) — go-to address and symbol already work
- ✅ Rename (F2) and comment (Ctrl+;) any address — function, global or label —
  persisted to a `.spydate` file beside the binary, or in a per-user store when
  that folder cannot be written to. A name applies on top of what analysis found,
  so clearing it restores the original; a project made for a different build is
  refused with a reason, the way a mismatched PDB is
- ✅ Stack slots (`arg_0`, `local_18`) renamed too, keyed by the generated name under
  the function they belong to, since that is what they are: `arg_0` exists in most
  functions and naming one must not name them all
- ✅ Split view (F6, or View ▸ Side by side): one function in both panes, each
  following the other by address. Every line that stands for an instruction states
  its address, so picking a line in either pane moves the other to it; an
  instruction the decompiler folded away lands on the statement it ended up inside
- ⬜ Settings: syntax (Intel/AT&T/MASM), font size, panel layout persistence
- ⬜ Light theme (needs light-background XSHD syntax palettes)
- ⬜ Dockable/floating tool windows; per-document context menus

## Phase 4 — Ecosystem 🚧
- ✅ MCP server (`spydate-mcp`): the engine as agent tools over stdio, so Claude Code or any MCP
  client can run the naming loop — orient, find something worth reading, read it, name it, follow it.
  Thirteen tools, answers as compact text with every truncation stated, and writes that merge into the
  same `.spydate` file the window reads, so both can work on one binary at once. The window watches
  the file and retitles its documents as the agent renames things. See `docs/MCP.md`
- ✅ Assistant panel with bring-your-own-key: OpenAI, OpenRouter, DeepSeek and Anthropic behind one
  `IChatClient`, running the same thirteen tools the MCP server publishes against the analysis the
  window already has open — so a name it gives shows up in the documents at once. Keys are encrypted
  to the Windows account and kept apart from the settings file, and the model is chosen from what the
  provider says it has rather than typed from memory, with the box still editable for anything it
  does not list
- ⬜ CLI (`spydate dump/disasm/decompile`) sharing the engine — largely subsumed by the MCP server,
  which is the same headless surface with a different front end
- ⬜ Plugin API (IAnalyzer / IDocumentProvider)
- ⬜ ARM64 decoding
- ⬜ Signed release builds, installer

## Phase 5 — Managed (.NET) depth 🚧

The window has decompiled managed assemblies since Phase 0 — C# and IL, from the ILSpy engine
behind `ManagedAssembly` / `ManagedDecompiler`. Nothing else has caught up with it. The agent
surface cannot see a .NET assembly at all, the patch path assembles x86 and only x86, and the
debugger is a raw Win32 debug loop that has never heard of the CLR. This phase closes that,
in the order the gaps actually hurt.

One thing applies throughout: single-file and NativeAOT publishes have no IL in them. `IsManaged`
is false, there is no metadata, and every item below is silent about such a binary by design — the
native side already reads it, because native is what it is.

- ✅ **Managed analysis for agents.** `Spydate.Mcp` never loads `ManagedAssembly`; every tool
  resolves its target to a virtual address through `Targets.Resolve`, and a managed entity has no
  address to resolve to. The fix is to widen the tools rather than add a second set: `read_function`
  takes a type or member name and a `view` of `csharp` or `il`, `find_symbol` searches metadata
  names, `get_overview` describes an assembly as an assembly. **No new tool at all** — not even
  `list_types`, which was in the first draft of this plan and did not survive the manifest budget:
  every tool's schema is sent on every turn of every conversation, including the ones that never
  open a .NET file, and an empty `find_symbol` query lists types already. Reading a type's members
  is `read_function`, since in managed code the listing and the source are the same answer
- ✅ **Managed cross-references and strings.** `ManagedReferences` walks every method body once and
  answers all of it: `xrefs` in both directions, `find_strings` from the literals with the method
  that loads each, and `list_imports` as what the assembly calls elsewhere — which is its real
  import list, since the PE import directory of a .NET file holds one entry, for the loader. The
  piece with no library behind it, as expected: ILSpy's analyzers live in the ILSpy application
  rather than in the package. Decided by operand *type* rather than by a list of opcodes, and the
  opcode table is taken from `System.Reflection.Emit.OpCodes` instead of transcribed, because a
  table that is wrong in one place mis-decodes every instruction after it in that method. Two
  things had to be decoded rather than skipped: a MethodSpec, or every call to a generic method
  would be counted against one instantiation; and a TypeSpec, without which `List.Add` and
  `HashSet.Add` collapsed into one fictional member with ninety calls against it
- ✅ **IL patching.** `PatchStore`, `PatchWriter` and the overlap and original-bytes checks are
  untouched: an IL patch is a change to bytes at an RVA, which is what they always took.
  `ManagedBodies` supplies the RVA a body's IL actually starts at — read from the method header
  rather than taken from `MethodBodyBlock`, which hands back the IL without saying where it began —
  and the IL listing carries that address against every instruction, so `AddressText.FromLine`,
  `Targets.Resolve` and the patch tool all work on it unchanged. `IlAssembler` is the subset;
  anything taking a metadata token is refused by name, because writing a token that is not already
  in the tables means adding a row and moving everything after it, and the one thing this promises
  is that nothing moves.
  The part with no native equivalent is `IlStack`. x86 is a machine with registers, so NOPping a
  call leaves a wrong value in one and the program runs on being wrong — usually the point. IL is
  verified before it runs, so the same edit leaves the stack a different depth and the method is
  refused outright with an `InvalidProgramException`, at first call, nowhere near the patch. So the
  depth is computed for what goes and for what replaces it, and a mismatch is reported as the
  number of values it is out by, which is the fix. Depth only: types are not checked, and a patch
  that removes a call says so, since removing `call uint8[] ReadAllBytes(string)` is depth-neutral
  and still will not verify.
  The three limits stated up front all hold. New metadata is out of scope. A ReadyToRun image is
  detected from `ClrHeader.ManagedNativeHeader` and the answer says the patch may correctly do
  nothing. A patched copy's strong name breaks, as its Authenticode signature already did
- 🚧 **Managed debugging (ICorDebug).** Launch, attach, hold before anything runs, breakpoints by
  `(module, method token, IL offset)` — including ones set before their module exists, which is the
  normal case — stops reported as a method and an IL offset with a word for whether that offset is
  exact, stepping one IL instruction at a time (in, over, out), locals and arguments read as typed
  values, continue, and terminate. `ManagedDebugSession` is a second `IDebugControl`, as planned.
  It is wired into both front ends. The four `debug_*` tools drive it when the open binary is
  managed, taking a method rather than an address and reporting the frame's values rather than
  registers; no new tool, on the usual reasoning. The Debug panel swaps its "Threads &amp; registers"
  pane for a **Locals** pane and grows a step-out button, and the assistant drives the same session
  the analyst is watching, as it always has for native.
  The window's IL view is addressed, so a click in its gutter sets a breakpoint on the method and
  offset that line stands for, and a stop puts the arrow back on the line it is at — the same body
  index answers both directions, so the two cannot disagree. Breakpoints set before a run are held
  and planted while the process is held at the start.
  A breakpoint can be cleared from a running process, without stopping it first: the session keeps
  the runtime's own object beside the method and offset it is for, and turns it off with
  `Activate(false)` — releasing the pointer alone gives back a handle and leaves the breakpoint
  firing, because the runtime holds a reference of its own. One still waiting for its module is only
  a note and is forgotten; one whose process has exited is dropped without asking a dead object to
  deactivate; one the runtime refuses stays in the list and keeps its dot, since the dot describes
  the process rather than the request. Clearing one the assistant set takes its dot off the listing
  too, and the other way round.
  Neither debugger's debuggee needs a console window: `ShowConsole` decides, and the test suite
  turns it off so a run does not throw thirty windows in front of whoever is working.
  Three things only a run of the panel itself showed, none of which a test could have: the managed
  Start validated a host and then launched the assembly anyway, so a modern .NET DLL — which is
  every .NET assembly with an entry point, the `.exe` beside it being a native launcher — produced a
  process with no runtime and a thirty-second wait; every ICorDebug call from the window's STA thread
  threw before it was made; and `CorDebugMappingResult` is a flag set, so the word beside a stop's
  offset said "unmapped" on every ordinary stop. The panel is now driven end to end from a probe
  outside the repo, which is what found all three.
  The C# view carries breakpoints too, which needed no PDB. A portable PDB's sequence points map IL
  onto lines of a source file nobody here has; what this view shows was invented by the decompiler a
  moment ago, so the only thing that can say which line is which is the decompiler — ILSpy annotates
  every syntax node with the IL it was made from, and a token writer that knows its own line records
  them as the text is written. (Its `CreateSequencePoints` cannot be used from outside the ILSpy
  application: it reads node locations that a decompiled tree does not have, so every point it hands
  back says line zero.) Three corrections were needed on top of that, and each was invisible until
  the thing was run: an async or iterator method's offsets belong to its state machine's `MoveNext`,
  not to the stub the reader is looking at; a breakpoint binds only where the evaluation stack is
  empty, so the offset walks back from the expression ILSpy names to the statement holding it — the
  runtime refuses the rest with an asynchronous `BreakpointSetError` long after saying yes; and an
  offset must sit at an instruction boundary or the line carries no address at all. The address goes
  in a trailing comment, the way pseudo-C has always done it, so the margin, the caret and the
  execution arrow all work without learning anything new.
  Stepping moves a C# statement, not an IL instruction. A statement is the run of IL between two
  points where the evaluation stack is empty — that is what the language guarantees and what the JIT
  records — so the range needs no decompiler and no symbols, only the IL both the debugger and the
  reader are looking at. `StepRange` over that range lands on the next statement; over one
  instruction it lands on the next instruction, which is what `Step one IL instruction` (Ctrl+F11)
  still does for anyone reading the IL view. A Debug build's `nop` between statements is not a
  boundary, or every second press moved a single byte onto the line the reader was already expecting.
  A stop is shown in the C# of the method it is in, never as a native listing: those bytes are IL,
  and disassembling them produced a page of invented x86 on every step. The execution arrow is placed
  by nearest-preceding line, so a `for` header — three statements in IL, one line on screen — keeps
  the arrow on the `for` rather than blanking it for two presses out of three.
  Values are followed rather than pointed at: an array reads as `string[1] { "C:\Windows\..." }`, an
  object as `McpOptions { ReadOnly = false, MaxFunctions = 20000, … }`, a boxed value as what is in
  it, and the type name comes from the module's own metadata rather than from `IMetaDataImport`.
  Two levels deep and six wide — a graph has no end and a line has a width — so what is not shown is
  an expandable tree, which is the next thing worth building here.
  .NET Framework is debugged too, and until recently was not debugged at all. dbgshim's handshake
  answers for CoreCLR and only for CoreCLR, and a .NET Framework program simply never signals the
  event it waits on — so the program was launched, ran to the end undebugged, exited, and half a
  minute later the panel reported that its runtime had never become debuggable. Nothing failed
  anywhere. Those go through the metahost instead — `CLRCreateInstance`, `GetRuntime`,
  `GetInterface(CLSID_CLRDebuggingLegacy)` — and `ICorDebug::CreateProcess` does the launching, which
  is the call CoreCLR does not implement. Which route a file takes is decided from the file before
  anything runs: a `.runtimeconfig.json` beside it, else its `TargetFrameworkAttribute`, else whether
  it binds `mscorlib` or `System.Runtime`. Bitness is checked at the same time and refused with a
  sentence, because ICorDebug does not cross that line and the failure is otherwise the same silence.
  A .NET Framework 4.8 WPF application now holds before its first instruction, breaks on a line of
  its own decompiled C#, and reads its arguments as values.
  Starting no longer happens on the window's thread. That wait is bounded by a timeout rather than by
  the debuggee, and on the UI thread it froze the whole application for its duration — which is what
  anyone starting a .NET Framework binary saw, on top of it not working.
  Locals is a tree now, laid out as every Locals window is — Name, Value, Type — with an expander on
  anything that has an inside. `this` is `this`, parameters have their names from the metadata, and
  locals have the names the decompiler gave them in the C# view beside the pane rather than `V_0`.
  The Type column is the declared type, from the field, parameter or local signature, with the
  runtime type after it in braces when they differ — so a null still says what it would have been,
  and `object {int}` says what is in the box. An object opens to every field its whole class
  hierarchy declares, read through the value's exact type; enums read as their member (`Angry`,
  `Read | Write`), `int?` as what it holds, pointers padded to their width. A row is found again from
  the frame by a path each time it is opened, never by a kept runtime object, so rows that were open
  stay open across a step and show what they hold now.
  The Threads tab is back for .NET, and says what a Threads window says: OS id, managed id, category,
  name, where it is, priority, app domain and state. Picking a thread moves the values, the arrow and
  stepping to it without letting the process run. A parameter the runtime will not read — the
  framework's own code is optimised — is listed as unavailable rather than dropped.
  Found on the way: `ICorDebugBoxValue`'s id had been `ICorDebugEval`'s since it was written, so no
  boxed value was ever unboxed; a comment beside it claimed a test pinned it, and none did. One does
  now, against a live box.
  The call stack is a pane of its own: the frames of the thread being looked at, innermost first,
  walked through the runtime's chains so a waiting thread's managed frames are found below the native
  wait rather than reported as "not in managed code". Picking a frame moves the locals and the arrow
  to it, so a caller's variables are readable, not only the method that stopped. Native and runtime
  frames are listed too, greyed, so the shape of the stack is honest.
  Properties are read by running their getters — function evaluation, the one thing here that runs
  the program's own code. It is fenced: only when a person opens the object, never on its own; every
  other thread suspended for the duration; a timeout, and an abort for a getter that overstays; a
  getter that throws reports what it threw. Static getters and auto-properties both run; a getter on
  a generic type needs its type arguments passed and is not evaluated yet, reading "(cannot
  evaluate)". The value is a display, not an expandable subtree — an eval result is not reachable
  from a frame by a path, so there is nothing to walk to open it. `ICorDebugEval`'s id, which the box
  bug had wrongly borrowed, and every chain, frame and enum id were asked of a live process first.
  An object a getter returns opens like any other. It is reachable from no frame, so the session keeps
  a strong handle on it and the row's path names that handle instead of a slot; the handles go the
  moment the process runs on, because one held past its usefulness is an object the collector may not
  take. A getter on a generic type runs too — a method on one cannot be called without the type
  arguments its type was instantiated with, so they are read off the value's exact type and passed
  alongside. Statics have a row of their own rather than being mixed into what the object holds: a
  static belongs to the type, and is read through the class and a frame, since which app domain and
  which thread it belongs to is decided by where execution is.
  A value can be written back: a number, a character, a bool, an enum member by name, null, or text
  in quotes — a string is made in the debuggee first, which is an evaluation, and then the reference
  is pointed at it. It is behind a prompt rather than typed into the grid, because a value written by
  accident into a running program is not something to make easy, and nonsense is refused with the
  value left as it was rather than written as zero.
  A recorded patch now goes into a managed run, the way it always did into a native one — but written
  into the method's IL as its module loads, which for managed code is the only moment it takes: after
  the JIT has turned that IL into native code, changing the IL changes nothing. It is nothing like the
  native write. The IL is not at `base + RVA` (a managed image is not mapped section-for-section, and
  that address reads as zeroes); it is reached through the runtime's IL-code object for the method
  (`GetFunctionFromToken` → `GetILCode` → `GetAddress`), the same route a breakpoint takes. The IL
  page is read-only, so the write is `VirtualProtectEx` + `WriteProcessMemory` with the protection put
  straight back — the runtime's own `WriteMemory` refuses it — and a patch is checked against the bytes
  it expected first, so one cut against a recompiled or precompiled method is refused rather than
  written blind. Patches are named by token and IL offset, so toggling one on mid-run reaches nothing
  already compiled; a managed patch is applied at launch and a change waits for a relaunch.
  Still to do: writing through a property's setter, which is a second evaluation; and evaluating an
  expression somebody types, which is a watch window and a language of its own.
  Four things cost real time and are worth knowing before touching this again. Registering for
  runtime startup yields an `ICorDebug` and nothing else — `DebugActiveProcess` is a separate act,
  and without it the runtime reports nothing at all. A `[ComImport]` interface takes its vtable
  slots from the methods declared in it alone, so `ICorDebugProcess : ICorDebugController` puts
  `GetID` on the slot holding `Stop`. `ICorDebugCode::CreateBreakpoint` never returns when called on
  the thread delivering the callback, though the calls needed to reach it answer there fine.
  And `Step` moves one *machine* instruction: stepping IL means `StepRange` over the current offset.
  Interface ids are pinned by a test that asks a live value what it supports, because three written
  from memory were wrong and a wrong one reads as "not available" rather than as a mistake
- ⬜ **Managed debugging, the rest.** Breakpoints are
  `(MethodDef token, IL offset)` pairs, which is exactly what the IL view shows, and the portable
  PDB's sequence points carry them onto C# lines; `ICorDebugStepper` steps statements rather than
  instructions, and `ICorDebugValue` reads a local as a typed value rather than as a stack word.
  Two structural consequences. It cannot extend `DebugSession`: ICorDebug takes the process's
  native debug port and intends to be the only debugger attached, so this is a second
  `IDebugControl` implementation chosen when the binary is opened, not a branch inside the
  existing one — which is the argument for that interface having existed all along. And
  `DebugSnapshot` is native-shaped (registers, stack words, VAs), so managed frames, locals and
  IL offsets go beside those rather than through them. Budget it honestly: there is no usable
  managed wrapper — MdbgCore is .NET Framework-era and CLRMD is read-only — so this is
  hand-written COM interop over several dozen interfaces plus `dbgshim` startup
  (`RegisterForRuntimeStartup`), with callbacks arriving on ICorDebug's own thread and every one
  of them needing a `Continue()`. dnSpy spent roughly ten thousand lines here
- ⬜ **Managed inspection without control (CLRMD).** `Microsoft.Diagnostics.Runtime` against a
  live process or a dump: managed thread stacks, heap objects, field and static values. No
  breakpoints and no stepping, so it is not debugging — but it answers "what is actually on the
  heap" and "what do these managed frames say" for a fraction of the work above, and it is
  useful whether or not ICorDebug ever lands
