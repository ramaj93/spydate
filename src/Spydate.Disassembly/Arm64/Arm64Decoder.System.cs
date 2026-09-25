using System.Globalization;

namespace Spydate.Disassembly.Arm64;

public static partial class Arm64Decoder
{
    private static readonly string?[] BarrierOptions =
    [
        null, "oshld", "oshst", "osh", null, "nshld", "nshst", "nsh", null, "ishld", "ishst", "ish", null, "ld", "st", "sy",
    ];

    /// <summary>Hints by their number: CRm:op2. The ones missing are written <c>hint #n</c>.</summary>
    private static readonly Dictionary<uint, string> Hints = new()
    {
        [0] = "nop", [1] = "yield", [2] = "wfe", [3] = "wfi", [4] = "sev", [5] = "sevl", [6] = "dgh", [7] = "xpaclri",
        [8] = "pacia1716", [10] = "pacib1716", [12] = "autia1716", [14] = "autib1716",
        [16] = "esb", [17] = "psb csync", [18] = "tsb csync", [20] = "csdb", [22] = "clrbhb",
        [24] = "paciaz", [25] = "paciasp", [26] = "pacibz", [27] = "pacibsp",
        [28] = "autiaz", [29] = "autiasp", [30] = "autibz", [31] = "autibsp",
        [32] = "bti", [34] = "bti c", [36] = "bti j", [38] = "bti jc",
    };

    /// <summary>System registers by op0:op1:CRn:CRm:op2, the ones user code and the kernel read most.</summary>
    private static readonly Dictionary<uint, string> SystemRegisters = BuildSystemRegisters();

    private static uint SysKey(uint op0, uint op1, uint crn, uint crm, uint op2) => (op0 << 14) | (op1 << 11) | (crn << 7) | (crm << 3) | op2;

    private static Dictionary<uint, string> BuildSystemRegisters()
    {
        var map = new Dictionary<uint, string>();
        void Add(string name, uint op0, uint op1, uint crn, uint crm, uint op2) => map[SysKey(op0, op1, crn, crm, op2)] = name;

        Add("nzcv", 3, 3, 4, 2, 0);
        Add("daif", 3, 3, 4, 2, 1);
        Add("svcr", 3, 3, 4, 2, 2);
        Add("dit", 3, 3, 4, 2, 5);
        Add("ssbs", 3, 3, 4, 2, 6);
        Add("tco", 3, 3, 4, 2, 7);
        Add("fpcr", 3, 3, 4, 4, 0);
        Add("fpsr", 3, 3, 4, 4, 1);
        Add("dspsr_el0", 3, 3, 4, 5, 0);
        Add("dlr_el0", 3, 3, 4, 5, 1);
        Add("ctr_el0", 3, 3, 0, 0, 1);
        Add("dczid_el0", 3, 3, 0, 0, 7);
        Add("rndr", 3, 3, 2, 4, 0);
        Add("rndrrs", 3, 3, 2, 4, 1);
        Add("tpidr_el0", 3, 3, 13, 0, 2);
        Add("tpidrro_el0", 3, 3, 13, 0, 3);
        Add("tpidr2_el0", 3, 3, 13, 0, 5);
        Add("cntfrq_el0", 3, 3, 14, 0, 0);
        Add("cntpct_el0", 3, 3, 14, 0, 1);
        Add("cntvct_el0", 3, 3, 14, 0, 2);
        Add("cntpctss_el0", 3, 3, 14, 0, 5);
        Add("cntvctss_el0", 3, 3, 14, 0, 6);
        Add("cntp_tval_el0", 3, 3, 14, 2, 0);
        Add("cntp_ctl_el0", 3, 3, 14, 2, 1);
        Add("cntp_cval_el0", 3, 3, 14, 2, 2);
        Add("cntv_tval_el0", 3, 3, 14, 3, 0);
        Add("cntv_ctl_el0", 3, 3, 14, 3, 1);
        Add("cntv_cval_el0", 3, 3, 14, 3, 2);
        Add("pmcr_el0", 3, 3, 9, 12, 0);
        Add("pmccntr_el0", 3, 3, 9, 13, 0);
        Add("pmuserenr_el0", 3, 3, 9, 14, 0);

        Add("midr_el1", 3, 0, 0, 0, 0);
        Add("mpidr_el1", 3, 0, 0, 0, 5);
        Add("revidr_el1", 3, 0, 0, 0, 6);
        Add("id_aa64pfr0_el1", 3, 0, 0, 4, 0);
        Add("id_aa64pfr1_el1", 3, 0, 0, 4, 1);
        Add("id_aa64zfr0_el1", 3, 0, 0, 4, 4);
        Add("id_aa64smfr0_el1", 3, 0, 0, 4, 5);
        Add("id_aa64dfr0_el1", 3, 0, 0, 5, 0);
        Add("id_aa64dfr1_el1", 3, 0, 0, 5, 1);
        Add("id_aa64isar0_el1", 3, 0, 0, 6, 0);
        Add("id_aa64isar1_el1", 3, 0, 0, 6, 1);
        Add("id_aa64isar2_el1", 3, 0, 0, 6, 2);
        Add("id_aa64mmfr0_el1", 3, 0, 0, 7, 0);
        Add("id_aa64mmfr1_el1", 3, 0, 0, 7, 1);
        Add("id_aa64mmfr2_el1", 3, 0, 0, 7, 2);
        Add("currentel", 3, 0, 4, 2, 2);
        Add("spsel", 3, 0, 4, 2, 0);
        Add("pan", 3, 0, 4, 2, 3);
        Add("uao", 3, 0, 4, 2, 4);
        Add("sp_el0", 3, 0, 4, 1, 0);
        Add("spsr_el1", 3, 0, 4, 0, 0);
        Add("elr_el1", 3, 0, 4, 0, 1);
        Add("sctlr_el1", 3, 0, 1, 0, 0);
        Add("actlr_el1", 3, 0, 1, 0, 1);
        Add("cpacr_el1", 3, 0, 1, 0, 2);
        Add("ttbr0_el1", 3, 0, 2, 0, 0);
        Add("ttbr1_el1", 3, 0, 2, 0, 1);
        Add("tcr_el1", 3, 0, 2, 0, 2);
        Add("apiakeylo_el1", 3, 0, 2, 1, 0);
        Add("apiakeyhi_el1", 3, 0, 2, 1, 1);
        Add("apibkeylo_el1", 3, 0, 2, 1, 2);
        Add("apibkeyhi_el1", 3, 0, 2, 1, 3);
        Add("afsr0_el1", 3, 0, 5, 1, 0);
        Add("afsr1_el1", 3, 0, 5, 1, 1);
        Add("esr_el1", 3, 0, 5, 2, 0);
        Add("far_el1", 3, 0, 6, 0, 0);
        Add("par_el1", 3, 0, 7, 4, 0);
        Add("mair_el1", 3, 0, 10, 2, 0);
        Add("amair_el1", 3, 0, 10, 3, 0);
        Add("vbar_el1", 3, 0, 12, 0, 0);
        Add("isr_el1", 3, 0, 12, 1, 0);
        Add("contextidr_el1", 3, 0, 13, 0, 1);
        Add("tpidr_el1", 3, 0, 13, 0, 4);
        Add("cntkctl_el1", 3, 0, 14, 1, 0);
        Add("csselr_el1", 3, 2, 0, 0, 0);
        Add("ccsidr_el1", 3, 1, 0, 0, 0);
        Add("clidr_el1", 3, 1, 0, 0, 1);
        Add("mdscr_el1", 2, 0, 0, 2, 2);
        Add("oslar_el1", 2, 0, 1, 0, 4);
        Add("mdccsr_el0", 2, 3, 0, 1, 0);
        Add("dbgdtr_el0", 2, 3, 0, 4, 0);
        Add("dbgdtrrx_el0", 2, 3, 0, 5, 0);

        Add("sctlr_el2", 3, 4, 1, 0, 0);
        Add("hcr_el2", 3, 4, 1, 1, 0);
        Add("vbar_el2", 3, 4, 12, 0, 0);
        Add("esr_el2", 3, 4, 5, 2, 0);
        Add("far_el2", 3, 4, 6, 0, 0);
        Add("elr_el2", 3, 4, 4, 0, 1);
        Add("spsr_el2", 3, 4, 4, 0, 0);
        Add("tpidr_el2", 3, 4, 13, 0, 2);
        Add("sctlr_el3", 3, 6, 1, 0, 0);
        Add("scr_el3", 3, 6, 1, 1, 0);
        Add("vbar_el3", 3, 6, 12, 0, 0);
        Add("elr_el3", 3, 6, 4, 0, 1);
        Add("spsr_el3", 3, 6, 4, 0, 0);
        return map;
    }

    /// <summary>
    /// Cache and TLB maintenance, and address translation, by op1:CRn:CRm:op2 — the <c>sys</c> aliases. The bool says
    /// whether the operation takes a register.
    /// </summary>
    private static readonly Dictionary<uint, (string Name, bool TakesRegister)> SysAliases = BuildSysAliases();

    private static Dictionary<uint, (string, bool)> BuildSysAliases()
    {
        var map = new Dictionary<uint, (string, bool)>();
        void Add(string name, uint op1, uint crn, uint crm, uint op2, bool register = true) => map[SysKey(1, op1, crn, crm, op2)] = (name, register);

        Add("ic ialluis", 0, 7, 1, 0, false);
        Add("ic iallu", 0, 7, 5, 0, false);
        Add("ic ivau", 3, 7, 5, 1);
        Add("dc ivac", 0, 7, 6, 1);
        Add("dc isw", 0, 7, 6, 2);
        Add("dc csw", 0, 7, 10, 2);
        Add("dc cisw", 0, 7, 14, 2);
        Add("dc zva", 3, 7, 4, 1);
        Add("dc gva", 3, 7, 4, 3);
        Add("dc gzva", 3, 7, 4, 4);
        Add("dc cvac", 3, 7, 10, 1);
        Add("dc cvau", 3, 7, 11, 1);
        Add("dc cvap", 3, 7, 12, 1);
        Add("dc cvadp", 3, 7, 13, 1);
        Add("dc civac", 3, 7, 14, 1);
        Add("at s1e1r", 0, 7, 8, 0);
        Add("at s1e1w", 0, 7, 8, 1);
        Add("at s1e0r", 0, 7, 8, 2);
        Add("at s1e0w", 0, 7, 8, 3);
        Add("at s1e2r", 4, 7, 8, 0);
        Add("at s1e2w", 4, 7, 8, 1);
        Add("at s1e3r", 6, 7, 8, 0);
        Add("at s1e3w", 6, 7, 8, 1);
        Add("tlbi vmalle1is", 0, 8, 3, 0, false);
        Add("tlbi vae1is", 0, 8, 3, 1);
        Add("tlbi aside1is", 0, 8, 3, 2);
        Add("tlbi vaae1is", 0, 8, 3, 3);
        Add("tlbi vale1is", 0, 8, 3, 5);
        Add("tlbi vaale1is", 0, 8, 3, 7);
        Add("tlbi vmalle1", 0, 8, 7, 0, false);
        Add("tlbi vae1", 0, 8, 7, 1);
        Add("tlbi aside1", 0, 8, 7, 2);
        Add("tlbi vaae1", 0, 8, 7, 3);
        Add("tlbi vale1", 0, 8, 7, 5);
        Add("tlbi vaale1", 0, 8, 7, 7);
        Add("tlbi alle2", 4, 8, 7, 0, false);
        Add("tlbi alle2is", 4, 8, 3, 0, false);
        Add("tlbi alle1", 4, 8, 7, 4, false);
        Add("tlbi alle1is", 4, 8, 3, 4, false);
        Add("tlbi alle3", 6, 8, 7, 0, false);
        Add("tlbi alle3is", 6, 8, 3, 0, false);
        return map;
    }

    private static string SystemRegisterName(uint op0, uint op1, uint crn, uint crm, uint op2)
        => SystemRegisters.TryGetValue(SysKey(op0, op1, crn, crm, op2), out var name)
            ? name
            : string.Create(CultureInfo.InvariantCulture, $"s{op0}_{op1}_c{crn}_c{crm}_{op2}");

    private static Arm64Instruction? SystemInstruction(uint w)
    {
        bool read = Bit(w, 21);
        uint op0 = F(w, 20, 19);
        uint op1 = F(w, 18, 16);
        uint crn = F(w, 15, 12);
        uint crm = F(w, 11, 8);
        uint op2 = F(w, 7, 5);
        uint rt = F(w, 4, 0);

        if (op0 == 0)
        {
            if (read)
            {
                return null;
            }

            // Hints: nop, yield, the pointer authentication and branch target hints, and the rest by number.
            if (crn == 0b0010 && rt == 31 && op1 == 0b011)
            {
                uint hint = (crm << 3) | op2;
                return Hints.TryGetValue(hint, out var name) ? I(w, name) : I(w, "hint", Imm(hint));
            }

            // Barriers.
            if (crn == 0b0011 && rt == 31 && op1 == 0b011)
            {
                switch (op2)
                {
                    case 0b010:
                        return crm == 15 ? I(w, "clrex") : I(w, "clrex", Imm(crm));
                    case 0b100:
                        return crm switch
                        {
                            0 => I(w, "ssbb"),
                            4 => I(w, "pssbb"),
                            _ => I(w, "dsb", Barrier(crm)),
                        };
                    case 0b101:
                        return I(w, "dmb", Barrier(crm));
                    case 0b110:
                        return crm == 15 ? I(w, "isb") : I(w, "isb", Imm(crm));
                    case 0b111 when crm == 0:
                        return I(w, "sb");
                }

                return null;
            }

            // MSR to a PSTATE field, with an immediate.
            if (crn == 0b0100 && rt == 31)
            {
                string? field = (op1, op2) switch
                {
                    (0b000, 0b011) => "uao",
                    (0b000, 0b100) => "pan",
                    (0b000, 0b101) => "spsel",
                    (0b011, 0b010) => "dit",
                    (0b011, 0b100) => "tco",
                    (0b011, 0b110) => "daifset",
                    (0b011, 0b111) => "daifclr",
                    (0b011, 0b001) => "ssbs",
                    _ => null,
                };

                return field is null ? null : I(w, "msr", T(field), Imm(crm));
            }

            return null;
        }

        if (op0 == 1)
        {
            if (read)
            {
                return I(w, "sysl", X(rt), Imm(op1), T("C" + crn.ToString(CultureInfo.InvariantCulture)), T("C" + crm.ToString(CultureInfo.InvariantCulture)), Imm(op2));
            }

            if (SysAliases.TryGetValue(SysKey(1, op1, crn, crm, op2), out var alias))
            {
                int space = alias.Name.IndexOf(' ', StringComparison.Ordinal);
                var operation = T(alias.Name[(space + 1)..]);
                return alias.TakesRegister || rt != 31
                    ? I(w, alias.Name[..space], operation, X(rt))
                    : I(w, alias.Name[..space], operation);
            }

            var parts = new List<Arm64Operand>
            {
                Imm(op1), T("C" + crn.ToString(CultureInfo.InvariantCulture)), T("C" + crm.ToString(CultureInfo.InvariantCulture)), Imm(op2),
            };
            if (rt != 31)
            {
                parts.Add(X(rt));
            }

            return new Arm64Instruction(w, "sys", parts);
        }

        var register = T(SystemRegisterName(op0, op1, crn, crm, op2));
        return read ? I(w, "mrs", X(rt), register) : I(w, "msr", register, X(rt));
    }

    private static Arm64Operand Barrier(uint crm) => BarrierOptions[crm] is { } option ? T(option) : Imm(crm);
}
