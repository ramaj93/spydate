# Architecture decision records

Short ADRs. Newest at the bottom. Add one whenever a non‑obvious choice is made.

## ADR‑001: .NET 10 + WPF (not WinUI 3 / Avalonia)
WPF is mature, tooling is stable, AvalonEdit and Wpf.Ui give a modern look
without the WinUI packaging friction. Windows‑only is acceptable: PE analysis
is a Windows‑centric task. Engine projects stay `net10.0` so a future
cross‑platform CLI or Avalonia UI can reuse them.

## ADR‑002: Iced for x86/x64 decoding
Iced (MIT) is the fastest, most complete managed x86 decoder, has formatters
(Intel/MASM/NASM/GAS) with symbol resolution, and exposes rich instruction
info (flow control, memory operands, RIP‑relative targets). Writing our own
decoder would be months of work with no benefit.

## ADR‑003: ICSharpCode.Decompiler for managed code
ILSpy's engine is the de‑facto standard for C# decompilation and is MIT. We
wrap it rather than reimplement. Pinned to 9.1.x (stable, .NET 8/10 compatible).

## ADR‑004: Own PE parser instead of `System.Reflection.PortableExecutable`
`PEReader` covers headers/sections but not imports/exports/delay‑imports/debug
in a convenient way and is tuned for managed images. A small custom parser
gives full control over bounds checking, warnings for malformed files, and
overlay/anomaly reporting that RE tools need. `PEReader` is still used inside
the managed decompiler path (via ILSpy).

## ADR‑005: Central package management
`Directory.Packages.props` pins every version once. Adding a package = one line
there + `<PackageReference Include="X" />` in the csproj.

## ADR‑006: Wpf.Ui + AvalonEdit + CommunityToolkit.Mvvm
Wpf.Ui provides Fluent controls, Mica and theming with minimal XAML; AvalonEdit
gives a virtualised code editor with XSHD highlighting; the MVVM toolkit removes
boilerplate via source generators. All MIT.

## ADR‑007: Native decompiler is an in‑house IR pipeline
No mature managed library exists for native decompilation. We build a small,
explicit IR (see DECOMPILER‑DESIGN.md) with a goto‑based emitter first, then
add passes and structuring incrementally. Correctness over prettiness: the
lifter must never silently drop an instruction (unsupported → `__asm` passthrough).

## ADR‑008: Immutable analysis results
`PeImage`, `Function`, `IrFunction` are immutable after construction, so
documents can share them across threads without locks. Mutable user
annotations (renames, comments) will live in a separate project store (Phase 3).

## Rich header product ids are reported, not named

The Rich header records `(product id, build number, object count)` triples for
every tool that contributed to the binary. Product ids are undocumented; the
mapping to tool names circulates as a community-maintained table.

Spydate reports the raw ids, builds and counts, and does not name the tools.
Two reasons:

- The table cannot be verified from anything on the machine, and a wrong
  toolchain label is worse than none — it is exactly the kind of detail an
  analyst would quote in a report.
- An earlier attempt inferred the Visual Studio version from the build number
  instead. That is provably wrong: build numbers are not ordered across
  releases (30729 is VS2008 SP1, 23026 is VS2015).

What *is* verifiable is the header's own checksum, computed by the linker over
the DOS stub and the entries. Spydate recomputes it: a match is evidence the
header is genuine, and a mismatch means it was edited or forged, which is a
signal worth surfacing. If a verified prodid table is ever added to the repo as
data, naming can be layered on top without changing this decision.

## No hand-typed Win32 signature database

Typing call arguments from the API being called is the obvious next step for
readability: `SendMessageW(hwnd, WM_SETTEXT, 0, lParam)` beats four bare
registers. It needs a table mapping an import name to its parameter list, and
the usual way to get one is to type it in.

Spydate does not, for the same reason it does not name Rich header product ids:
nothing on the machine can check the table, and a wrong signature is not a
cosmetic error — it drops arguments the code really passes, or invents ones it
does not, in output an analyst is reading to decide what a binary does.

Two things would make it viable, and either is worth doing before the table is:

- **Read the arity from the DLL.** An x86 `__stdcall` export cleans its own
  stack, so the `ret N` at the end of `user32!SendMessageW` states the argument
  count exactly. Resolving imports against the DLLs on disk gives verified arity
  for 32-bit binaries, with no table at all. It costs the ability to analyse a
  binary whose DLLs are not present, so it belongs behind an option.
- **Ship a checked data file.** A signature table generated from the SDK
  headers, kept as data rather than code, with its provenance recorded — the
  same shape as an IDA type library.

Until then argument *values* are shown, which is what the recovery can prove,
and the parameter names are left out.

## The control-flow graph is laid out in Core, not in the window

The graph view is drawn in a document tab, and nothing inside a document tab is
visible to UI Automation — the only way anything in this window can be inspected
from outside it. Screenshots do not work either: the desktop renders as a flat
colour and `PrintWindow` returns blank.

So the split is drawn on purpose. `Spydate.Core.Graph` takes node sizes and edges
and returns rectangles and polylines; it knows nothing about functions, blocks,
instructions or fonts. The WPF control does nothing but put ink on the geometry it
is handed.

That puts every question with a right answer on the testable side:

- no two boxes overlap, so no block is hidden or unclickable;
- an edge begins on the block it leaves and ends on the block it reaches, so an
  arrowhead always points at a box;
- no edge passes through a box it is not attached to;
- forward edges run downwards;
- the same graph is always drawn the same way, so a block does not move out from
  under the pointer between redraws.

These are asserted over every function of both notepads, not just over made-up
examples. What remains unverifiable is whether the ink appears — and for that the
same geometry is rendered to SVG, which can be opened and looked at.

## A loop edge is drawn round the side, not through the layers

Ranking a layered drawing needs an acyclic graph, so the back edges a depth-first
walk finds have to come out before ranking either way. The usual next step is to put
them back in reversed, let them take part in ordering, and un-reverse the route at
the end.

Spydate does not. A reversed back edge is laid out from the loop header *down* to the
block that jumps back, so un-reversing it produces a line that leaves the **top** of
the block it comes from. That is geometrically consistent and reads wrongly: control
leaves the *end* of a block, and a loop is the one place a reader is specifically
looking for where it goes back to.

Instead each loop edge gets a channel down the left of the drawing and is routed out
of the bottom of its source, along the band below that layer, up the channel, and
into the top of the header. The cost is a wider drawing when a function has many
loops, and lines that are longer than they would otherwise be. What it buys is that
the picture says what the code does.

## Import signatures are read from the DLLs, not from a table

The first of the two options above is now what Spydate does, and the table is
still not written.

`ImportSignatures` opens the DLL that exports an imported function and reads what
the export itself says:

- On x86 a `__stdcall` callee removes its own arguments, so `ret N` states the
  stack argument count exactly. `user32!SetWindowPos` ends in `ret 1Ch`, and that
  is seven arguments — read, not looked up. `ret` with no immediate means the
  caller cleans up, which settles the *cleanup* while leaving the count unknown,
  because a cdecl function with four arguments returns exactly like one with none.
- On x64 nothing states a count, but an export that reads `xmm2` before writing
  it was handed a float in the third slot. That is the one thing a call site
  cannot show, and it is what "float arguments" needed.

Three things had to be true for this to be worth more than the table it replaces:

- **API sets.** Since Windows 7 most system imports name
  `api-ms-win-core-synch-l1-1-0.dll`, which is not a file. `ApiSetSchema` reads
  the redirect out of `apisetschema.dll`. Without it four fifths of a modern
  binary's imports resolve to nothing.
- **Export thunks.** `kernel32!CloseHandle` is one instruction:
  `jmp [api-ms-win-core-handle-l1-1-0!CloseHandle]`. Read literally it is a
  function that takes nothing. Following the jump is most of the Win32 API.
- **Direction.** An x86 count is exact, so it may cap what a call site collected.
  An x64 count is a lower bound — only the entry block is read — so it may only
  add or retype an argument, never remove one the call site really passes.

What is still not claimed: parameter names, and any type beyond
integer-versus-float. Neither is in the binary.

The cost is the one the earlier entry predicted: a binary whose DLLs are not on
this machine gets nothing extra. It fails soft — every lookup returns "unknown"
and the output is what it was before — and `BinaryAnalysis.ResolveImportSignatures`
turns the whole thing off. `ImportSignatures.Modules` records what was opened and
why anything was not, so a wrong answer can be traced to a file.

One caveat worth stating plainly: the DLL read is the one installed *here*, not
the one the binary was built against. Argument counts are part of an API's
contract and effectively never change, but a binary analysed on a machine whose
Windows differs from its target is being told about this machine's DLLs.
