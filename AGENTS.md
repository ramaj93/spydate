# AGENTS.md — working in the Spydate repository

This file is the operating manual for AI coding agents (and humans) working on
Spydate. Read it before changing anything. `CLAUDE.md` simply points here.

## 1. What Spydate is

Spydate is a Windows PE (Portable Executable) **disassembler and decompiler**
written in C# / .NET 10 with a modern WPF (Fluent) desktop UI.

It handles two kinds of PE files:

| Kind | Examples | Pipeline |
|------|----------|----------|
| **Native** (x86 / x64) | `.exe`, `.dll`, `.sys` built with MSVC/GCC/Clang/… | PE parse → Iced decode → function discovery / CFG → IR lifting → pseudo‑C |
| **Managed** (.NET) | any assembly with a CLR header | PE parse → metadata (System.Reflection.Metadata) → IL / C# via ICSharpCode.Decompiler |

Both are surfaced through one UI: a tree explorer on the left, tabbed document
views (overview, sections, imports/exports, hex, disassembly, decompiled code)
on the right.

Detailed design lives in `docs/`:

- `docs/ARCHITECTURE.md` — projects, layers, data flow, key types.
- `docs/DECOMPILER-DESIGN.md` — native IR, lifting, structuring, pseudo‑C.
- `docs/UI-DESIGN.md` — WPF/MVVM conventions, view/document model.
- `docs/PE-FORMAT.md` — condensed PE reference used by the parser.
- `docs/ROADMAP.md` — phased plan and current status.
- `docs/DECISIONS.md` — architecture decision records (ADRs).

## 2. Repository layout

```
Spydate.slnx
Directory.Build.props          shared MSBuild settings (LangVersion, nullable, analyzers)
Directory.Packages.props       central package versions — add versions HERE, not in csproj
src/
  Spydate.Core/                PE parsing, address mapping, symbols. No UI, no Iced.
  Spydate.Disassembly/         Iced-based x86/x64 decoder, function discovery, CFG.
  Spydate.Decompiler/          Native IR + lifter + pseudo-C; managed C#/IL via ILSpy engine.
  Spydate.App/                 WPF application (Wpf.Ui, AvalonEdit, CommunityToolkit.Mvvm).
tests/
  Spydate.Tests/               xunit tests for Core / Disassembly / Decompiler.
docs/                          design docs (see above)
```

Dependency direction is strictly one‑way:

```
Spydate.App → Spydate.Decompiler → Spydate.Disassembly → Spydate.Core
```

`Spydate.Core` must never reference Iced, ILSpy, or anything WPF. Analysis
projects must never reference WPF. Never introduce a cycle.

## 3. Build, run, test

```bash
dotnet build Spydate.slnx
```

```bash
dotnet test Spydate.slnx
```

```bash
dotnet run --project src/Spydate.App
```

- Target framework: `net10.0` for libraries/tests, `net10.0-windows` for the app.
- Warnings are not errors, **except nullable warnings** (`WarningsAsErrors=nullable`).
  Do not disable nullable; fix the annotation.
- Before finishing any task: build **and** run the tests. Report failures verbatim.

## 4. Coding conventions

- C# latest, file‑scoped namespaces, `readonly record struct` / `record` for
  immutable data, `sealed` classes by default.
- Private fields `_camelCase`; public members PascalCase; async methods end in `Async`.
- Prefer `ReadOnlySpan<byte>` / `ReadOnlyMemory<byte>` for binary data. PE
  parsing must be **bounds‑checked**; corrupt input must throw `PeParseException`
  (or return a partial result with `Warnings`), never `IndexOutOfRangeException`
  or hang. Untrusted input is the normal case.
- Addresses: use `ulong` for virtual addresses (VA), `uint` for RVAs and file
  offsets. Name variables `va`, `rva`, `offset` explicitly — never a bare `address`
  when the space is ambiguous.
- Iced types (`Instruction`, `Register`, `Mnemonic`, …) stay inside
  `Spydate.Disassembly` and `Spydate.Decompiler.Native.Lifting`. Everything else
  consumes `DecodedInstruction` / IR types.
- UI: MVVM only. No business logic in code‑behind beyond view plumbing. Views
  bind to ViewModels from `Spydate.App.ViewModels`. Long‑running analysis runs
  off the UI thread (`Task.Run`) and reports through observable properties.
- No new NuGet dependency without adding it to `Directory.Packages.props` and a
  one‑line justification in `docs/DECISIONS.md`.

## 5. Testing conventions

- xunit, one test class per production type where practical (`PeImageTests`,
  `X86DisassemblerTests`, …).
- Native PE tests use real system binaries under `%SystemRoot%\System32`
  (`kernel32.dll`, `notepad.exe`) guarded by `File.Exists` — skip gracefully if
  missing. Managed tests use the test assembly itself (`typeof(...).Assembly.Location`).
- Disassembler / lifter tests use inline byte arrays with the expected text.
- Never commit third‑party binaries to the repo. Put local samples in
  `samples/local/` (git‑ignored).

## 6. How to approach common tasks

**Adding a PE structure (e.g. TLS, relocations, resources)**
1. Add the record(s) in `src/Spydate.Core/PE/`.
2. Parse in `PeImage` (its own private `ParseXxx` method, guarded by the data
   directory being present and in bounds).
3. Expose as a property; add a test against `kernel32.dll`.
4. Surface in UI: add a document ViewModel + view, and a tree node in
   `ExplorerTreeBuilder`.

**Adding lifter support for an instruction**
1. Extend the `switch (instr.Mnemonic)` in `X86Lifter.LiftInstruction(...)`.
2. Add a unit test in `NativeDecompilerTests` with the encoded bytes and the
   expected pseudo‑C fragment (use `Assert.True(text.Contains(..), text)` so a
   failure prints the whole output).
3. Update the supported‑instruction list in `docs/DECOMPILER-DESIGN.md`.

**Adding a decompiler pass**
1. Implement `IIrPass` under `src/Spydate.Decompiler/Native/Passes/`.
2. Register it in `NativeDecompiler.DefaultPasses` at the right position
   (`StackFramePass` must stay first — everything else assumes named slots).
3. Add tests; run the `RealBinaryTests` smoke tests, which print the decompiled
   entry point of notepad/kernel32 (x64 and x86) — eyeball them for regressions.

**Changing the look**
All chrome lives in `src/Spydate.App/Themes/`: `Palette.Dark.xaml` (colours,
fonts, metrics) and `Controls.xaml` (compact square control templates). Views
must reference palette keys with `DynamicResource`, never hard‑coded colours,
and must not reintroduce `ui:` (Wpf.Ui) controls other than `FluentWindow`,
`TitleBar` and `SymbolIcon` — their Fluent styling is rounded and padded.

**Verifying the UI**
`dotnet run --project src/Spydate.App -- <file>` opens the file at startup.
For an automated visual check, `Start-Process` the built exe, then capture the
window itself with `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT=2)` — this works
even when the window is behind others. Call `SetProcessDPIAware()` in the
capturing script first, otherwise `GetWindowRect` returns virtualised
coordinates and the capture is cropped.
To click, steal focus with the `AttachThreadInput` + `SetForegroundWindow`
trick, then send real input with `mouse_event`; posted `WM_LBUTTONDOWN`
messages do not drive WPF reliably. Toolbar and menu elements also carry
`AutomationProperties.Name`, so UI Automation (`InvokePattern`) works for them.

**Adding a document view**
1. ViewModel derives from `DocumentViewModel` (`Title`, `Icon`, `Kind`).
2. View is a `UserControl` in `Views/Documents/`, registered in
   `App.xaml` via a `DataTemplate` keyed on the ViewModel type.
3. Open it from `MainViewModel.OpenDocument(...)`.

## 7. Things to avoid

- Don't load whole large files into `string`s for the UI; the hex and
  disassembly views must stay virtualized / on‑demand.
- Don't run analysis synchronously in constructors or property getters.
- Don't guess PE offsets — every read goes through `PeImage.RvaToOffset` or
  `SpanReader` with a bounds check.
- Don't change the public shape of `Spydate.Core` types casually; the UI, the
  analysis layers and the tests all consume them.
- Don't add "TODO" without a matching entry in `docs/ROADMAP.md`.

## 8. Status snapshot

See `docs/ROADMAP.md` for the authoritative list. As of Phase 0 (August 2026):
PE parsing (headers, sections, data directories, imports, delay imports,
exports, CLR header, debug/CodeView, x64 exception table), x86/x64
disassembly, recursive‑descent function discovery with CFG (seeded from entry
point, exports and `.pdata`), a native IR + lifter + stack‑frame / copy‑
propagation / simplification passes + goto‑based pseudo‑C emitter with
recovered locals and call arguments, managed C#/IL decompilation, and the WPF
shell with all core document views are implemented and covered by 44 tests.
Control‑flow structuring, SSA, type recovery, resources, relocations, PDB
symbols and a plugin API are planned.
