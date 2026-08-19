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
