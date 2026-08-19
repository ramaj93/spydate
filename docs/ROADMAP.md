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

## Phase 2 — Decompiler quality 🚧
- ⬜ SSA construction (cross-block propagation), register aliasing normalisation
- ⬜ x86 fastcall/thiscall register arguments; return-value inference; float args
- ✅ Control‑flow structuring: `if`/`else if`/`else`, `while`, `do`/`while`,
  `break`/`continue`, from dominators and post-dominators. Edges no structure
  covers keep a `goto`, and only those blocks keep a label; every block is still
  emitted exactly once (asserted over every function of both notepads)
- ⬜ Switch statements (needs jump-table recovery below)
- ✅ Global data named instead of printed as addresses: `data_XXXX` (or the
  symbol), `&data_XXXX` for a pointer, `sub_XXXX` for a function pointer, and the
  text itself for a string literal
- ⬜ Type propagation from import signatures (small Win32 API DB)
- ⬜ Switch‑table (jump table) recovery
- ⬜ SSE/AVX float lifting subset

## Phase 3 — UI ⬜
- ⬜ CFG graph view (basic blocks as nodes)
- ⬜ Go‑to address, search (bytes / text / symbol), navigation history
- ⬜ Rename symbols / comments persisted to a `.spydate` project file
- ⬜ Split view: disassembly ↔ pseudo‑C synchronised by address
- ⬜ Settings: syntax (Intel/AT&T/MASM), font size, panel layout persistence
- ⬜ Light theme (needs light-background XSHD syntax palettes)
- ⬜ Dockable/floating tool windows; per-document context menus

## Phase 4 — Ecosystem ⬜
- ⬜ CLI (`spydate dump/disasm/decompile`) sharing the engine
- ⬜ Plugin API (IAnalyzer / IDocumentProvider)
- ⬜ ARM64 decoding
- ⬜ Signed release builds, installer
