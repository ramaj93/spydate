# PE format quick reference (as used by `Spydate.Core.PE`)

Condensed from the Microsoft PE/COFF specification and ECMA‑335 §II.25.
All fields little‑endian.

## Layout

```
offset 0      IMAGE_DOS_HEADER          e_magic 'MZ' (0x5A4D) … e_lfanew @ 0x3C
e_lfanew      "PE\0\0" signature (0x00004550)
+4            IMAGE_FILE_HEADER         (20 bytes)
+24           IMAGE_OPTIONAL_HEADER32/64 (Magic 0x10B / 0x20B) incl. data directories
+24+SizeOfOptionalHeader   IMAGE_SECTION_HEADER[NumberOfSections] (40 bytes each)
…             section raw data at PointerToRawData
…             overlay (anything after the last section's raw data)
```

## IMAGE_FILE_HEADER (20 bytes)

| Off | Size | Field |
|----:|-----:|-------|
| 0 | 2 | Machine (0x14C i386, 0x8664 AMD64, 0x1C0 ARM, 0xAA64 ARM64, 0x1C4 ARMNT) |
| 2 | 2 | NumberOfSections |
| 4 | 4 | TimeDateStamp |
| 8 | 4 | PointerToSymbolTable |
| 12 | 4 | NumberOfSymbols |
| 16 | 2 | SizeOfOptionalHeader |
| 18 | 2 | Characteristics (0x0002 EXECUTABLE_IMAGE, 0x2000 DLL, 0x0100 32BIT_MACHINE, 0x0020 LARGE_ADDRESS_AWARE) |

## IMAGE_OPTIONAL_HEADER

| PE32 off | PE32+ off | Size | Field |
|---:|---:|---:|---|
| 0 | 0 | 2 | Magic |
| 2 | 2 | 1+1 | MajorLinkerVersion, MinorLinkerVersion |
| 4 | 4 | 4 | SizeOfCode |
| 8 | 8 | 4 | SizeOfInitializedData |
| 12 | 12 | 4 | SizeOfUninitializedData |
| 16 | 16 | 4 | AddressOfEntryPoint (RVA) |
| 20 | 20 | 4 | BaseOfCode |
| 24 | — | 4 | BaseOfData (PE32 only) |
| 28 | 24 | 4/8 | ImageBase |
| 32 | 32 | 4 | SectionAlignment |
| 36 | 36 | 4 | FileAlignment |
| 40 | 40 | 2+2 | OS version |
| 44 | 44 | 2+2 | Image version |
| 48 | 48 | 2+2 | Subsystem version |
| 52 | 52 | 4 | Win32VersionValue |
| 56 | 56 | 4 | SizeOfImage |
| 60 | 60 | 4 | SizeOfHeaders |
| 64 | 64 | 4 | CheckSum |
| 68 | 68 | 2 | Subsystem (2 GUI, 3 CUI, 1 native, 9 CE, 10–13 EFI, 16 boot) |
| 70 | 70 | 2 | DllCharacteristics (0x0040 DYNAMIC_BASE, 0x0100 NX_COMPAT, 0x0020 HIGH_ENTROPY_VA, 0x4000 GUARD_CF) |
| 72 | 72 | 4/8 ×4 | SizeOfStackReserve/Commit, SizeOfHeapReserve/Commit |
| 88 | 104 | 4 | LoaderFlags |
| 92 | 108 | 4 | NumberOfRvaAndSizes |
| 96 | 112 | 8 × N | DataDirectory[N] { VirtualAddress, Size } |

Data directory indices: 0 Export, 1 Import, 2 Resource, 3 Exception, 4 Security
(file offset, not RVA!), 5 BaseReloc, 6 Debug, 7 Architecture, 8 GlobalPtr,
9 TLS, 10 LoadConfig, 11 BoundImport, 12 IAT, 13 DelayImport, 14 CLR (COM
descriptor), 15 Reserved.

## IMAGE_SECTION_HEADER (40 bytes)

Name[8] · VirtualSize · VirtualAddress · SizeOfRawData · PointerToRawData ·
PointerToRelocations · PointerToLinenumbers · NumberOfRelocations ·
NumberOfLinenumbers · Characteristics (0x20 CODE, 0x40 INITIALIZED_DATA,
0x80 UNINITIALIZED_DATA, 0x02000000 DISCARDABLE, 0x20000000 EXECUTE,
0x40000000 READ, 0x80000000 WRITE).

RVA→offset: find section with `VirtualAddress <= rva < VirtualAddress + max(VirtualSize, SizeOfRawData)`;
`offset = rva - VirtualAddress + PointerToRawData` (must be `< PointerToRawData + SizeOfRawData`).
RVAs below `SizeOfHeaders` map 1:1 to file offsets.

## Imports (dir 1)

`IMAGE_IMPORT_DESCRIPTOR[]` (20 bytes each, terminated by all‑zero):
OriginalFirstThunk (ILT RVA) · TimeDateStamp · ForwarderChain · Name (RVA of
DLL name) · FirstThunk (IAT RVA). Thunks are 4 (PE32) / 8 (PE32+) bytes;
high bit set ⇒ ordinal (low 16 bits); else RVA of `IMAGE_IMPORT_BY_NAME`
{ Hint u16, Name asciiz }. If ILT is 0, walk the IAT instead (bound imports).

## Delay imports (dir 13)

`IMAGE_DELAYLOAD_DESCRIPTOR[]` (32 bytes): Attributes · DllNameRVA ·
ModuleHandleRVA · ImportAddressTableRVA · ImportNameTableRVA ·
BoundImportAddressTableRVA · UnloadInformationTableRVA · TimeDateStamp.
Attributes bit 0 = RVAs (else VAs, legacy).

## Exports (dir 0)

`IMAGE_EXPORT_DIRECTORY` (40 bytes): Characteristics · TimeDateStamp ·
Major/MinorVersion · Name · Base · NumberOfFunctions · NumberOfNames ·
AddressOfFunctions · AddressOfNames · AddressOfNameOrdinals.
Function RVA inside the export directory range ⇒ forwarder string
(`NTDLL.RtlAllocateHeap`).

## Debug (dir 6)

`IMAGE_DEBUG_DIRECTORY[]` (28 bytes): Characteristics · TimeDateStamp ·
Major/Minor · Type (2 = CODEVIEW, 12 = REPRO, 20 = EX_DLLCHARACTERISTICS) ·
SizeOfData · AddressOfRawData · PointerToRawData. CodeView `RSDS`: signature
u32 · GUID · Age u32 · PDB path asciiz.

## CLR header (dir 14) — `IMAGE_COR20_HEADER`

cb · MajorRuntimeVersion · MinorRuntimeVersion · MetaData (dir) · Flags
(0x1 ILONLY, 0x2 32BITREQUIRED, 0x8 IL_LIBRARY, 0x10 STRONGNAMESIGNED,
0x10000 NATIVE_ENTRYPOINT, 0x20000 32BITPREFERRED) · EntryPointToken/RVA ·
Resources · StrongNameSignature · CodeManagerTable · VTableFixups ·
ExportAddressTableJumps · ManagedNativeHeader.

## Base relocations (dir 5)

A chain of `IMAGE_BASE_RELOCATION` blocks: PageRVA u32 · BlockSize u32,
then `(BlockSize - 8) / 2` u16 entries of `type:4 | offset:12`.
Fix-up RVA = PageRVA + offset. Type 0 (ABSOLUTE) is padding and is dropped;
10 (DIR64) is the x64 pointer patch, 3 (HIGHLOW) the x86 one.
A block whose size is < 8 is invalid — trusting it loops forever.

## TLS (dir 9) — `IMAGE_TLS_DIRECTORY`

StartAddressOfRawData · EndAddressOfRawData · AddressOfIndex ·
AddressOfCallBacks (all pointer-sized **VAs**, not RVAs) · SizeOfZeroFill u32 ·
Characteristics u32. `AddressOfCallBacks` points at a NULL-terminated array of
function pointers that run **before** the entry point — always seed analysis
with them.

## Load config (dir 10) — `IMAGE_LOAD_CONFIG_DIRECTORY`

Size-prefixed and grown over time, so every field past the version must be
range-checked against `Size`. Offsets differ between PE32 and PE32+:

| Field | PE32 | PE32+ |
|---|---|---|
| SecurityCookie | 0x3C | 0x58 |
| SEHandlerTable / Count | 0x40 / 0x44 | – (x86 only) |
| GuardCFCheckFunctionPointer | 0x48 | 0x70 |
| GuardCFDispatchFunctionPointer | 0x4C | 0x78 |
| GuardCFFunctionTable / Count | 0x50 / 0x54 | 0x80 / 0x88 |
| GuardFlags | 0x58 | 0x90 |

`GuardCFFunctionTable` is an array of 4-byte RVAs — every address the image
declares as a legal indirect-call target, i.e. a high-quality function seed.
The **top nibble of GuardFlags is not a flag**: it is the count of extra
metadata bytes per table entry, so the stride is `4 + (GuardFlags >> 28)`.

## Resources (dir 2)

Three nested `IMAGE_RESOURCE_DIRECTORY` levels — type → name → language —
each 16 bytes (Characteristics · TimeDateStamp · Major/Minor · NumberOfNamed ·
NumberOfId) followed by 8-byte entries: Name/Id u32 · OffsetToData u32.
High bit of Name = offset to a `u16 length + UTF-16` string; high bit of
OffsetToData = subdirectory, else an `IMAGE_RESOURCE_DATA_ENTRY`
(DataRVA · Size · CodePage · Reserved). All offsets are relative to the
directory start, and malformed files can point a child back at an ancestor —
track visited offsets.

## Rich header (DOS stub, undocumented)

Between the DOS header and `e_lfanew` the Microsoft linker writes XOR-encrypted
`(product id:16 | build:16, use count)` pairs: `DanS` marker, three padding
DWORDs, the pairs, then the literal `Rich` followed by the XOR key. Product ids
are undocumented and build numbers are **not** ordered across Visual Studio
releases (30729 = VS2008 SP1, 23026 = VS2015), so do not infer a version from
them — the optional header's linker version is the reliable signal.
