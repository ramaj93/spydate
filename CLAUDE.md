# CLAUDE.md

All agent guidance for this repository lives in **[AGENTS.md](AGENTS.md)**.
Read it first, then the relevant file under `docs/`.

Quick reference:

- Build: `dotnet build Spydate.slnx`
- Test: `dotnet test Spydate.slnx`
- Run UI: `dotnet run --project src/Spydate.App`
- Layering: `App → Decompiler → Disassembly → Core` (never the other way).
- Package versions: `Directory.Packages.props` only.
- Nullable warnings are errors. Untrusted PE input must never crash the parser.
