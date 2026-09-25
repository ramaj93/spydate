using Spydate.Core.Binary;
using Spydate.Core.Dex;

namespace Spydate.Tests;

/// <summary>
/// The DEX parser: a hand-built file with every part the parser reads — ids, a class with supertypes, fields
/// with static values, methods with code, try ranges and debug tables, and annotations — and hostile input,
/// which must come back as a <see cref="DexFormatException"/> or a warning, never anything else.
/// </summary>
public sealed class DexTests
{
    private const string Greeter = "Lcom/example/Greeter;";

    internal static byte[] GreeterDex()
    {
        var dex = new SyntheticDex();
        int println = dex.Method("Ljava/io/PrintStream;", "println", "V", "Ljava/lang/String;");
        int output = dex.Field("Ljava/lang/System;", "out", "Ljava/io/PrintStream;");
        int hello = dex.String("hello");

        // void greet(String name) { try { System.out.println("hello"); } catch (RuntimeException e) { } catch (Throwable) { } }
        ushort[] greet =
        [
            0x0062, (ushort)output,         // 0: sget-object v0, System.out
            0x011A, (ushort)hello,          // 2: const-string v1, "hello"
            0x206E, (ushort)println, 0x0010, // 4: invoke-virtual {v0, v1}, println
            0x000E,                         // 7: return-void
            0x000D,                         // 8: move-exception v0
            0x000E,                         // 9: return-void
        ];
        var code = new SyntheticDex.Code(3, 2, 2, greet)
        {
            Tries = [(0, 7, [("Ljava/lang/RuntimeException;", 8)], 9)],
            HasDebug = true,
            LineStart = 10,
            ParameterNames = ["name"],

            // start v0 as "out" of PrintStream at 0, then address +4, line +1.
            DebugProgram = [0x03, 0x00, (byte)(dex.String("out") + 1), (byte)(dex.Type("Ljava/io/PrintStream;") + 1), (byte)(0x0A + (4 * 15) + 5)],
        };

        dex.Class(Greeter, 0x0001, "Ljava/lang/Object;", "Greeter.java", "Ljava/lang/Runnable;")
            .StaticField("COUNT", "I", 0x0019, SyntheticDex.Value.Int(42))
            .InstanceField("name", "Ljava/lang/String;")
            .Method("<init>", "V", [], 0x10001, new SyntheticDex.Code(1, 1, 0, [0x000E]))
            .Method("greet", "V", ["Ljava/lang/String;"], 0x0001, code)
            .Method("run", "V", [], 0x0401, null)
            .Annotate(dex.Annotate("Ldalvik/annotation/Signature;", 2, ("value", SyntheticDex.Value.Array(SyntheticDex.Value.Str(dex, "Ljava/lang/Object;"), SyntheticDex.Value.Str(dex, "Ljava/lang/Runnable;")))))
            .Annotate(dex.Annotate("Ldalvik/annotation/InnerClass;", 2, ("accessFlags", SyntheticDex.Value.Int(9)), ("name", SyntheticDex.Value.Null())));
        return dex.Build();
    }

    [Fact]
    public void AHandBuiltDexReadsBackWhole()
    {
        var dex = DexFile.Parse(GreeterDex());

        Assert.Equal(35, dex.Version);
        Assert.True(dex.ChecksumMatches);
        Assert.Empty(dex.Warnings);
        var greeter = Assert.Single(dex.Classes);
        Assert.Equal("com/example/Greeter", greeter.Name);
        Assert.Equal("Ljava/lang/Object;", greeter.Super);
        Assert.Equal(["Ljava/lang/Runnable;"], greeter.Interfaces);
        Assert.Equal("Greeter.java", greeter.SourceFile);

        var count = Assert.Single(greeter.StaticFields);
        Assert.Equal(("COUNT", "I"), (count.Ref.Name, count.Ref.Type));
        Assert.Equal(42L, count.StaticValue!.Value);
        Assert.Equal("name", Assert.Single(greeter.InstanceFields).Ref.Name);

        var init = Assert.Single(greeter.DirectMethods);
        Assert.True(init.Access.HasFlag(DexAccess.Constructor));
        Assert.Equal("()V", init.Ref.Proto.Descriptor);

        var greet = greeter.VirtualMethods.Single(m => m.Ref.Name == "greet");
        Assert.Equal("(Ljava/lang/String;)V", greet.Ref.Proto.Descriptor);
        var code = greet.Code!;
        Assert.Equal((3, 2, 10), (code.Registers, code.Ins, code.Insns.Length));
        var attempt = Assert.Single(code.Tries);
        Assert.Equal((0, 7, 9), (attempt.Start, attempt.Length, attempt.CatchAll));
        Assert.Equal(("Ljava/lang/RuntimeException;", 8), Assert.Single(attempt.Handlers));
        Assert.Equal(["name"], code.Debug!.ParameterNames);
        Assert.Equal((4, 11), Assert.Single(code.Debug.Lines));
        var local = Assert.Single(code.Debug.Locals);
        Assert.Equal((0, "out", "Ljava/io/PrintStream;", 0, 10), (local.Register, local.Name, local.Type, local.Start, local.End));

        Assert.Null(greeter.VirtualMethods.Single(m => m.Ref.Name == "run").Code);
        var signature = greeter.Annotations.Single(a => a.Type == "Ldalvik/annotation/Signature;");
        Assert.Equal(DexVisibility.System, signature.Visibility);
        Assert.Equal(["Ljava/lang/Object;", "Ljava/lang/Runnable;"], ((IReadOnlyList<DexValue>)signature["value"]!.Value!).Select(v => (string?)v.Value));
        var inner = greeter.Annotations.Single(a => a.Type == "Ldalvik/annotation/InnerClass;");
        Assert.Equal(DexValueKind.Null, inner["name"]!.Kind);
    }

    [Fact]
    public void ADebugTableSharedWithLongerMethodsStopsAtThisOnesEnd()
    {
        // R8 gives every method that maps pc to line the same way one shared table, as long as the longest of them:
        // for a two-unit method it goes on past the end. That is the table working, not damage — the runtime reads
        // it up to the method's end, and so does the parser, without a warning and keeping the lines it has.
        var dex = new SyntheticDex();
        var code = new SyntheticDex.Code(1, 0, 0, [0x0012, 0x000E])   // const/4 v0, #0 ; return-void
        {
            HasDebug = true,
            LineStart = 1,

            // One line per code unit: address 0, then +1 at a time, well past the two units there are.
            DebugProgram = [0x0F, 0x1E, 0x1E, 0x1E, 0x1E],
        };
        dex.Class("Lcom/example/Short;", 0x0001, "Ljava/lang/Object;", null)
            .Method("f", "V", [], 0x0009, code);

        var parsed = DexFile.Parse(dex.Build());

        Assert.Empty(parsed.Warnings);
        var debug = Assert.Single(parsed.Classes).DirectMethods.Single().Code!.Debug!;
        Assert.Equal([(0, 2), (1, 3), (2, 4)], debug.Lines);
    }

    [Theory]
    [InlineData(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 0x34 })]
    [InlineData(new byte[] { (byte)'d', (byte)'e', (byte)'x', (byte)'\n', (byte)'0', (byte)'3', (byte)'5', 0 })]
    public void WhatIsNotADexOrIsCutShortIsSaidToBe(byte[] bytes)
    {
        Assert.Throws<DexFormatException>(() => DexFile.Parse(bytes));
    }

    [Fact]
    public void AByteSwappedDexIsNamed()
    {
        byte[] bytes = GreeterDex();
        bytes[0x28] = 0x12;
        bytes[0x29] = 0x34;
        bytes[0x2A] = 0x56;
        bytes[0x2B] = 0x78;
        var error = Assert.Throws<DexFormatException>(() => DexFile.Parse(bytes));
        Assert.Contains("byte-swapped", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedDexStillReadsButSaysItWasChanged()
    {
        byte[] bytes = GreeterDex();
        bytes[^20] ^= 0xFF; // inside the data, past everything the parser needs
        var dex = DexFile.Parse(bytes);

        Assert.False(dex.ChecksumMatches);
        Assert.Contains(dex.Warnings, w => w.Contains("checksum", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryTruncationIsADexFormatErrorOrAWarning()
    {
        byte[] whole = GreeterDex();
        for (int length = 0; length < whole.Length; length++)
        {
            try
            {
                DexFile.Parse(whole.AsMemory(0, length));
            }
            catch (DexFormatException)
            {
            }
        }
    }

    [Fact]
    public void RandomDamageIsADexFormatErrorOrAWarning()
    {
        byte[] whole = GreeterDex();
        var random = new Random(20260924);
        for (int round = 0; round < 3000; round++)
        {
            byte[] bytes = (byte[])whole.Clone();
            for (int k = 0; k < 1 + random.Next(6); k++)
            {
                int at = 0x08 + random.Next(bytes.Length - 0x08);
                bytes[at] = (byte)random.Next(256);
            }

            try
            {
                var dex = DexFile.Parse(bytes);
                _ = dex.Classes.Sum(c => c.Methods.Count());
            }
            catch (DexFormatException)
            {
            }
        }
    }

    [Fact]
    public void EveryOperandFormatDecodes()
    {
        ushort[] code =
        [
            0x2101,                         // 0: move v1, v2
            0xF412,                         // 1: const/4 v4, -1
            0x0513, 0xFFFE,                 // 2: const/16 v5, -2
            0x0615, 0x4120,                 // 4: const/high16 v6, 0x41200000
            0x0718, 0x5678, 0x1234, 0xDEF0, 0x9ABC, // 6: const-wide v7, 0x9ABCDEF012345678
            0x2132, 0x0005,                 // 11: if-eq v1, v2, +5 → 16
            0x216E, 0x0007, 0x0021,         // 13: invoke-virtual {v1, v2}, method@7
            0x0376, 0x0009, 0x0004,         // 16: invoke-direct/range {v4 .. v6}, method@9
            0x0290, 0x5401,                 // 19: add-int v2, v1, v84
            0x03D8, 0xFF02,                 // 21: add-int/lit8 v3, v2, -1
            0x000E,                         // 23: return-void
        ];
        var list = Dalvik.Decode(code);

        Assert.Equal(["move", "const/4", "const/16", "const/high16", "const-wide", "if-eq", "invoke-virtual", "invoke-direct/range", "add-int", "add-int/lit8", "return-void"], list.Select(i => i.Mnemonic));
        Assert.Equal((1, 2), (list[0].A, list[0].B));
        Assert.Equal((4, -1L), (list[1].A, list[1].Literal));
        Assert.Equal(-2L, list[2].Literal);
        Assert.Equal(0x41200000L, list[3].Literal);
        Assert.Equal(unchecked((long)0x9ABCDEF012345678), list[4].Literal);
        Assert.Equal((1, 2, 16), (list[5].A, list[5].B, list[5].Target));
        Assert.Equal(7, list[6].Index);
        Assert.Equal([1, 2], list[6].Registers);
        Assert.Equal(9, list[7].Index);
        Assert.Equal([4, 5, 6], list[7].Registers);
        Assert.Equal((2, 1, 84), (list[8].A, list[8].B, list[8].C));
        Assert.Equal((3, 2, -1L), (list[9].A, list[9].B, list[9].Literal));
        Assert.All(list, i => Assert.Null(i.Problem));
    }

    [Fact]
    public void SwitchesAndArrayFillsReadTheirPayloads()
    {
        ushort[] code =
        [
            0x002B, 0x000A, 0x0000,         // 0: packed-switch v0, payload at 0 + 10
            0x002C, 0x0010, 0x0000,         // 3: sparse-switch v0, payload at 3 + 16
            0x0026, 0x0015, 0x0000,         // 6: fill-array-data v0, payload at 6 + 21
            0x000E,                         // 9: return-void; the payloads follow
            0x0100, 0x0002, 0x000A, 0x0000, 0x0009, 0x0000, 0x0009, 0x0000, // 10: packed: keys 10, 11 → 0 + 9
            0x0000,                         // 18: nop
            0x0200, 0x0001, 0x0064, 0x0000, 0x0006, 0x0000, // 19: sparse: key 100 → 3 + 6
            0x0000, 0x0000,                 // 25: nops
            0x0300, 0x0002, 0x0002, 0x0000, 0xFFFF, 0x0007, // 27: fill: width 2, 2 elements: -1, 7
        ];

        var list = Dalvik.Decode(code);

        Assert.Equal(["packed-switch", "sparse-switch", "fill-array-data", "return-void", "nop", "nop", "nop"], list.Select(i => i.Mnemonic));
        Assert.Equal([(10, 9), (11, 9)], list[0].Cases!);
        Assert.Equal([(100, 9)], list[1].Cases!);
        Assert.Equal(2, list[2].ArrayData!.Value.Width);
        Assert.Equal([-1L, 7L], list[2].ArrayData!.Value.Values);
    }

    [Fact]
    public void ACutOffOrUnusedInstructionIsAProblemNotAnError()
    {
        Assert.Equal("the instruction runs past the end of the code", Assert.Single(Dalvik.Decode(new ushort[] { 0x0118 })).Problem);
        Assert.StartsWith("opcode 0x3E", Assert.Single(Dalvik.Decode(new ushort[] { 0x003E })).Problem, StringComparison.Ordinal);
        Assert.Equal("its payload is missing or cut off", Assert.Single(Dalvik.Decode(new ushort[] { 0x002B, 0x0100, 0x0000 })).Problem);
    }

    [Fact]
    public void ADexIsBinaryParseException()
    {
        // Callers that catch every format's parse failure catch this one too.
        Assert.True(typeof(BinaryParseException).IsAssignableFrom(typeof(DexFormatException)));
    }
}
