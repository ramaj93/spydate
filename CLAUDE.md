# CLAUDE.md

All agent guidance for this repository lives in **[AGENTS.md](AGENTS.md)**.
Read it first, then the relevant file under `docs/`.

Quick reference:

- Build: `dotnet build Spydate.slnx`
- Test: `dotnet test Spydate.slnx`
- Run UI: `dotnet run --project src/Spydate.App`
- Layering: `App → Decompiler → Disassembly → Core` (never the other way).
- **`Spydate.App` has no tests and never will.** If a change can affect the window,
  drive the window before saying it works — console probe or UI Automation, see
  AGENTS.md §5.1. A green test run says nothing about the panel.
- Package versions: `Directory.Packages.props` only.
- Nullable warnings are errors. Untrusted PE input must never crash the parser.
