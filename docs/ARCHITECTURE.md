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
│  Native: IR · X86Lifter · passes · Structurer · PseudoC     │
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
│  Strings: StringScanner · Binary: SpanReader                │
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

### 3.2 `StringScanner`

Finds printable ASCII and UTF-16LE runs in the raw file bytes (so the overlay is
covered too) and maps each hit back to RVA/VA and its section. UTF-16 is scanned
at both parities — packers do place wide strings at odd offsets. Bounded by
`MinLength` / `MaxLength` / `MaxResults` so a huge file cannot exhaust memory.

### 3.3 `SpanReader`

A `ref struct` cursor over `ReadOnlySpan<byte>` with little‑endian
`ReadU16/U32/U64`, `ReadBytes`, `ReadAsciiZ`, `Position/Remaining`. Throws
`PeParseException` on overrun.

### 3.4 PDB (`Spydate.Core.Pdb`)

`MsfFile` reads the Multi-Stream Format container: a superblock, a stream
directory, and per-stream block lists. `PdbFile` reads the two streams that
matter here — the info stream, for the GUID and age that tie a PDB to its image,
and the DBI header, which points at the symbol record stream holding `S_PUB32`
publics. Types, line numbers and per-module symbols are not read.

`PdbSymbols.TryLoadFor` probes the path recorded at build time, the same file
name next to the image, and `<image>.pdb`. **A PDB whose GUID and age do not
match is rejected** — symbols from a different build land at the wrong
addresses, which is worse than having none. Publics are mapped through the
section table (segment is a 1-based section index) and added without overwriting
existing names, since an export carries the undecorated name a reader expects.

### 3.5 Symbols

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
- `JumpTables` — switch dispatch recovery. Reads back over the instructions
  physically preceding an indirect jump (the linear run only: following branches
  backwards would mean guessing which path set the index) and matches two MSVC
  forms — 32-bit `jmp [idx*4 + table]`, where the entry is the address, and
  64-bit `lea base,[rip+X]` / `mov e,[base+idx*4+rva]` / `add`/`jmp`, where the
  entry is a delta from the base the `lea` loaded. The `cmp`/`ja` pair in front
  of the dispatch bounds the read; without one, entries are read until one is not
  executable or leaves the function. `FunctionDiscovery` follows the recovered
  targets, so the case bodies belong to the function instead of being left to the
  gap sweep, and `Function.JumpTables` records what was found.
- `XrefTable` / `XrefExtractor` — the cross-reference index. Every function that
  gets discovered is scanned for references: direct calls and jumps, indirect
  ones through a known slot (`call [iat]`), memory operands with a statically
  known address (RIP-relative or absolute) classified as read/write, `lea` and
  in-image immediates as address-taken. `XrefTable` indexes both directions and
  is locked, because discovery runs on a background thread while the UI reads it.
  `BinaryAnalysis.XrefsTo(va)` also resolves the enclosing function per site.
- `StringIndex` / `StringReferences` — the join between scanned strings and the
  xref index. Lookups match the whole range a string occupies, not just its
  start, because compilers routinely point into the middle of a literal
  (`lea rcx, [str+4]`). `StringReferences.Resolve` buckets every reference into
  the string containing it; `BinaryAnalysis.StringAt(va)` answers the reverse
  question for one address and backs the `; "text"` comments in disassembly.
  The scan behind `BinaryAnalysis.Strings` is lazy (`Lazy<T>`) because it reads
  the whole file — touch it off the UI thread.
- Discovery inputs beyond the entry VA:
  - **Unwind bounds.** `BinaryAnalysis` maps every non-chained `RUNTIME_FUNCTION`
    to its declared `[begin, end)` and passes the end into `Discover`. It is the
    authoritative extent — `Function.BoundsEnd` — while `EndVa` only covers what
    was decoded. After the descent, `SweepGaps` decodes the bytes inside those
    bounds that nothing branched to (jump-table targets), skipping `int3`/`nop`
    padding and abandoning a gap the moment it stops decoding, because it may
    hold the table itself.
  - **No-return calls.** Symbols whose name matches a known no-return API
    (`ExitProcess`, `RaiseFailFastException`, `abort`, …) mark an address as
    no-return, as does `int 0x29` (`__fastfail`); a single-block function whose
    only exit tail-jumps to one is recorded as a no-return thunk too. Decoding
    stops after such a call, because the following bytes are padding or the next
    function, not code — continuing produces junk instructions and bogus xrefs.
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

## 5b. User annotations and the project file

`Spydate.Core.Project` holds what the user has said about an address:
`AnnotationStore` (VA → name + comment, thread-safe, raises `Changed`) and
`SpydateProject` (load/save). They are kept apart from `SymbolTable`, which holds
what *analysis* found — a name typed by hand outranks a generated one and has to
survive re-analysis, so the store is applied on top rather than merged in.

`BinaryAnalysis.Annotations` is the live store. Setting a name there puts it into
the symbol table (remembering what it displaced), refreshes the discovered
`Function` that carries the name, and so reaches every call site, label, listing
and tab that asks what an address is called; clearing it restores the symbol
analysis had found, or removes the one the rename invented. A user name also wins
over the name a discovery *seed* carries — `EntryPoint`, an export — which is
otherwise passed in when the function is first discovered.

Stack slots are named too, but they are not addresses: `arg_0` exists in almost
every function, so slot names hang off the *function's* annotation, keyed by the
generated name. `LocalNamingPass` applies them at the end of the decompiler
pipeline, once the frame pass has invented the names there is something to replace.

The file is indented JSON with hex **RVAs**, not VAs, so it stays readable,
diffable and correct if the image is ever examined at a different base. It lives
beside the binary (`notepad.exe.spydate`) when that folder can be written to and
in `%LOCALAPPDATA%\Spydate\Projects` when it cannot — which is the normal case for
anything in System32. Both are probed on open, and a project whose recorded size,
timestamp and checksum do not match the image is refused with a reason.

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
                          DisassembleRange     Function/CFG ─→ X86Lifter ─→ IR ─→ passes ─→ Structurer ─→ PseudoCEmitter
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
