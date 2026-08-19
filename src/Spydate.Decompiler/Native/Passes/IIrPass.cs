using Spydate.Decompiler.Native.IR;

namespace Spydate.Decompiler.Native.Passes;

/// <summary>A transformation over an <see cref="IrFunction"/>. Passes mutate the function in place.</summary>
public interface IIrPass
{
    string Name { get; }

    void Run(IrFunction function);
}

/// <summary>x86 register aliasing helpers used by passes to reason about kills and overlaps.</summary>
public static class RegisterAliases
{
    private static readonly Dictionary<string, string> Canonical = Build();

    private static Dictionary<string, string> Build()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        void Family(string full, params string[] aliases)
        {
            d[full] = full;
            foreach (var a in aliases)
            {
                d[a] = full;
            }
        }

        Family("rax", "eax", "ax", "al", "ah");
        Family("rbx", "ebx", "bx", "bl", "bh");
        Family("rcx", "ecx", "cx", "cl", "ch");
        Family("rdx", "edx", "dx", "dl", "dh");
        Family("rsi", "esi", "si", "sil");
        Family("rdi", "edi", "di", "dil");
        Family("rbp", "ebp", "bp", "bpl");
        Family("rsp", "esp", "sp", "spl");
        for (int i = 8; i <= 15; i++)
        {
            Family($"r{i}", $"r{i}d", $"r{i}w", $"r{i}l", $"r{i}b");
        }

        for (int i = 0; i < 32; i++)
        {
            Family($"zmm{i}", $"ymm{i}", $"xmm{i}");
        }

        return d;
    }

    /// <summary>Canonical (widest) register name for aliasing purposes; unknown names map to themselves.</summary>
    public static string CanonicalOf(string name) => Canonical.TryGetValue(name, out var c) ? c : name;

    public static bool Overlap(string a, string b) => CanonicalOf(a) == CanonicalOf(b);

    /// <summary>
    /// True when writing <paramref name="write"/> completely replaces the value of <paramref name="def"/>.
    /// On x64, 32-bit GPR writes zero-extend and therefore kill the full 64-bit register.
    /// </summary>
    public static bool Kills(IrExpr write, IrExpr def, int bitness)
    {
        if (write is IrTemp wt && def is IrTemp dt)
        {
            return wt.Id == dt.Id;
        }

        if (write is IrLocal wl && def is IrLocal dl)
        {
            return wl.Name == dl.Name && wl.Bits >= dl.Bits;
        }

        if (write is IrReg wr && def is IrReg dr)
        {
            if (!Overlap(wr.Name, dr.Name))
            {
                return false;
            }

            int writeBits = wr.Bits;
            if (bitness == 64 && writeBits == 32 && IsGpr(wr.Name))
            {
                writeBits = 64;
            }

            return writeBits >= dr.Bits;
        }

        return false;
    }

    private static bool IsGpr(string name)
    {
        string c = CanonicalOf(name);
        return c.Length <= 3 && c[0] == 'r' && !c.StartsWith("rip", StringComparison.Ordinal);
    }

    /// <summary>True when the two variables may refer to overlapping storage.</summary>
    public static bool MayAlias(IrExpr a, IrExpr b) => (a, b) switch
    {
        (IrReg x, IrReg y) => Overlap(x.Name, y.Name),
        (IrTemp x, IrTemp y) => x.Id == y.Id,
        (IrLocal x, IrLocal y) => x.Name == y.Name,
        _ => false,
    };
}
