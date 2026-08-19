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

## Phase 1 — Native analysis depth 🚧
- ✅ Base relocations, TLS (callbacks as function seeds), load config (Control Flow Guard
  and SafeSEH tables as seeds), resource tree, Rich header
- ⬜ Use `RUNTIME_FUNCTION` end addresses as function bounds; ARM64 unwind format
- ⬜ Decode resource leaves (version info, manifests, string tables) instead of raw bytes
- ⬜ Map Rich header product ids to tool names (needs the undocumented prodid table)
- ⬜ Linear‑sweep gap filling + prologue heuristics for function discovery
- ⬜ Recognise CRT frame helpers (`__SEH_prolog4`, `__EH_prolog`, `__chkstk`)
  and no-return functions (`ExitProcess`, `__fastfail`) in discovery
- ⬜ Cross‑references (code→code, code→data, IAT usages) and Xrefs panel
- ⬜ String scanning (ASCII/UTF‑16) with xrefs
- ⬜ PDB symbol loading (MSF/DBI) for local symbol names
- ⬜ Authenticode signature summary (the certificate directory in the overlay)

## Phase 2 — Decompiler quality ⬜
- ⬜ SSA construction (cross-block propagation), register aliasing normalisation
- ⬜ x86 fastcall/thiscall register arguments; return-value inference; float args
- ⬜ Control‑flow structuring (if/else, loops, switch) — remove gotos
- ⬜ Name global data (`data_XXXX`, string literals) instead of raw addresses
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
