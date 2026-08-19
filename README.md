# Spydate

**Spydate** is a Windows PE disassembler and decompiler with a modern Fluent
WPF interface, written in C# on .NET 10.

Drop in an `.exe`, `.dll` or `.sys` and Spydate will:

- parse the PE headers, sections, data directories, imports, exports, CLR
  header and debug (PDB) info;
- disassemble native x86 / x64 code (powered by [Iced](https://github.com/icedland/iced)),
  discover functions from the entry point and exports, and build control‑flow graphs;
- lift native code to an intermediate representation and emit pseudo‑C;
- for .NET assemblies, show IL and decompile to C# (powered by the
  [ILSpy](https://github.com/icsharpcode/ILSpy) engine);
- browse everything in a tree + tabbed‑document UI with a hex viewer and
  syntax‑highlighted code views.

> Status: early development (0.1). See [docs/ROADMAP.md](docs/ROADMAP.md).

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

## Solution structure

| Project | Purpose |
|---------|---------|
| `src/Spydate.Core` | PE parsing, RVA/VA/offset mapping, symbol table. Dependency‑free. |
| `src/Spydate.Disassembly` | x86/x64 decoding via Iced, function discovery, basic blocks & CFG. |
| `src/Spydate.Decompiler` | Native IR + lifter + pseudo‑C emitter; managed C#/IL decompiler wrapper. |
| `src/Spydate.App` | WPF UI (Wpf.Ui Fluent controls, AvalonEdit, CommunityToolkit.Mvvm). |
| `tests/Spydate.Tests` | xunit tests. |

## Documentation

- [AGENTS.md](AGENTS.md) — how to work in this repo (conventions, layering, tasks)
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- [docs/DECOMPILER-DESIGN.md](docs/DECOMPILER-DESIGN.md)
- [docs/UI-DESIGN.md](docs/UI-DESIGN.md)
- [docs/PE-FORMAT.md](docs/PE-FORMAT.md)
- [docs/ROADMAP.md](docs/ROADMAP.md)
- [docs/DECISIONS.md](docs/DECISIONS.md)

## License

MIT (see `LICENSE`). Third‑party components: Iced (MIT), ICSharpCode.Decompiler
(MIT), WPF‑UI (MIT), AvalonEdit (MIT), CommunityToolkit.Mvvm (MIT).
