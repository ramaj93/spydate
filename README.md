# Spydate

**Spydate** is a disassembler, decompiler and debugger for Windows, Linux, Java and Android
binaries, with a modern Fluent WPF interface, written in C# on .NET 10.

Drop in a file and Spydate will read it:

| You open | Spydate shows |
|---|---|
| **Windows PE** — `.exe`, `.dll`, `.sys` (x86, x64, ARM64) | Headers, sections, imports, exports, relocations, TLS, load config, resources, Rich header, Authenticode, PDB symbols; disassembly and pseudo‑C |
| **.NET assembly** | Metadata, IL and C# (powered by the [ILSpy](https://github.com/icsharpcode/ILSpy) engine), with cross‑references and strings read from every method body |
| **ELF** — a Linux, BSD or Android program, `.so` or object file (x86, x64, ARM64) | Segments, sections, dynamic symbols, symbol versions, PLT stubs, `.eh_frame`; disassembly and pseudo‑C |
| **JAR** / class files | The archive, a bytecode listing, and Java source decompiled in‑house, without a JVM |
| **APK** / bare `.dex` | The manifest and resource XML decoded, `resources.arsc`, every DEX class as a smali‑like listing and as Java, and the native libraries under `lib/` |

Behind that:

- **Native code** is disassembled (x86/x64 by [Iced](https://github.com/icedland/iced), ARM64 by an in‑house
  decoder checked word by word against a second one). Functions are discovered from the entry point, exports,
  unwind tables and the gaps between them, with switch tables, no‑return calls and cross‑references
  recovered, and control‑flow graphs drawn.
- **Pseudo‑C** comes from one in‑house pipeline for all three architectures. It lifts code to an IR, then
  propagates values, removes dead code, infers return values and call arguments, and structures the result
  into `if`, `while` and `switch`. Globals and strings are named, and import signatures are read from the
  DLLs on disk. The calling convention comes from the image: Microsoft x64, System V or AAPCS64.
- **Java** is decompiled on the same IR and structurer. It recovers erased generics, lambdas, records,
  enums, nested classes, Java 21 pattern switches and Kotlin coroutine loops. Its quality is measured
  by compiling the output again. Android release builds shrunk by R8 read cleanly too.
- **Debugging** covers native programs (x86 and x64; a 32‑bit build of the app debugs 32‑bit programs) and .NET
  (Core and Framework, launch or attach), with mixed mode across both. Each open file has its own debugger,
  so two programs can run at once.
- **Patching** works on x86 and on IL, typed as assembly or as bytes, each shown as the other. Patches are
  kept in the project and written out with File ▸ Save patched copy, or applied to a running .NET process.
- **Your work is saved as you go.** Renames (F2), comments (Ctrl+;) and notes go into a `.spydate` project
  file beside the binary. A project made for a different build is refused, the way a mismatched PDB is.
- **The window** is a tree plus tabbed documents, with several files open at once, one tab each. It has
  a hex viewer, syntax‑highlighted listings, a side‑by‑side view (F6) that keeps listing and pseudo‑code
  in step, a control‑flow graph (F7), navigation history, cross‑references and a string table.
- **It can be driven by an agent.** `spydate-mcp` exposes the engine as MCP tools, so Claude Code or
  any MCP client can run the reverse‑engineering loop: find what is still unnamed and heavily used,
  read it, name it, follow its callers. It writes into the same project file the window reads.

> Status: early development (0.1). See [docs/ROADMAP.md](docs/ROADMAP.md).

## Letting an agent help

Two ways, sharing one set of tools.

**In the window.** The Assistant panel, beside Output and Xrefs: choose a provider, paste a key, and
ask. It works on the binary you already have open, so a name it gives appears in your documents at
once. Each file tab has its own assistant, and a conversation is kept and can be picked up again.
Bring your own key — OpenAI, OpenRouter, DeepSeek or Anthropic — and it is encrypted to your
Windows account, kept in a different file from the settings so it cannot travel with them.

**From your own agent**, over MCP:

```bash
claude mcp add spydate -- dotnet run --project src/Spydate.Mcp
```

Then ask it to open a binary and start naming things. Both it and the window write the same
`.spydate` file, and saving merges, so you can work on one binary at the same time — rename something
from the agent and the open document retitles itself. Every annotation records who set it, so what an
agent did can be reviewed and undone as a set.

`--read-only` gives you its reasoning without its opinions landing in your project.

**One caution worth reading before you point this at something hostile.** The binary being analysed
is untrusted input, and its strings reach the agent's context — a sample can carry text shaped like
an instruction. The server confines what a persuaded agent can do: it can annotate the project file,
and nothing else. It cannot write bytes to the binary or run anything. The one exception is the
debugger tools. They are off unless you pass `--allow-debug`, and they only work in the window's
Assistant panel, which drives the debugger you are watching.
[docs/MCP.md](docs/MCP.md) has the full tool list and the rest of the caveats.

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (the app targets `net10.0-windows`)

## Build & run

```bash
dotnet build Spydate.slnx
```

```bash
dotnet run --project src/Spydate.App
```

```bash
dotnet test Spydate.slnx
```

To debug a 32‑bit program, build the 32‑bit app as well (`Spydate-x86.exe`):

```bash
dotnet build src/Spydate.App -p:PlatformTarget=x86
```

## Solution structure

| Project | Purpose |
|---------|---------|
| `src/Spydate.Core` | Format parsers — PE, ELF, zip/JAR, class files, APK, DEX, binary XML, PDB — plus address mapping, strings, symbols and the project file. Dependency‑free. |
| `src/Spydate.Disassembly` | Instruction decoders (x86/x64 via Iced, ARM64 in‑house), function discovery, switch tables, cross‑references, basic blocks and CFG. |
| `src/Spydate.Decompiler` | Native IR, the x86 and ARM64 lifters, passes, structurer and pseudo‑C emitter; the Java and Dalvik decompiler; the ILSpy wrapper for C#/IL. |
| `src/Spydate.Debugger` | The native Win32 debugger and the .NET debugger (ICorDebug through dbgshim, CLRMD for inspection). |
| `src/Spydate.Agent` | The Assistant panel's engine: providers, key storage, and the tool loop over the MCP tools. |
| `src/Spydate.Mcp` | `spydate-mcp`, the engine as MCP tools over stdio. |
| `src/Spydate.App` | WPF UI (Wpf.Ui Fluent controls, AvalonEdit, CommunityToolkit.Mvvm). |
| `tests/Spydate.Tests` | xunit tests, including real‑binary smoke tests. |
| `tests/Spydate.Tests.ManagedDebuggee` | A small .NET program the debugger tests run under the debugger. |

## Documentation

- [AGENTS.md](AGENTS.md) — how to work in this repo (conventions, layering, tasks)
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- [docs/DECOMPILER-DESIGN.md](docs/DECOMPILER-DESIGN.md)
- [docs/UI-DESIGN.md](docs/UI-DESIGN.md)
- [docs/PE-FORMAT.md](docs/PE-FORMAT.md)
- [docs/MCP.md](docs/MCP.md) — the agent tools and their safety model
- [docs/MIXED-MODE.md](docs/MIXED-MODE.md) — native and managed debugging in one session
- [docs/MULTI-FILE.md](docs/MULTI-FILE.md) — several binaries open at once
- [docs/STEP-INTO-IMPORTS.md](docs/STEP-INTO-IMPORTS.md) — following calls into the DLLs a program imports
- [docs/ROADMAP.md](docs/ROADMAP.md)
- [docs/DECISIONS.md](docs/DECISIONS.md)

## License

MIT (see `LICENSE`). Third‑party components include Iced (MIT), ICSharpCode.Decompiler (MIT),
WPF‑UI (MIT), AvalonEdit (MIT), CommunityToolkit.Mvvm (MIT), Microsoft.Diagnostics.Runtime (MIT)
and Markdig (BSD‑2‑Clause); [Directory.Packages.props](Directory.Packages.props) lists every package.
