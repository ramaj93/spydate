# Spydate architecture

## 1. Goals

1. Open any Windows PE file safely (corrupt / hostile input is expected).
2. Present a faithful, navigable structural view (headers, sections, directories).
3. Disassemble native x86/x64 code with symbolized operands and function/CFG
   recovery; decompile to readable pseudo‑C.
4. For managed assemblies: metadata browsing, IL, and C# decompilation.
5. Keep the UI responsive on large binaries (tens of MB) — everything heavy is
   lazy, incremental and off the UI thread.
6. Keep the analysis engine UI‑agnostic so it can be reused from a CLI or tests.

## 2. Projects and layering

```
┌─────────────────────────────────────────────────────────────┐
│ Spydate.App (WPF, net10.0-windows)                          │
│  Views · ViewModels · Services (Workspace, dialogs)         │
└───────────────▲─────────────────────────────────────────────┘
                │
┌───────────────┴─────────────────────────────────────────────┐
│ Spydate.Decompiler (net10.0)                                │
│  Native: IR · X86Lifter · passes · PseudoCEmitter           │
│  Managed: ManagedDecompiler (ILSpy) · IlDisassembler        │
└───────────────▲─────────────────────────────────────────────┘
                │
┌───────────────┴─────────────────────────────────────────────┐
│ Spydate.Disassembly (net10.0)                               │
│  X86Disassembler (Iced) · DecodedInstruction · CFG          │
│  FunctionDiscovery · BinaryAnalysis (session)               │
└───────────────▲─────────────────────────────────────────────┘
                │
┌───────────────┴─────────────────────────────────────────────┐
│ Spydate.Core (net10.0, no external deps)                    │
│  PE: PeImage + headers/sections/imports/exports/CLR/debug   │
│      + relocations/TLS/load config/resources/Rich           │
│  Binary: SpanReader · Symbols: SymbolTable                  │
└─────────────────────────────────────────────────────────────┘
```

Rules: strictly downward references. Core knows nothing about Iced/ILSpy/WPF.

## 3. Core: `Spydate.Core`

### 3.1 `PeImage`

`PeImage` is an immutable, fully‑parsed view of a PE file held in memory
(`ReadOnlyMemory<byte>`). Construction:

```csharp
var pe = PeImage.Load(path);        // reads file, parses
var pe = PeImage.Parse(bytes, name); // from a buffer
```

Parsing is eager for headers/sections/directories (cheap) and eager but
guarded for imports/exports/CLR/debug/relocations/TLS/load config/resources/Rich
(each wrapped so one corrupt table doesn't
prevent the rest from loading; problems are added to `pe.Warnings`).

Key members:

| Member | Meaning |
|--------|---------|
| `DosHeader`, `FileHeader`, `OptionalHeader` | Raw header records (32/64 unified in `OptionalHeader`). |
| `DataDirectories` | 16 `DataDirectory` entries (`Rva`, `Size`). |
| `Sections` | `SectionHeader[]` with `Name`, `VirtualAddress`, `VirtualSize`, `RawPointer`, `RawSize`, `Characteristics`. |
| `Imports` / `DelayImports` | `ImportedModule[]` → `ImportedFunction[]` (name/ordinal/hint/IAT RVA). |
| `Exports` | `ExportTable?` (`ExportedFunction` with RVA or forwarder). |
| `ClrHeader` / `IsManaged` | CLR (COR20) header if present. |
| `Debug` | `DebugEntry[]` incl. CodeView PDB70 (GUID, age, path). |
| `ExceptionTable` | x64 `RuntimeFunction[]` from `.pdata` (function starts, chained flag). |
| `Relocations` / `RelocationCount` | `RelocationBlock[]` — one per 4 KiB page, `Absolute` padding dropped. |
| `Tls` | `TlsDirectory?` incl. `CallbackVas` (they run before the entry point). |
| `LoadConfig` | Security cookie, SafeSEH table, Control Flow Guard table (`GuardCfFunctionRvas`) and flags. |
| `Resources` | `ResourceNode?` tree: root → type → name → language → data entry. |
| `RichHeader` | Linker build stamp from the DOS stub (tool ids, build numbers, object counts). |
| `Is64Bit`, `Machine`, `ImageBase`, `EntryPointRva`, `Subsystem` | Convenience. |
| `RvaToOffset(uint) : uint?`, `OffsetToRva`, `RvaToVa`, `VaToRva`, `TryReadAt(rva, len)` | Address translation, bounds‑checked. |
| `SectionFromRva`, `SectionFromVa` | Section lookup. |
| `Warnings` | Non‑fatal parse issues. |
| `Overlay` | Data past the last section. |

### 3.2 `SpanReader`

A `ref struct` cursor over `ReadOnlySpan<byte>` with little‑endian
`ReadU16/U32/U64`, `ReadBytes`, `ReadAsciiZ`, `Position/Remaining`. Throws
`PeParseException` on overrun.

### 3.3 Symbols

`SymbolTable` maps VA → `Symbol(Name, Kind, Address, Size)`. Populated from
exports, import thunks (`kernel32!CreateFileW`), the entry point, and later PDB
data. Used by the disassembler formatter to render `call [kernel32!ExitProcess]`.

## 4. Disassembly: `Spydate.Disassembly`

- `X86Disassembler` wraps Iced's `Decoder` + `IntelFormatter`
  (`ISymbolResolver` backed by `SymbolTable`). `Decode(code, va, count)`
  yields `DecodedInstruction`s.
- `DecodedInstruction` — arch‑neutral record consumed by the UI and lifter:
  `Va`, `Rva`, `Length`, `Bytes`, `Mnemonic`, `Operands`, `Text`, `Flow`
  (`Next | UnconditionalBranch | ConditionalBranch | Call | Return | IndirectBranch | IndirectCall | Interrupt | Invalid`),
  `BranchTargetVa`, plus the raw Iced `Instruction` (`Native`) for lifting.
- `BinaryAnalysis.GetSeeds()` — where discovery starts, most trustworthy first:
  entry point, TLS callbacks, exports, non-chained `.pdata` entries, then the
  Control Flow Guard and SafeSEH tables (addresses the image itself declares as
  legal indirect-call targets). Deduplicated and filtered to executable memory.
- `FunctionDiscovery` — recursive‑descent from a single entry VA; follows
  direct branches, records (does not follow) calls, splits basic blocks at
  branch targets, stops at `ret`/indirect jumps/invalid bytes. Produces
  `Function` (entry VA, name, address-ordered `BasicBlock`s with
  successor/predecessor links, `CallTargets`, `IndirectCallSlots`, `Notes`).
  Whole-image discovery (`BinaryAnalysis.DiscoverAll`) seeds from the entry
  point, executable exports and every non-chained x64 `RUNTIME_FUNCTION`, then
  follows direct call targets transitively.
- `BinaryAnalysis` — analysis session for one `PeImage`: owns the disassembler,
  symbol table, discovered functions; provides `DisassembleRange` and
  `GetOrDiscoverFunction(va)`. Thread‑safe for concurrent reads.

## 5. Decompiler: `Spydate.Decompiler`

See `DECOMPILER-DESIGN.md`. Summary:

- **Native**: `X86Lifter` maps `DecodedInstruction` → IR statements;
  `IrFunction` holds `IrBlock`s; passes simplify; `PseudoCEmitter` prints C‑like
  output. `NativeDecompiler.Decompile(Function)` ties it together.
- **Managed**: `ManagedAssembly` (metadata browsing: namespaces → types →
  members) and `ManagedDecompiler` (`DecompileType`, `DecompileMethod`,
  `DecompileWholeModule`, `DisassembleIl`) built on `ICSharpCode.Decompiler`.

## 6. App: `Spydate.App`

See `UI-DESIGN.md`. `WorkspaceService` loads a file (`PeImage`), builds a
`BinaryAnalysis` (native) and/or `ManagedAssembly` (managed) on a background
thread and exposes an `OpenedBinary`. `MainViewModel` builds the explorer tree
and opens `DocumentViewModel`s in the tab strip.

## 7. Data flow (native file)

```
File → PeImage.Load ─┐
                     ├─→ BinaryAnalysis(pe) ─→ FunctionDiscovery.Run(seeds)
SymbolTable(pe) ─────┘         │                      │
                               │                      ▼
                          DisassembleRange     Function/CFG ─→ X86Lifter ─→ IR ─→ passes ─→ PseudoCEmitter
                               │                                                    │
                               ▼                                                    ▼
                    DisassemblyDocument (AvalonEdit)                     DecompiledDocument (AvalonEdit)
```

## 8. Threading model

- `PeImage` and analysis results are immutable after construction → safe to
  share across threads.
- `BinaryAnalysis` caches functions in a `ConcurrentDictionary`.
- ViewModels are UI‑thread only; services return `Task`s.

## 9. Error handling

- `PeParseException` for fatal format errors (not a PE, truncated headers).
- Non‑fatal issues → `PeImage.Warnings` (shown in the Overview document).
- Disassembler never throws on bad bytes: invalid opcodes yield
  `Flow = Invalid` / `db` pseudo‑instructions.
- Lifter never throws on unsupported instructions: emits `IrAsm` passthrough
  and records a warning on the `DecompiledFunction`.
