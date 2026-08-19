# UI design (Spydate.App)

## 1. Look & feel

Spydate uses a **dense, square IDE look** in the Visual Studio / dnSpy family:
flat greys, single-pixel borders, no rounded corners, no large padding.

- The theme is written in‑house: `Themes/Palette.Dark.xaml` (colours, fonts,
  metrics) plus `Themes/Controls.xaml` (control templates). Both are merged in
  `App.xaml` **after** the Wpf.Ui dictionaries and override them.
- Wpf.Ui is kept only for the window chrome (`FluentWindow` + `TitleBar`) and
  the `SymbolIcon` glyph font. Its Fluent control styles are not used —
  `ControlCornerRadius`/`OverlayCornerRadius` are redefined to `0` and every
  control Spydate uses has its own compact template.
- Metrics: 12px UI font, 12px monospace, 18px grid/tree rows, 21px headers and
  inputs, 22px tabs, 4px splitters, 12px scroll bars (no arrows).
- Palette keys (all resolved with `DynamicResource`):
  `Chrome.*` (title bar, menu, toolbar, status bar), `Panel.*` (tool windows),
  `Document.*`/`Tab.*` (document area), `Text.Primary|Secondary|Tertiary`,
  `Accent`, `Selection.Background`, `Control.*` (inputs), `Grid.*`,
  `Scroll.*`, `Semantic.*`, `Editor.*`, `Icon.Foreground`.
- Code surfaces use **AvalonEdit** with custom XSHD highlighting
  (`Assets/Highlighting/asm.xshd`, `pseudoc.xshd`, `csharp-dark.xshd`, `il.xshd`).
  `Views/Controls/CodeEditor` pulls its colours from the `Editor.*` keys.

Dark only for now: the syntax palettes are tuned for a dark background, so a
light theme needs a second set of XSHD files (see ROADMAP).

## 2. Shell layout

```
FluentWindow  (WindowBackdropType=None, ExtendsContentIntoTitleBar)
 ├─ ui:TitleBar            28px · "Spydate v0.1 (64-bit) — <file>"
 ├─ Menu                   File · Edit · View · Analyze · Help
 ├─ Toolbar                Open · Close │ Address box · Go │ Entry point · Re-analyze
 ├─ Grid
 │   ├─ Explorer tool window   header ("Explorer" + ✕) over the TreeView
 │   ├─ GridSplitter (4px)
 │   └─ Grid
 │       ├─ Document TabControl      square tabs, accent line on the selected one
 │       ├─ GridSplitter (4px)
 │       └─ Output tool window       header + tab strip *below* the content
 │                                    (Output = timestamped log, Xrefs, Warnings)
 └─ Status bar             file summary · function count, 2px progress bar when busy
```

Both tool windows can be hidden from the **View** menu or their own ✕; the
window remembers the last size of each panel (`MainWindow.xaml.cs`).

The **Xrefs** tab follows the active document: every document may carry an
`Address` (and an `AddressLength`), and code documents set it to their function
entry, so opening a function lists the sites that reference it (address,
enclosing function, kind, instruction). Double-clicking navigates to the
referring function. The Strings document moves its address as the selection
changes and spans the whole literal, so a reference into the middle of a string
still shows up.

## 3. MVVM

- `CommunityToolkit.Mvvm` (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`).
- DI container: `Microsoft.Extensions.DependencyInjection`, configured in `App.xaml.cs`.
- Services:
  - `IFileDialogService` — open file dialog.
  - `WorkspaceService` — loads `PeImage`, builds `BinaryAnalysis` / `ManagedAssembly`
    off‑thread, publishes `Current` (`OpenedBinary`, which also owns the
    `NativeDecompiler`).
  - `HighlightingService` — registers the embedded XSHD definitions.
- ViewModels:
  - `MainViewModel` — commands (Open, Close file, Go to address, Entry point,
    Re-analyze, Close tab(s), Clear output), `Explorer` (root nodes),
    `Documents`, `ActiveDocument`, `Output` (log), `Warnings`, `StatusText`,
    `AnalysisText`; `OpenTarget(NodeTarget)` routes tree clicks to documents
    (documents are keyed, so re‑opening activates the existing tab); runs
    `DiscoverAll` in the background after a file opens and refreshes the
    Functions node when done.
  - `ExplorerNodeViewModel` — `Title`, `Subtitle`, `Icon`, `Children` (lazy via
    `ChildrenFactory`), `Target` (a `NodeTarget` record), `IsExpanded`, `IsSelected`.
    Built by `ExplorerTreeBuilder`.
  - `DocumentViewModel` (abstract) — `Key`, `Title`, `Icon`, `CanClose`,
    `IsBusy`, `StatusMessage`, lazy `LoadAsync`. Concrete:
    `OverviewDocumentViewModel`, `HeadersDocumentViewModel`,
    `SectionsDocumentViewModel`, `ImportsDocumentViewModel`,
    `ExportsDocumentViewModel`, `FunctionsDocumentViewModel`,
    `ResourcesDocumentViewModel`, `StringsDocumentViewModel` (scan runs off-thread),
    `HexDocumentViewModel` (virtual `HexRowList`),
    `CodeDocumentViewModel` (disassembly / pseudo‑C, with toolbar `CodeAction`s
    such as "Decompile" ↔ "Disassembly"),
    `ManagedCodeDocumentViewModel` (C# / IL switch).
- Views: `UserControl`s under `Views/Documents/`, chosen with
  `DataTemplate DataType="{x:Type docs:XxxDocumentViewModel}"` in `App.xaml`.

## 4. Explorer tree (native)

```
notepad.exe  PE32+ · Amd64
 ├─ Overview
 ├─ Headers
 ├─ Entry point        0x1400019C0
 ├─ Sections           .text, .rdata, .data …   → click: hex at that section
 ├─ Imports            KERNEL32.dll → CreateFileW …
 ├─ Exports
 ├─ Functions          entry, exports, discovered sub_xxxx → click: disassembly
 ├─ Resources         type → name → language → click: hex at the data
 ├─ Strings           ascii + utf-16
 └─ Hex dump
```

Managed files additionally get **Assembly** (references) and **Namespaces →
Type → Member** nodes that open C#/IL documents.

## 5. Performance rules

- Hex view uses a virtual row list (`HexRowList`) so a 100 MB file costs no
  memory beyond the file itself.
- Disassembly documents render one function (or an explicit byte range).
- Decompilation and metadata loading run in `Task.Run`; the document shows
  "working…" and the status bar shows an indeterminate 2px bar.
- Trees, grids and the output list are virtualized (`VirtualizationMode=Recycling`).

## 6. Keyboard

- `Ctrl+O` open · `Ctrl+G` focus the address box · `Ctrl+W` / `Ctrl+F4` close tab
  · `F5` re‑analyze · `Alt+F4` exit.
- Files can also be dropped on the window or passed on the command line.

## 7. Known gaps (see ROADMAP)

- Dark theme only (light theme needs light syntax palettes).
- The Functions tree node lists up to 50 000 discovered functions eagerly;
  the Functions *document* has a filter box and should become the primary list.
- No navigation history yet; go‑to accepts VA, RVA, offset or symbol name.
- The Strings view hides hits in executable sections by default (they are mostly
  instruction bytes) and can be narrowed to strings some instruction points at.
- Toolbar/menu items expose `AutomationProperties.Name`, but buttons inside
  document toolbars do not yet appear in the UI Automation tree.
