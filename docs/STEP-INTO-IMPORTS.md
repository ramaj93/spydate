# Stepping into imported module functions

Step into a `call` that goes to another module — an imported DLL, the CRT, a system library — and the
debugger should stop in that function and *show* it, the way it shows a function in the opened
binary. Today it steps in but has nothing to display, so the reader loses the thread at the first
call that leaves the file. This document is the design for closing that, for review before any of it
is built.

Status: **Phases 1 and 2 are done**, both verified in the window.

- **Phase 1** (commit "Show imported-module code when execution steps into it"): stepping where.exe
  into a call to `KERNEL32!GetModuleHandleW` opens `kernel32.dll!GetModuleHandleW` with the execution
  arrow on it. Disassembly only, on the §4 defaults (module file bytes translated through the
  run-time base; on-demand cached per-module analysis; step-into everything, no skip-list yet).
- **Phase 2** (commit "Name imported modules from their PDBs and open their functions on a click"):
  (a) clicking an import name in the opened binary — `KERNEL32!GetSystemTimeAsFileTime` — opens that
  function statically from the on-disk DLL, the same document a step-into would; and (b) a module's
  PDB is fetched from the Microsoft symbol server in the background (reusing `CoreClrSymbols`) and its
  functions folded into that module's analysis — verified by "Loaded 3210 symbols for KERNEL32.dll
  from its PDB" and the open document reloading with the richer names.

Phase 3 (§5 — decompiled C for foreign functions; fold into a general multi-binary view) is not
started, nor is the stepping skip-list for noisy system modules (§4/§6). Click-to-navigate inside a
foreign-module document still resolves against the opened binary, not that module — a Phase 3 item.

## 1. What the debugger already does, and what it doesn't

Two halves, and only one is missing.

- **Stepping already lands there.** Native "Step into" (F11) is a single machine-instruction step, so
  stepping on a `call` stops on the callee's first instruction — including a callee in another
  module. The debugger also already thinks in more than one module: `DebugSession` keeps a
  loaded-module map (`_modules`, `LoadedModule`), and breakpoints and patches are keyed by module +
  RVA, not by a bare address (see `DECISIONS.md` and the module-relative `BreakpointRequest`). So the
  stop in the imported function happens and is addressable.
- **There is nothing to show it in.** Spydate analyses exactly one binary — the opened one — into a
  single `BinaryAnalysis` (functions, symbols, xrefs, the decompiler). An imported DLL is never
  disassembled, so when execution stops inside it, `ShowWhereItStopped` asks the opened binary's
  analysis for the function containing an address that is not in it, gets nothing, and falls through
  to disassembling raw bytes at that address *as if they were in the opened image* — which they are
  not. The reader gets a fabricated listing at the wrong base, or a blank.

So the feature is not "make the debugger step into imports" — it already does. It is **"give the
imported module a listing to stop in,"** which is really *on-demand analysis and display of a second
module.*

## 2. The gap, precisely

To stop in `KERNEL32!Sleep` and show it the way we show `sub_140001554`, the window needs, for a
module that is not the opened one:

1. **Its bytes.** From the file on disk, or from the live process's memory. These differ — see §4.
2. **An analysis of it.** A `BinaryAnalysis` (or something lighter) for that module: functions,
   symbols, the disassembler, optionally the decompiler. Keyed by module, built on demand, cached.
3. **Its symbols.** Export names and, if available, a PDB (the CoreCLR symbol fetch in
   `CoreClrSymbols` is a precedent for pulling a PDB by hash) — so the listing reads
   `KERNEL32!Sleep`, not `sub_7FF8xxxx`.
4. **A document that knows its module.** The code document and its arrow are addressed by VA today,
   with the opened image's base assumed. A foreign-module document has to carry which module it is,
   so the arrow, the breakpoint gutter and the address column all resolve against that module's base
   (which the debugger's loaded-module map already tracks, since a DLL lands at a fresh address each
   run).
5. **`ShowWhereItStopped` routing by module.** On a stop, decide which module the address is in
   (from the loaded-module map), pick or build that module's analysis, and open its function — rather
   than always reaching for the opened binary.

## 3. Where this connects to what exists

- **Click-to-navigate (just shipped).** Clicking an import name — `KERNEL32!Sleep` — is currently a
  no-op on purpose (imports resolve to no function in the opened binary). Once a module can be
  analysed on demand, that same click becomes "open the imported function," which is the static
  cousin of stepping into it. The word resolver in `MainViewModel.ResolveFunction` is where that hook
  goes.
- **The multi-binary story.** An analysed imported module is close to "open a second binary in the
  same session." Whatever shape module analysis takes here should not paint the workspace into a
  corner that a general multi-binary view would have to undo.

## 4. Forks to decide first

- **Bytes from the file, or from process memory?** The file on disk is clean and complete (full
  headers, relocations, a real PE to analyse) but is not exactly what runs — it is unrelocated, and a
  patched or packed module differs. Live process memory is the truth of what executes and is already
  reachable (the mixed-mode overlay reads it with `ReadProcessMemory`), but it is a relocated image
  with no tidy on-disk structure to analyse. Likely answer: analyse the **file** for structure and
  names, translate addresses through the module's load base for display — matching how the debugger
  already keys everything by module + RVA.
- **How deep an analysis?** Full `BinaryAnalysis` per module (rich: functions, xrefs, decompiler —
  but heavy, and system DLLs are large) versus a **lightweight range disassembly** around the stop
  (cheap, no full discovery, crude — no function boundaries or decompile). A middle path: range
  disassembly first, promote to full analysis if the user keeps working in that module.
- **Stepping policy.** Step into *everything*, or skip system/framework modules unless asked (dnSpy
  has "step into" plus filters, and "step over" library code)? Stepping into `ntdll` on every call is
  rarely what the reader wants. A "don't step into these modules" list, or a modifier, is likely
  needed to make the feature usable rather than exhausting.
- **How much to show.** Just disassembly for a foreign module, or the decompiled C too? The native
  decompiler is not module-specific, so it *could* run on an imported module's function — but that is
  more surface to get right. Disassembly-first is the smaller promise.

## 5. Phased plan (shape, pending §4)

- **Phase 1 — show where it stopped, in disassembly.** On a stop outside the opened binary, identify
  the module from the loaded-module map, analyse that module's file on demand (cached), and open its
  function's disassembly with the arrow placed against that module's base. Symbols from exports.
  Stepping policy: a simple skip-list for the noisiest system modules. This alone closes the "loses
  the thread at the first call" complaint.
- **Phase 2 — names and reach.** PDB symbols for imported modules (reuse the `CoreClrSymbols` fetch
  pattern), and make the import-name click in a listing open the imported function statically.
- **Phase 3 — decompiled and general.** The decompiled C for foreign-module functions, and fold
  on-demand module analysis into whatever the general multi-binary view becomes.

## 6. Open questions for review

- File bytes vs. process memory for the imported module — the §4 default (file for structure,
  process base for address translation), or must it be exactly what runs?
- Full per-module analysis vs. lightweight range disassembly for Phase 1 — how much does the first
  useful version show?
- Stepping policy — a skip-list of system modules, a "step into anything" toggle, or both? What is
  the default?
- Is this its own subsystem, or the first concrete step of a general "more than one binary open at
  once" workspace — and should the design be drawn for the latter now?
