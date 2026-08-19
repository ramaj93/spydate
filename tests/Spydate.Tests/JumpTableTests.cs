using Spydate.Core.Symbols;
using Spydate.Disassembly;

namespace Spydate.Tests;

/// <summary>Recovering the targets behind a switch dispatch, and refusing to guess when it is not one.</summary>
public class JumpTableTests
{
    private static Function Discover(byte[] code, ulong baseVa, int bitness, DiscoveryOptions? options = null)
    {
        var symbols = new SymbolTable();
        var source = new MemoryCodeSource(code, baseVa, bitness);
        var dis = new X86Disassembler(bitness, symbols);
        return new FunctionDiscovery(source, dis, symbols, options).Discover(baseVa);
    }

    /// <summary>
    /// 32-bit dispatch: <c>cmp eax,2 ; ja default ; jmp [eax*4+table]</c>, three cases and a default,
    /// with the table of absolute addresses at the end.
    /// </summary>
    private static byte[] Absolute32()
    {
        var code = new byte[0x40];
        Array.Fill(code, (byte)0xCC);
        var write = (int offset, byte[] bytes) => bytes.CopyTo(code, offset);

        write(0x00, new byte[] { 0x83, 0xF8, 0x02 });                          // 1000 cmp eax, 2
        write(0x03, new byte[] { 0x77, 0x1B });                                // 1003 ja 0x1020
        write(0x05, new byte[] { 0xFF, 0x24, 0x85, 0x30, 0x10, 0x00, 0x00 });  // 1005 jmp [eax*4+0x1030]
        write(0x0C, new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 });        // 100C case 0
        write(0x14, new byte[] { 0xB8, 0x02, 0x00, 0x00, 0x00, 0xC3 });        // 1014 case 1
        write(0x1A, new byte[] { 0x31, 0xC0, 0xC3 });                          // 101A case 2
        write(0x20, new byte[] { 0xB8, 0xFF, 0xFF, 0xFF, 0xFF, 0xC3 });        // 1020 default
        write(0x30, new byte[] { 0x0C, 0x10, 0x00, 0x00, 0x14, 0x10, 0x00, 0x00, 0x1A, 0x10, 0x00, 0x00 });
        return code;
    }

    /// <summary>
    /// 64-bit dispatch: the entries are deltas from the base <c>lea</c> loaded, which on a real image is
    /// the image base — so they read as RVAs.
    /// </summary>
    private static byte[] Relative64()
    {
        var code = new byte[0x48];
        Array.Fill(code, (byte)0xCC);
        var write = (int offset, byte[] bytes) => bytes.CopyTo(code, offset);

        write(0x00, new byte[] { 0x83, 0xF9, 0x01 });                                // 1000 cmp ecx, 1
        write(0x03, new byte[] { 0x77, 0x2B });                                      // 1003 ja 0x1030
        write(0x05, new byte[] { 0x48, 0x8D, 0x15, 0xF4, 0xFF, 0xFF, 0xFF });        // 1005 lea rdx, [0x140001000]
        write(0x0C, new byte[] { 0x8B, 0x84, 0x8A, 0x40, 0x00, 0x00, 0x00 });        // 100C mov eax, [rdx+rcx*4+0x40]
        write(0x13, new byte[] { 0x48, 0x01, 0xD0 });                                // 1013 add rax, rdx
        write(0x16, new byte[] { 0xFF, 0xE0 });                                      // 1016 jmp rax
        write(0x20, new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 });              // 1020 case 0
        write(0x26, new byte[] { 0x31, 0xC0, 0xC3 });                                // 1026 case 1
        write(0x30, new byte[] { 0xB8, 0xFF, 0xFF, 0xFF, 0xFF, 0xC3 });              // 1030 default
        write(0x40, new byte[] { 0x20, 0x00, 0x00, 0x00, 0x26, 0x00, 0x00, 0x00 });  // 1040 table (RVAs)
        return code;
    }

    [Fact]
    public void AbsoluteTableIsFollowed()
    {
        var function = Discover(Absolute32(), 0x1000, 32);

        var table = Assert.Single(function.JumpTables);
        Assert.Equal(0x1030u, table.TableVa);
        Assert.Equal(JumpTableKind.Absolute, table.Kind);
        Assert.True(table.CountFromBoundsCheck);
        Assert.Equal(new ulong[] { 0x100C, 0x1014, 0x101A }, table.Targets);

        // Every case is now part of the function, not left for the gap sweep.
        foreach (ulong target in table.Targets)
        {
            Assert.True(function.BlockByVa.ContainsKey(target), $"no block at 0x{target:X}");
        }

        Assert.Contains(function.Notes, n => n.Contains("Switch table", StringComparison.Ordinal));
    }

    [Fact]
    public void RelativeTableIsFollowed()
    {
        var function = Discover(Relative64(), 0x140001000, 64);

        var table = Assert.Single(function.JumpTables);
        Assert.Equal(0x140001040u, table.TableVa);
        Assert.Equal(JumpTableKind.RelativeToBase, table.Kind);
        Assert.Equal(0x140001000u, table.BaseVa);
        Assert.True(table.CountFromBoundsCheck);
        Assert.Equal(new ulong[] { 0x140001020, 0x140001026 }, table.Targets);
        Assert.True(function.BlockByVa.ContainsKey(0x140001026));
    }

    [Fact]
    public void TheRangeCheckDecidesHowManyEntriesAreRead()
    {
        // The table holds three addresses, but `cmp eax,1 ; ja` only lets two of them be reached.
        var code = Absolute32();
        code[2] = 0x01;

        var table = Assert.Single(Discover(code, 0x1000, 32).JumpTables);

        Assert.Equal(2, table.Targets.Count);
        Assert.True(table.CountFromBoundsCheck);
    }

    [Fact]
    public void EntriesThatAreNotCodeEndTheTable()
    {
        // Without a range check the entries themselves are the only limit, so an address outside the
        // executable region stops the read rather than inventing a block.
        var code = Absolute32();
        code[3] = 0x90;                                     // ja -> nop, so there is no bound
        code[4] = 0x90;
        BitConverter.GetBytes(0x9000u).CopyTo(code, 0x38);  // third entry points nowhere

        var table = Assert.Single(Discover(code, 0x1000, 32).JumpTables);

        Assert.Equal(new ulong[] { 0x100C, 0x1014 }, table.Targets);
        Assert.False(table.CountFromBoundsCheck);
    }

    [Fact]
    public void AnIndirectJumpThatIsNotADispatchIsLeftAlone()
    {
        // jmp rax with nothing in front of it: a virtual call or a guard thunk, not a switch.
        var function = Discover(new byte[] { 0xFF, 0xE0 }, 0x140001000, 64);

        Assert.Empty(function.JumpTables);
        Assert.Contains(function.Notes, n => n.Contains("possible switch table not followed", StringComparison.Ordinal));
    }

    [Fact]
    public void FollowingCanBeTurnedOff()
    {
        var options = DiscoveryOptions.Default with { FollowJumpTables = false };
        var function = Discover(Absolute32(), 0x1000, 32, options);

        Assert.Empty(function.JumpTables);
        Assert.False(function.BlockByVa.ContainsKey(0x101A));
    }
}
