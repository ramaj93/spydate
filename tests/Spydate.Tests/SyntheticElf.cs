using System.Buffers.Binary;
using System.Text;

namespace Spydate.Tests;

/// <summary>Where the builder put things, handed to the code callback so the code can call and load them.</summary>
internal sealed record ElfLayout(ulong TextVa, ulong RodataVa, IReadOnlyList<ulong> StubVas, IReadOnlyList<ulong> SlotVas);

/// <summary>
/// Builds small, complete ELF files in memory: the fixtures the ELF parser and the analysis are tested on, since
/// a Windows machine has no system ELF to lean on the way the PE tests lean on System32.
///
/// The file is one loadable segment mapped 1:1 (address = base + file offset), so every section's address is
/// known before its bytes are written. Imports get everything a real dynamic linker writes for them — a PLT
/// stub, a GOT slot, a JUMP_SLOT relocation, a versioned <c>.dynsym</c> entry naming the library — because each
/// of those is a path through the parser worth covering. Functions get a <c>.symtab</c> entry (unless stripped)
/// and an <c>.eh_frame</c> FDE.
/// </summary>
internal sealed class SyntheticElf
{
    private const ulong ShfWrite = 1;
    private const ulong ShfAlloc = 2;
    private const ulong ShfExec = 4;

    public bool Is64 { get; init; } = true;

    public ushort Machine { get; init; } = 62;

    /// <summary>2 = executable, 3 = shared object (a PIE when it also has an interpreter).</summary>
    public ushort Type { get; init; } = 2;

    public ulong Base { get; init; } = 0x400000;

    public string? Interpreter { get; init; } = "/lib64/ld-linux-x86-64.so.2";

    public string Library { get; init; } = "libc.so.6";

    public string LibraryVersion { get; init; } = "GLIBC_2.2.5";

    /// <summary>Whether the imports carry symbol versions, which is how an ELF says which library provides them.</summary>
    public bool Versioned { get; init; } = true;

    public IReadOnlyList<string> Imports { get; init; } = ["puts"];

    public byte[] Rodata { get; init; } = Encoding.ASCII.GetBytes("hello from elf\0");

    /// <summary>Functions in <c>.text</c>: name, offset into it, length. The first is the entry point.</summary>
    public IReadOnlyList<(string Name, int Offset, int Length)> Functions { get; init; } = [];

    /// <summary>The bytes of <c>.text</c>, written once the layout is known.</summary>
    public Func<ElfLayout, byte[]> Code { get; init; } = _ => [0xC3];

    /// <summary>No <c>.symtab</c>: the file as <c>strip</c> leaves it.</summary>
    public bool Stripped { get; init; }

    public byte[]? BuildId { get; init; } = [0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3, 4];

    private int Word => Is64 ? 8 : 4;

    public byte[] Build()
    {
        var file = new List<byte>(new byte[0x1000]);   // headers live in the first page
        var sections = new List<Section>();

        int Place(int align)
        {
            while (file.Count % align != 0)
            {
                file.Add(0);
            }

            return file.Count;
        }

        Section Add(string name, uint type, ulong flags, byte[] bytes, int align = 8, ulong entSize = 0)
        {
            int at = Place(align);
            file.AddRange(bytes);
            var s = new Section(name, type, flags, at, bytes.Length, align, entSize);
            sections.Add(s);
            return s;
        }

        ulong Va(int offset) => Base + (ulong)offset;

        // Fixed-size sections first, so .text can be written knowing where to call and load.
        var interp = Interpreter is null ? null : Add(".interp", 1, ShfAlloc, Encoding.ASCII.GetBytes(Interpreter + "\0"), 1);
        int pltEntries = Imports.Count + 1;
        var plt = Add(".plt", 1, ShfAlloc | ShfExec, new byte[16 * pltEntries], 16, 16);
        var rodata = Add(".rodata", 1, ShfAlloc, Rodata, 8);
        int gotEntries = 3 + Imports.Count;

        // The GOT is placed after .text, but its address is needed in .text and .plt; reserve it now at a
        // known offset far enough out that .text (at most a page) fits before it.
        int textAt = Place(16);
        int gotAt = textAt + 0x1000;
        ulong gotVa = Va(gotAt);
        var slotVas = Enumerable.Range(0, Imports.Count).Select(i => gotVa + (ulong)(Word * (3 + i))).ToList();
        var stubVas = Enumerable.Range(0, Imports.Count).Select(i => Va(plt.Offset + 16 * (i + 1))).ToList();

        byte[] code = Code(new ElfLayout(Va(textAt), Va(rodata.Offset), stubVas, slotVas));
        if (code.Length > 0x1000)
        {
            throw new ArgumentException("the synthetic .text holds at most a page of code");
        }

        var text = Add(".text", 1, ShfAlloc | ShfExec, code, 16);
        while (file.Count < gotAt)
        {
            file.Add(0xCC);
        }

        var got = Add(".got.plt", 1, ShfAlloc | ShfWrite, new byte[Word * gotEntries], Word, (ulong)Word);
        WritePlt(file, plt, gotVa, slotVas);

        var ehFrame = Add(".eh_frame", 1, ShfAlloc, EhFrame(Va(Place(8)), Va(textAt)), 8);

        // Dynamic linking: strings, symbols, versions, relocations, the dynamic section.
        var dynstr = new StringTable();
        uint libraryName = dynstr.Add(Library);
        uint versionName = dynstr.Add(LibraryVersion);
        var importNames = Imports.Select(dynstr.Add).ToList();

        var dynsymBytes = new List<byte>(new byte[SymSize]);
        foreach (uint name in importNames)
        {
            dynsymBytes.AddRange(Sym(name, 0, 0, 0x12, 0));   // GLOBAL FUNC, undefined
        }

        var dynsym = Add(".dynsym", 11, ShfAlloc, dynsymBytes.ToArray(), Word, (ulong)SymSize);
        var dynstrSection = Add(".dynstr", 3, ShfAlloc, dynstr.Bytes, 1);

        Section? versym = null;
        Section? verneed = null;
        if (Versioned)
        {
            var v = new List<byte>(new byte[2]);
            foreach (var _ in Imports)
            {
                v.AddRange(U16(2));
            }

            versym = Add(".gnu.version", 0x6FFFFFFF, ShfAlloc, v.ToArray(), 2, 2);
            var need = new List<byte>();
            need.AddRange(U16(1));
            need.AddRange(U16(1));
            need.AddRange(U32(libraryName));
            need.AddRange(U32(16));
            need.AddRange(U32(0));
            need.AddRange(U32(0x09691A75));
            need.AddRange(U16(0));
            need.AddRange(U16(2));
            need.AddRange(U32(versionName));
            need.AddRange(U32(0));
            verneed = Add(".gnu.version_r", 0x6FFFFFFE, ShfAlloc, need.ToArray(), 4);
        }

        var rela = new List<byte>();
        for (int i = 0; i < Imports.Count; i++)
        {
            rela.AddRange(Word == 8 ? U64(slotVas[i]) : U32((uint)slotVas[i]));
            rela.AddRange(Word == 8 ? U64(((ulong)(i + 1) << 32) | 7) : U32((uint)(((i + 1) << 8) | 7)));
            if (Is64)
            {
                rela.AddRange(U64(0));
            }
        }

        var relPlt = Add(Is64 ? ".rela.plt" : ".rel.plt", Is64 ? 4u : 9u, ShfAlloc, rela.ToArray(), Word, (ulong)(Is64 ? 24 : 8));

        var dyn = new List<(long, ulong)> { (1, libraryName) };
        dyn.Add((5, Va(dynstrSection.Offset)));
        dyn.Add((6, Va(dynsym.Offset)));
        dyn.Add((10, (ulong)dynstr.Bytes.Length));
        dyn.Add((11, (ulong)SymSize));
        dyn.Add((3, gotVa));
        dyn.Add((2, (ulong)rela.Count));
        dyn.Add((20, Is64 ? 7UL : 17UL));
        dyn.Add((23, Va(relPlt.Offset)));
        if (versym is not null && verneed is not null)
        {
            dyn.Add((0x6FFFFFF0, Va(versym.Offset)));
            dyn.Add((0x6FFFFFFE, Va(verneed.Offset)));
            dyn.Add((0x6FFFFFFF, 1));
        }

        dyn.Add((0, 0));
        var dynBytes = new List<byte>();
        foreach (var (tag, value) in dyn)
        {
            dynBytes.AddRange(Word == 8 ? U64((ulong)tag) : U32((uint)tag));
            dynBytes.AddRange(Word == 8 ? U64(value) : U32((uint)value));
        }

        var dynamic = Add(".dynamic", 6, ShfAlloc | ShfWrite, dynBytes.ToArray(), Word, (ulong)(2 * Word));

        Section? note = null;
        if (BuildId is { } id)
        {
            var n = new List<byte>();
            n.AddRange(U32(4));
            n.AddRange(U32((uint)id.Length));
            n.AddRange(U32(3));
            n.AddRange(Encoding.ASCII.GetBytes("GNU\0"));
            n.AddRange(id);
            while (n.Count % 4 != 0)
            {
                n.Add(0);
            }

            note = Add(".note.gnu.build-id", 7, ShfAlloc, n.ToArray(), 4);
        }

        int loadEnd = file.Count;

        // Not loaded: the full symbol table and the section names.
        var names = new StringTable();
        Section? symtab = null;
        Section? strtab = null;
        if (!Stripped)
        {
            var strings = new StringTable();
            var syms = new List<byte>(new byte[SymSize]);
            foreach (var (name, offset, length) in Functions)
            {
                syms.AddRange(Sym(strings.Add(name), Va(textAt + offset), (ulong)length, 0x12, (ushort)(sections.IndexOf(text) + 1)));
            }

            symtab = Add(".symtab", 2, 0, syms.ToArray(), Word, (ulong)SymSize);
            strtab = Add(".strtab", 3, 0, strings.Bytes, 1);
        }

        foreach (var s in sections)
        {
            s.NameOffset = names.Add(s.Name);
        }

        uint shstrName = names.Add(".shstrtab");
        var shstrtab = Add(".shstrtab", 3, 0, names.Bytes, 1);
        shstrtab.NameOffset = shstrName;

        // Links, now every index is known (section header index = list index + 1; 0 is the null section).
        int Index(Section? s) => s is null ? 0 : sections.IndexOf(s) + 1;
        dynsym.Link = Index(dynstrSection);
        dynsym.Info = 1;
        dynamic.Link = Index(dynstrSection);
        relPlt.Link = Index(dynsym);
        relPlt.Info = Index(got);
        if (versym is not null)
        {
            versym.Link = Index(dynsym);
        }

        if (verneed is not null)
        {
            verneed.Link = Index(dynstrSection);
            verneed.Info = 1;
        }

        if (symtab is not null)
        {
            symtab.Link = Index(strtab);
            symtab.Info = 1;
        }

        int shoff = Place(8);
        int shentsize = Is64 ? 64 : 40;
        file.AddRange(new byte[shentsize]);   // SHN_UNDEF
        foreach (var s in sections)
        {
            bool alloc = (s.Flags & ShfAlloc) != 0;
            file.AddRange(SectionHeader(s, alloc ? Va(s.Offset) : 0));
        }

        var bytes = file.ToArray();

        // Program headers: one RWX load covering every loaded byte, then the interpreter, dynamic, stack, note.
        var phdrs = new List<byte[]>
        {
            Phdr(1, 7, 0, Base, (ulong)loadEnd, (ulong)loadEnd, 0x1000),
        };
        if (interp is not null)
        {
            phdrs.Add(Phdr(3, 4, (ulong)interp.Offset, Va(interp.Offset), (ulong)interp.Size, (ulong)interp.Size, 1));
        }

        phdrs.Add(Phdr(2, 6, (ulong)dynamic.Offset, Va(dynamic.Offset), (ulong)dynamic.Size, (ulong)dynamic.Size, (ulong)Word));
        phdrs.Add(Phdr(0x6474E551, 6, 0, 0, 0, 0, 16));   // GNU_STACK, not executable
        if (note is not null)
        {
            phdrs.Add(Phdr(4, 4, (ulong)note.Offset, Va(note.Offset), (ulong)note.Size, (ulong)note.Size, 4));
        }

        int ehsize = Is64 ? 64 : 52;
        int phentsize = Is64 ? 56 : 32;
        for (int i = 0; i < phdrs.Count; i++)
        {
            phdrs[i].CopyTo(bytes, ehsize + i * phentsize);
        }

        ulong entry = Functions.Count > 0 ? Va(textAt + Functions[0].Offset) : 0;
        WriteHeader(bytes, entry, (ulong)ehsize, (ulong)shoff, phdrs.Count, sections.Count + 1, Index(shstrtab));
        return bytes;
    }

    private int SymSize => Is64 ? 24 : 16;

    private void WritePlt(List<byte> file, Section plt, ulong gotVa, List<ulong> slotVas)
    {
        ulong pltVa = Base + (ulong)plt.Offset;
        var bytes = new byte[plt.Size];

        // PLT0: push [GOT+8]; jmp [GOT+16] — its jump goes through a slot no import owns.
        if (Is64)
        {
            bytes[0] = 0xFF;
            bytes[1] = 0x35;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), (int)((long)(gotVa + 8) - (long)(pltVa + 6)));
            bytes[6] = 0xFF;
            bytes[7] = 0x25;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), (int)((long)(gotVa + 16) - (long)(pltVa + 12)));
        }
        else
        {
            bytes[0] = 0xFF;
            bytes[1] = 0x35;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), (uint)(gotVa + 4));
            bytes[6] = 0xFF;
            bytes[7] = 0x25;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)(gotVa + 8));
        }

        for (int i = 0; i < slotVas.Count; i++)
        {
            int at = 16 * (i + 1);
            ulong stubVa = pltVa + (ulong)at;
            bytes[at] = 0xFF;
            bytes[at + 1] = 0x25;
            if (Is64)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 2), (int)((long)slotVas[i] - (long)(stubVa + 6)));
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 2), (uint)slotVas[i]);
            }

            bytes[at + 6] = 0x68;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 7), i);
            bytes[at + 11] = 0xE9;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 12), (int)((long)pltVa - (long)(stubVa + 16)));
        }

        for (int i = 0; i < bytes.Length; i++)
        {
            file[plt.Offset + i] = bytes[i];
        }
    }

    /// <summary>One CIE ("zR", pc-relative sdata4 addresses) and an FDE per function, then the terminator.</summary>
    private byte[] EhFrame(ulong frameVa, ulong textVa)
    {
        var b = new List<byte>();
        b.AddRange(U32(16));
        b.AddRange(U32(0));
        b.Add(1);
        b.AddRange("zR\0"u8.ToArray());
        b.Add(1);
        b.Add(0x78);
        b.Add(16);
        b.Add(1);
        b.Add(0x1B);
        b.AddRange(new byte[3]);

        foreach (var (_, offset, length) in Functions)
        {
            int start = b.Count;
            b.AddRange(U32(16));
            b.AddRange(U32((uint)(start + 4)));
            ulong field = frameVa + (ulong)b.Count;
            b.AddRange(U32((uint)(int)((long)(textVa + (ulong)offset) - (long)field)));
            b.AddRange(U32((uint)length));
            b.Add(0);
            b.AddRange(new byte[3]);
        }

        b.AddRange(U32(0));
        return b.ToArray();
    }

    private void WriteHeader(byte[] bytes, ulong entry, ulong phoff, ulong shoff, int phnum, int shnum, int shstrndx)
    {
        bytes[0] = 0x7F;
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'L';
        bytes[3] = (byte)'F';
        bytes[4] = (byte)(Is64 ? 2 : 1);
        bytes[5] = 1;
        bytes[6] = 1;
        var h = new List<byte>();
        h.AddRange(U16(Type));
        h.AddRange(U16(Machine));
        h.AddRange(U32(1));
        h.AddRange(Word == 8 ? U64(entry) : U32((uint)entry));
        h.AddRange(Word == 8 ? U64(phoff) : U32((uint)phoff));
        h.AddRange(Word == 8 ? U64(shoff) : U32((uint)shoff));
        h.AddRange(U32(0));
        h.AddRange(U16((ushort)(Is64 ? 64 : 52)));
        h.AddRange(U16((ushort)(Is64 ? 56 : 32)));
        h.AddRange(U16((ushort)phnum));
        h.AddRange(U16((ushort)(Is64 ? 64 : 40)));
        h.AddRange(U16((ushort)shnum));
        h.AddRange(U16((ushort)shstrndx));
        h.CopyTo(bytes, 16);
    }

    private byte[] Phdr(uint type, uint flags, ulong offset, ulong vaddr, ulong filesz, ulong memsz, ulong align)
    {
        var p = new List<byte>();
        if (Is64)
        {
            p.AddRange(U32(type));
            p.AddRange(U32(flags));
            p.AddRange(U64(offset));
            p.AddRange(U64(vaddr));
            p.AddRange(U64(vaddr));
            p.AddRange(U64(filesz));
            p.AddRange(U64(memsz));
            p.AddRange(U64(align));
        }
        else
        {
            p.AddRange(U32(type));
            p.AddRange(U32((uint)offset));
            p.AddRange(U32((uint)vaddr));
            p.AddRange(U32((uint)vaddr));
            p.AddRange(U32((uint)filesz));
            p.AddRange(U32((uint)memsz));
            p.AddRange(U32(flags));
            p.AddRange(U32((uint)align));
        }

        return p.ToArray();
    }

    private byte[] SectionHeader(Section s, ulong addr)
    {
        var h = new List<byte>();
        h.AddRange(U32(s.NameOffset));
        h.AddRange(U32(s.Type));
        h.AddRange(Word == 8 ? U64(s.Flags) : U32((uint)s.Flags));
        h.AddRange(Word == 8 ? U64(addr) : U32((uint)addr));
        h.AddRange(Word == 8 ? U64((ulong)s.Offset) : U32((uint)s.Offset));
        h.AddRange(Word == 8 ? U64((ulong)s.Size) : U32((uint)s.Size));
        h.AddRange(U32((uint)s.Link));
        h.AddRange(U32((uint)s.Info));
        h.AddRange(Word == 8 ? U64((ulong)s.Align) : U32((uint)s.Align));
        h.AddRange(Word == 8 ? U64(s.EntSize) : U32((uint)s.EntSize));
        return h.ToArray();
    }

    private byte[] Sym(uint name, ulong value, ulong size, byte info, ushort shndx)
    {
        var s = new List<byte>();
        s.AddRange(U32(name));
        if (Is64)
        {
            s.Add(info);
            s.Add(0);
            s.AddRange(U16(shndx));
            s.AddRange(U64(value));
            s.AddRange(U64(size));
        }
        else
        {
            s.AddRange(U32((uint)value));
            s.AddRange(U32((uint)size));
            s.Add(info);
            s.Add(0);
            s.AddRange(U16(shndx));
        }

        return s.ToArray();
    }

    private static byte[] U16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        return b;
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    private static byte[] U64(ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        return b;
    }

    private sealed class Section(string name, uint type, ulong flags, int offset, int size, int align, ulong entSize)
    {
        public string Name { get; } = name;

        public uint Type { get; } = type;

        public ulong Flags { get; } = flags;

        public int Offset { get; } = offset;

        public int Size { get; } = size;

        public int Align { get; } = align;

        public ulong EntSize { get; } = entSize;

        public uint NameOffset { get; set; }

        public int Link { get; set; }

        public int Info { get; set; }
    }

    private sealed class StringTable
    {
        private readonly List<byte> _bytes = [0];

        public byte[] Bytes => _bytes.ToArray();

        public uint Add(string s)
        {
            uint at = (uint)_bytes.Count;
            _bytes.AddRange(Encoding.ASCII.GetBytes(s));
            _bytes.Add(0);
            return at;
        }
    }
}
