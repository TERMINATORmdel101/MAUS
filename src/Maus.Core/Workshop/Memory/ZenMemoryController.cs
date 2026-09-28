using System.Buffers.Binary;

namespace Maus.Core.Workshop.Memory;

/// <summary>Lecture d'un registre du processeur AMD par le réseau interne SMN (pilote PawnIO requis).</summary>
public interface ISmnReader
{
    uint Read(uint address);
}

/// <summary>Table de télémétrie (« PM table ») du microcontrôleur de gestion d'énergie AMD (SMU).</summary>
public interface IPmTableReader
{
    /// <summary>Version de la table, ou <c>null</c> si elle est illisible.</summary>
    uint? Version();

    /// <summary>Les <paramref name="size"/> premiers octets de la table, fraîchement mise à jour.</summary>
    byte[] Read(int size);
}

/// <summary>Réglages du contrôleur mémoire qui ne sont pas des durées.</summary>
public sealed record ControllerSettings(bool GearDownMode, bool Command2T, bool PowerDown, bool BankGroupSwap, bool BankGroupSwapAlt, string RefreshMode);

/// <summary>Timings réellement appliqués par le contrôleur mémoire (ceux choisis par le BIOS), lus dans ses registres.</summary>
public sealed record LiveMemoryTimings(
    bool Ddr5,
    IReadOnlyList<int> Channels,
    double MclkMhz,
    int SpeedMts,
    IReadOnlyList<MemoryTiming> Timings,
    ControllerSettings Settings,
    bool ChannelsDiffer);

/// <summary>Horloges et tensions du processeur liées à la mémoire, lues dans la table PM.</summary>
public sealed record MemoryClocks(double? FclkMhz, double? UclkMhz, double? MclkMhz, double? SocVolts, double? VddpVolts, double? VddgIodVolts, double? VddgCcdVolts, uint TableVersion, bool GenericLayout)
{
    /// <summary>Rapport UCLK:MCLK (« 1:1 » idéal, « 1:2 » au-delà de ce que tient le contrôleur).</summary>
    public string? UclkMode => UclkMhz is { } u && MclkMhz is { } m && m > 0
        ? Math.Abs(u - m) / m < 0.05 ? "1:1" : Math.Abs((u * 2) - m) / m < 0.05 ? "1:2" : null
        : null;

    /// <summary>UCLK nettement plus haute que MCLK : impossible en fonctionnement normal, signe d'une table PM mal interprétée.</summary>
    public bool UclkAboveMclk => UclkMhz is { } u && MclkMhz is { } m && m > 0 && u > m * 1.05;
}

/// <summary>
/// Contrôleur mémoire des processeurs AMD Ryzen (Zen 2 à Zen 5) : canaux actifs, timings primaires, secondaires et tertiaires,
/// réglages (GDM, 1T/2T, Power Down, BGS), horloges FCLK / UCLK / MCLK. Carte des registres UMC et des tables PM reprise de
/// ZenStates-Core (GPL-3.0, https://github.com/irusanov/ZenStates-Core), faits matériels vérifiés par ses utilisateurs ;
/// MAUS les relit sans rien écrire. Intel : voir <see cref="IntelMemoryController"/>.
/// </summary>
public static class ZenMemoryController
{
    private const int MaxChannels = 12;

    private readonly record struct Field(uint Register, string Key, TimingGroup Group, int High, int Low);

    private static readonly Field[] Common =
    [
        new(0x50204, "tCL", TimingGroup.Primary, 5, 0),
        new(0x50204, "tRCDRD", TimingGroup.Primary, 21, 16),
        new(0x50204, "tRCDWR", TimingGroup.Primary, 29, 24),
        new(0x50208, "tRP", TimingGroup.Primary, 21, 16),
        new(0x50204, "tRAS", TimingGroup.Primary, 14, 8),
        new(0x50208, "tRC", TimingGroup.Primary, 7, 0),
        new(0x5020C, "tRRDS", TimingGroup.Secondary, 4, 0),
        new(0x5020C, "tRRDL", TimingGroup.Secondary, 12, 8),
        new(0x50210, "tFAW", TimingGroup.Secondary, 7, 0),
        new(0x50214, "tWTRS", TimingGroup.Secondary, 12, 8),
        new(0x50214, "tWTRL", TimingGroup.Secondary, 22, 16),
        new(0x50218, "tWR", TimingGroup.Secondary, 7, 0),
        new(0x5020C, "tRTP", TimingGroup.Secondary, 28, 24),
        new(0x50214, "tCWL", TimingGroup.Secondary, 5, 0),
        new(0x50220, "tRDRDSCL", TimingGroup.Tertiary, 29, 24),
        new(0x50224, "tWRWRSCL", TimingGroup.Tertiary, 29, 24),
        new(0x50220, "tRDRDSC", TimingGroup.Tertiary, 19, 16),
        new(0x50220, "tRDRDSD", TimingGroup.Tertiary, 11, 8),
        new(0x50220, "tRDRDDD", TimingGroup.Tertiary, 3, 0),
        new(0x50224, "tWRWRSC", TimingGroup.Tertiary, 19, 16),
        new(0x50224, "tWRWRSD", TimingGroup.Tertiary, 11, 8),
        new(0x50224, "tWRWRDD", TimingGroup.Tertiary, 3, 0),
        new(0x50228, "tRDWR", TimingGroup.Tertiary, 13, 8),
        new(0x50228, "tWRRD", TimingGroup.Tertiary, 3, 0),
        new(0x50254, "tCKE", TimingGroup.Tertiary, 28, 24),
        new(0x50254, "tXP", TimingGroup.Tertiary, 5, 0),
        new(0x50250, "tSTAG", TimingGroup.Tertiary, 26, 16),
        new(0x50234, "tMOD", TimingGroup.Tertiary, 13, 8),
        new(0x50234, "tMRD", TimingGroup.Tertiary, 5, 0),
        new(0x50234, "tMODPDA", TimingGroup.Tertiary, 29, 24),
        new(0x50234, "tMRDPDA", TimingGroup.Tertiary, 21, 16),
        new(0x50258, "tPHYWRD", TimingGroup.Tertiary, 26, 24),
        new(0x50258, "tPHYWRL", TimingGroup.Tertiary, 15, 8),
        new(0x50258, "tPHYRDL", TimingGroup.Tertiary, 23, 16),
        new(0x5021C, "tTRCPAGE", TimingGroup.Tertiary, 31, 20),
    ];

    private static readonly Field[] Ddr5Only =
    [
        new(0x50208, "tRPpb", TimingGroup.Tertiary, 29, 24),
        new(0x50208, "tRCpb", TimingGroup.Tertiary, 15, 8),
        new(0x5021C, "tPPD", TimingGroup.Tertiary, 2, 0),
    ];

    /// <summary>Canaux mémoire actifs (numéro et décalage de leur bloc de registres).</summary>
    public static IReadOnlyList<(int Index, uint Offset)> Channels(ISmnReader smn)
    {
        var channels = new List<(int, uint)>();
        for (var i = 0; i < MaxChannels; i++)
        {
            var offset = (uint)i << 20;
            var disabled = ((smn.Read(offset | 0x50DF0) >> 19) & 1) != 0;
            var dimm1 = (smn.Read(offset | 0x50000) & 1) != 0;
            var dimm2 = (smn.Read(offset | 0x50008) & 1) != 0;
            if (!disabled && (dimm1 || dimm2))
            {
                channels.Add((i, offset));
            }
        }

        return channels;
    }

    /// <summary>Timings du premier canal actif ; <c>null</c> si aucun canal n'est lisible (pilote absent, processeur non géré).</summary>
    public static LiveMemoryTimings? ReadTimings(ISmnReader smn, bool ddr5)
    {
        var channels = Channels(smn);
        if (channels.Count == 0)
        {
            return null;
        }

        var offset = channels[0].Offset;
        uint Reg(uint register) => smn.Read(offset | register);
        var ratioRegister = Reg(0x50200);
        // Rapport × BCLK (100 MHz supposés) : DDR5 en centièmes, DDR4 en tiers.
        var mclk = ddr5 ? (double)Bits(ratioRegister, 15, 0) : Bits(ratioRegister, 6, 0) / 3.0 * 100;

        if (mclk is < 400 or > 6000)
        {
            return null;
        }

        var timings = new List<MemoryTiming>();
        foreach (var field in ddr5 ? Common.Concat(Ddr5Only) : Common)
        {
            var value = (int)Bits(Reg(field.Register), field.High, field.Low);
            timings.Add(new MemoryTiming(field.Key, field.Group, value));
        }

        // Préambules : codés à partir de zéro dans le registre.
        var preamble = Reg(0x502A4);
        var wrpre = (int)Bits(preamble, 10, 8) + 1;
        var rdpreRaw = (int)Bits(preamble, 2, 0);
        timings.Add(new MemoryTiming("tWRPRE", TimingGroup.Tertiary, wrpre));
        timings.Add(new MemoryTiming("tRDPRE", TimingGroup.Tertiary, rdpreRaw < 2 ? rdpreRaw + 1 : rdpreRaw));

        // Rafraîchissement : un des registres porte la vraie valeur, les autres la valeur par défaut.
        double Ns(int clocks) => Math.Round(clocks * 1000.0 / mclk, 1);
        if (ddr5)
        {
            var rfc = new uint[] { Reg(0x50260), Reg(0x50264), Reg(0x50268), Reg(0x5026C) }.FirstOrDefault(v => v != 0 && v != 0x00C00138);
            if (rfc != 0)
            {
                timings.Add(new MemoryTiming("tRFC", TimingGroup.Secondary, (int)Bits(rfc, 15, 0), Ns((int)Bits(rfc, 15, 0))));
                timings.Add(new MemoryTiming("tRFC2", TimingGroup.Secondary, (int)Bits(rfc, 31, 16), Ns((int)Bits(rfc, 31, 16))));
            }

            var rfcsb = new uint[] { Reg(0x502C0), Reg(0x502C4), Reg(0x502C8), Reg(0x502CC) }.Select(v => (int)Bits(v, 10, 0)).FirstOrDefault(v => v != 0);
            if (rfcsb != 0)
            {
                timings.Add(new MemoryTiming("tRFCsb", TimingGroup.Secondary, rfcsb, Ns(rfcsb)));
            }
        }
        else
        {
            var r0 = Reg(0x50260);
            var r1 = Reg(0x50264);
            var rfc = r0 != r1 ? (r0 != 0x21060138 ? r0 : r1) : r0;
            if (rfc != 0)
            {
                timings.Add(new MemoryTiming("tRFC", TimingGroup.Secondary, (int)Bits(rfc, 10, 0), Ns((int)Bits(rfc, 10, 0))));
                timings.Add(new MemoryTiming("tRFC2", TimingGroup.Secondary, (int)Bits(rfc, 21, 11), Ns((int)Bits(rfc, 21, 11))));
                timings.Add(new MemoryTiming("tRFC4", TimingGroup.Secondary, (int)Bits(rfc, 31, 22), Ns((int)Bits(rfc, 31, 22))));
            }
        }

        var refi = (int)Bits(Reg(0x50230), 15, 0);
        timings.Add(new MemoryTiming("tREFI", TimingGroup.Secondary, refi, Ns(refi)));

        var refresh = Reg(0x5012C);
        var fgr = Bits(refresh, 18, 16);
        var refreshMode = ddr5
            ? (Bits(refresh, 1, 1), fgr) switch
            {
                (0, 0) => "normal",
                (0, _) => "FGR",
                (_, 0) => "per-bank",
                _ => "mixed",
            }
            : fgr < 2 ? "normal" : fgr == 2 ? "FGR 2x" : "FGR 4x";

        var settings = new ControllerSettings(
            GearDownMode: Bits(ratioRegister, ddr5 ? 18 : 11, ddr5 ? 18 : 11) == 1,
            Command2T: Bits(ratioRegister, ddr5 ? 17 : 10, ddr5 ? 17 : 10) == 1,
            PowerDown: Bits(refresh, 28, 28) == 1,
            BankGroupSwap: !(Reg(0x50050) == 0x87654321 && Reg(0x50058) == 0x87654321),
            BankGroupSwapAlt: Bits(Reg(0x500D0), 10, 4) > 0 || Bits(Reg(0x500D4), 10, 4) > 0,
            RefreshMode: refreshMode);

        // Canaux réglés différemment : rare, signalé plutôt que caché.
        var differ = channels.Skip(1).Any(c => smn.Read(c.Offset | 0x50204) != smn.Read(offset | 0x50204) || smn.Read(c.Offset | 0x50208) != smn.Read(offset | 0x50208));
        return new LiveMemoryTimings(ddr5, channels.Select(c => c.Index).ToList(), mclk, (int)Math.Round(mclk * 2), timings, settings, differ);
    }

    private readonly record struct PmLayout(uint Version, int Size, int Fclk, int Uclk, int Mclk, int Soc, int Vddp, int VddgIod, int VddgCcd);

    // version, taille, FCLK, UCLK, MCLK, VDDCR_SOC, VDDP, VDDG IOD, VDDG CCD (-1 = absent). Source : ZenStates-Core, PowerTable.cs.
    private static readonly PmLayout[] Layouts =
    [
        // Zen 2 (Matisse)
        new(0x240802, 0x7E0, 0xBC, 0xC4, 0xC8, 0xB0, 0x1F0, 0x1F4, -1),
        new(0x240902, 0x514, 0xBC, 0xC4, 0xC8, 0xB0, 0x1F0, 0x1F4, -1),
        new(0x240003, 0x18AC, 0xB0, 0xB8, 0xBC, 0xA4, 0x1E4, 0x1E8, -1),
        new(0x240503, 0xD7C, 0xC0, 0xC8, 0xCC, 0xB4, 0x1F4, 0x1F8, -1),
        new(0x240603, 0xAB0, 0xC0, 0xC8, 0xCC, 0xB4, 0x1F4, 0x1F8, -1),
        new(0x240703, 0x7E4, 0xC0, 0xC8, 0xCC, 0xB4, 0x1F4, 0x1F8, -1),
        new(0x240803, 0x7E4, 0xC0, 0xC8, 0xCC, 0xB4, 0x1F4, 0x1F8, -1),
        new(0x240903, 0x518, 0xC0, 0xC8, 0xCC, 0xB4, 0x1F4, 0x1F8, -1),
        new(0x000200, 0x7E4, 0xB0, 0xB8, 0xBC, 0xA4, 0x1E4, 0x1E8, -1),
        new(0x000202, 0x7E4, 0xBC, 0xC4, 0xC8, 0xB0, 0x1F0, 0x1F4, -1),
        new(0x000203, 0x7E4, 0xC0, 0xC8, 0xCC, 0xB4, 0x1F4, 0x1F8, -1),

        // Zen 2 Threadripper (Castle Peak)
        new(0x2D0008, 0x1AB0, 0xBC, 0xC4, 0xC8, 0xB0, 0x220, 0x224, -1),
        new(0x2D0803, 0x894, 0xBC, 0xC4, 0xC8, 0xB0, 0x220, 0x224, -1),
        new(0x2D0903, 0x7E4, 0xBC, 0xC4, 0xC8, 0xB0, 0x220, 0x224, -1),

        // Zen 3 (Vermeer)
        new(0x380005, 0x1BB0, 0xC0, 0xC8, 0xCC, 0xB4, 0x224, 0x228, 0x22C),
        new(0x380505, 0xF30, 0xC0, 0xC8, 0xCC, 0xB4, 0x224, 0x228, 0x22C),
        new(0x380605, 0xC10, 0xC0, 0xC8, 0xCC, 0xB4, 0x224, 0x228, 0x22C),
        new(0x380705, 0x8F0, 0xC0, 0xC8, 0xCC, 0xB4, 0x224, 0x228, 0x22C),
        new(0x380804, 0x8A4, 0xC0, 0xC8, 0xCC, 0xB4, 0x224, 0x228, 0x22C),
        new(0x380805, 0x8F0, 0xC0, 0xC8, 0xCC, 0xB4, 0x224, 0x228, 0x22C),
        new(0x380904, 0x5A4, 0xC0, 0xC8, 0xCC, 0xB4, 0x224, 0x228, 0x22C),
        new(0x380905, 0x5D0, 0xC0, 0xC8, 0xCC, 0xB4, 0x224, 0x228, 0x22C),
        new(0x000300, 0x948, 0xC0, 0xC8, 0xCC, 0xB4, 0x224, 0x228, 0x22C),

        // APU Zen 1 et Zen+ (Raven Ridge, Picasso) : ZenStates-Core, ryzen_monitor_ng et TuxTimings concordent.
        new(0x1E0004, 0x2A4, 0x298, 0x29C, 0x2A0, 0x104, 0xF0, -1, -1),

        // APU Zen 2 (Renoir) et Zen 3 (Cezanne). Versions 0x370000 à 0x370002 retirées : ZenStates-Core et RyzenAdj
        // placent FCLK et MCLK à des endroits différents ; la disposition générique (« à vérifier ») s'applique.
        new(0x370003, 0x8C8, 0x5CC, 0x5D0, 0x5D4, 0x198, 0x844, -1, -1),
        new(0x370004, 0x8C8, 0x5CC, 0x5D0, 0x5D4, 0x198, 0x844, -1, -1),
        new(0x370005, 0x8C0, 0x5E8, 0x5EC, 0x5F0, 0x198, 0x86C, -1, -1),
        new(0x400001, 0x8D0, 0x624, 0x628, 0x62C, 0x19C, 0x89C, -1, -1),
        new(0x400002, 0x8D0, 0x63C, 0x640, 0x644, 0x19C, 0x8B4, -1, -1),
        new(0x400003, 0x944, 0x660, 0x664, 0x668, 0x19C, 0x8D0, -1, -1),
        new(0x400004, 0x944, 0x664, 0x668, 0x66C, 0x19C, 0x8D4, -1, -1),
        new(0x400005, 0x944, 0x664, 0x668, 0x66C, 0x19C, 0x8D4, -1, -1),
        new(0x450004, 0xAA4, 0x664, 0x668, 0x66C, 0x19C, 0x8D4, -1, -1),
        new(0x450005, 0xAB0, 0x6B0, 0x6B4, 0x6B8, 0x1C8, 0x8D4, -1, -1),

        // Zen 4 (Raphael, AM5)
        new(0x540100, 0x618, 0x118, 0x128, 0x138, 0xD0, 0x430, -1, -1),
        new(0x540101, 0x61C, 0x118, 0x128, 0x138, 0xD0, 0x430, -1, -1),
        new(0x540102, 0x66C, 0x118, 0x128, 0x138, 0xD0, 0x430, -1, -1),
        new(0x540103, 0x68C, 0x118, 0x128, 0x138, 0xD0, 0x430, -1, -1),
        new(0x540104, 0x6A8, 0x118, 0x128, 0x138, 0xD0, 0x430, -1, -1),
        new(0x540105, 0x6B4, 0x118, 0x128, 0x138, 0xD0, 0x430, -1, -1),
        new(0x540108, 0x6BC, 0x118, 0x128, 0x138, 0xD0, 0x430, -1, -1),
        new(0x540000, 0x828, 0x118, 0x128, 0x138, 0xD0, 0x430, -1, -1),
        new(0x540001, 0x82C, 0x118, 0x128, 0x138, 0xD0, 0x430, -1, -1),
        new(0x540002, 0x87C, 0x118, 0x128, 0x138, 0xD0, 0x430, -1, -1),
        new(0x540003, 0x89C, 0x118, 0x128, 0x138, 0xD0, 0x430, -1, -1),
        new(0x540004, 0x8BC, 0x118, 0x128, 0x138, 0xD0, 0x430, -1, -1),
        new(0x540005, 0x8C8, 0x118, 0x128, 0x138, 0xD0, 0x430, -1, -1),
        new(0x540208, 0x8D0, 0x11C, 0x12C, 0x13C, 0xD4, 0x434, -1, -1),
        new(0x000400, 0x948, 0x118, 0x128, 0x138, 0xD0, 0x430, -1, -1),

        // APU Zen 4 (Phoenix, Hawk Point) : horloges seulement
        new(0x4C0008, 0xAF0, 0x164, 0x174, 0x184, -1, -1, -1, -1),
        new(0x4C0009, 0xB00, 0x164, 0x174, 0x184, -1, -1, -1, -1),

        // Zen 5 (Granite Ridge, AM5)
        new(0x620105, 0x724, 0x11C, 0x12C, 0x13C, 0xD8, 0x434, 0x40C, 0x414),
        new(0x620205, 0x994, 0x11C, 0x12C, 0x13C, 0xD8, 0x434, 0x40C, 0x414),
        new(0x621102, 0x724, 0x11C, 0x12C, 0x13C, 0xD8, 0x434, 0x40C, 0x414),
        new(0x621202, 0x994, 0x11C, 0x12C, 0x13C, 0xD8, 0x434, 0x40C, 0x414),
        new(0x000620, 0x994, 0x11C, 0x12C, 0x13C, 0xD8, 0x434, -1, -1),
    ];

    /// <summary>Disposition de la table PM pour cette version : exacte, sinon celle de la famille (marquée « générique »).</summary>
    internal static (PmLayoutInfo Layout, bool Generic)? LayoutFor(uint version)
    {
        if (Layouts.FirstOrDefault(l => l.Version == version) is { Size: > 0 } exact)
        {
            return (Info(exact), false);
        }

        uint? generic = (version >> 16) switch
        {
            0x24 => (version & 0x7) switch { 0 => 0x000200, 1 or 2 or 4 => 0x000202, _ => 0x000203 },
            0x38 => 0x000300,
            0x37 => 0x370005,
            0x1E => 0x1E0004,
            0x40 or 0x45 => 0x400005,
            0x54 => 0x000400,
            0x62 => 0x000620,
            _ => null,
        };
        return generic is { } g && Layouts.FirstOrDefault(l => l.Version == g) is { Size: > 0 } layout ? (Info(layout), true) : null;
    }

    internal readonly record struct PmLayoutInfo(int Size, int Fclk, int Uclk, int Mclk, int Soc, int Vddp, int VddgIod, int VddgCcd);

    private static PmLayoutInfo Info(PmLayout l) => new(l.Size, l.Fclk, l.Uclk, l.Mclk, l.Soc, l.Vddp, l.VddgIod, l.VddgCcd);

    /// <summary>FCLK, UCLK, MCLK et tensions SoC / VDDP / VDDG ; <c>null</c> si la version de table est inconnue ou illisible.</summary>
    public static MemoryClocks? ReadClocks(IPmTableReader pm)
    {
        if (pm.Version() is not { } version || LayoutFor(version) is not { } found)
        {
            return null;
        }

        var (layout, generic) = found;
        var table = pm.Read(layout.Size);
        double? Float(int offset, double min, double max)
        {
            if (offset < 0 || offset + 4 > table.Length)
            {
                return null;
            }

            var value = BinaryPrimitives.ReadSingleLittleEndian(table.AsSpan(offset, 4));
            return float.IsFinite(value) && value >= min && value <= max ? Math.Round(value, 3) : null;
        }

        var clocks = new MemoryClocks(
            Float(layout.Fclk, 300, 4000),
            Float(layout.Uclk, 300, 5000),
            Float(layout.Mclk, 300, 5000),
            Float(layout.Soc, 0.5, 1.6),
            Float(layout.Vddp, 0.5, 1.6),
            Float(layout.VddgIod, 0.5, 1.6),
            Float(layout.VddgCcd, 0.5, 1.6),
            version,
            generic);
        return clocks.FclkMhz is null && clocks.MclkMhz is null ? null : clocks;
    }

    internal static uint Bits(uint value, int high, int low) => (value >> low) & (uint)((1L << (high - low + 1)) - 1);
}
