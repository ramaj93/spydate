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
- The graph is drawn directly into a `DrawingContext` rather than as a control per
  block: a few hundred blocks of thirty instructions is tens of thousands of runs of
  text. Only what the scroller says is on screen is built, and below 45% zoom the
  text is dropped entirely — it could not be read at that size anyway. Laying out a
  111-block function takes about 40 ms, and it happens off the UI thread.

## 6. Keyboard

- `Ctrl+O` open · `Ctrl+G` focus the address box · `Ctrl+W` / `Ctrl+F4` close tab
  · `F5` re‑analyze · `Alt+F4` exit.
- `Alt+←` / `Alt+→` back and forward through everywhere you have been, as a browser
  does: going somewhere new from the middle drops what was ahead, and a tab that has
  since been closed is reopened rather than skipped.
- `F6` shows the current function side by side: disassembly left, pseudo-C right,
  each following the other. The panes agree through `LineAddressMap`, built from the
  addresses both texts already state — a listing line begins with one, a pseudo-C
  line ends with one. An instruction with no line of its own (the passes fold most
  of them away) resolves to the last statement at or before it, which is the one it
  ended up inside.
- `F7` draws the current function as a control-flow graph: a box per basic block,
  entry at the top, control downwards. Edge colour says how control got there —
  grey fall-through, green branch taken, blue jump, orange the edge that closes a
  loop, purple a switch arm — because that is the distinction a listing makes you
  work out. Clicking a block selects it, which is what the Xrefs panel and the
  naming commands follow; double-clicking opens the listing, since the graph is for
  the shape of a function and the listing is for reading it. `Ctrl`+scroll zooms.
- `F2` rename · `Ctrl+;` comment · `Ctrl+S` save the project. Rename and comment
  act on what the caret is on: a stack slot (`arg_0`, `local_18`) when it is one,
  then the name under the caret (so a callee can be renamed from the code that calls
  it), then the address of the line, then whatever the document is about. That
  ordering lives in `CaretTargets` rather than in the window, because it is the part
  worth testing. Right-clicking moves the caret first, so the context menu acts where
  you clicked. Single letters are deliberately *not* bound (IDA's `n` and `;`): a
  window-level key binding would take them from the address box.
- Files can also be dropped on the window or passed on the command line.

## 7. Diagnosing the window

Set `SPYDATE_TRACE_BINDINGS` to a file path and WPF's binding failures are written
there, with a header line so an empty log means "nothing failed" rather than "not
enabled". A binding that silently does nothing — a command that never arrives
because a context menu has no `Window` above it to bind through — is invisible
otherwise, and is exactly the kind of bug the window cannot show you.

## 7b. Known gaps (see ROADMAP)

- Dark theme only (light theme needs light syntax palettes).
- The Functions tree node lists up to 50 000 discovered functions eagerly;
  the Functions *document* has a filter box and should become the primary list.
- No navigation history yet; go‑to accepts VA, RVA, offset or symbol name.
- The Strings view hides hits in executable sections by default (they are mostly
  instruction bytes) and can be narrowed to strings some instruction points at.
- Toolbar/menu items expose `AutomationProperties.Name`, but buttons inside
  document toolbars do not yet appear in the UI Automation tree. Nothing inside a
  document does, in fact — which is why anything worth verifying is kept out of the
  window (see `Spydate.Core.Graph`, `Spydate.Core.Text`).
- The graph does not lay out a function past 600 blocks; it says so and points at
  the listing. Notepad's largest function is 1620 blocks, which would be a drawing
  70,000 by 210,000 pixels.
