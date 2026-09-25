using System.Globalization;
using System.Text;

namespace Maus.Core.Workshop.Memory;

/// <summary>
/// Décodage de la puce SPD des barrettes DDR4 (JESD21-C annexe L) et DDR5 (JESD400-5), avec les profils Intel XMP 2.0 / 3.0
/// et AMD EXPO. Chaque emplacement d'octet est recoupé avec au moins une source ouverte (relevé du 25/09/2026) :
/// memtest86+ (<c>system/spd.c</c>, GPL-2.0), ZenStates-Core (<c>Ddr5SpdDecoder.cs</c>, GPL-3.0), spdr
/// (<c>timing.rs</c> et <c>vendor.rs</c>, Apache-2.0 : timings DDR5 des octets 70 à 93, en-têtes XMP 3.0 et EXPO),
/// DDR5SPDEditor (<c>ddr5spd_structs.h</c>, GPL-3.0 : profils XMP 3.0 et EXPO complets, bits d'activation) et
/// DDR4XMPEditor (<c>XMP.cs</c> : profils XMP 2.0 complets). Rien n'est deviné : un champ sans source n'est pas lu.
/// </summary>
public static class SpdDecoder
{
    public const byte Ddr4Type = 0x0C;
    public const byte Ddr5Type = 0x12;

    /// <summary>Décode une image SPD ; <c>null</c> si ce n'est ni de la DDR4 ni de la DDR5.</summary>
    public static SpdModule? Decode(SpdImage image) => image.Bytes.Length > 2
        ? image.Bytes[2] switch
        {
            Ddr4Type when image.Bytes.Length >= 256 => DecodeDdr4(image),
            Ddr5Type when image.Bytes.Length >= 512 => DecodeDdr5(image),
            _ => null,
        }
        : null;

    /// <summary>CRC-16 des SPD JEDEC (polynôme 0x1021, valeur initiale 0).</summary>
    public static int Crc16(ReadOnlySpan<byte> data)
    {
        var crc = 0;
        foreach (var value in data)
        {
            crc ^= value << 8;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
            }
        }

        return crc & 0xFFFF;
    }

    // ---------------------------------------------------------------- DDR4

    private const double Mtb = 0.125;
    private const double Ftb = 0.001;

    private static SpdModule DecodeDdr4(SpdImage image)
    {
        var b = image.Bytes;
        var densityCode = b[4] & 0x0F;
        int[] densities = [0, 0, 1, 2, 4, 8, 16, 32, 12, 24];
        var densityGbit = densityCode < densities.Length ? densities[densityCode] : 0;
        var deviceWidth = 4 << (b[12] & 0x07);
        var ranks = ((b[12] >> 3) & 0x07) + 1;
        var busWidth = 8 << (b[13] & 0x07);
        var dies = ((b[6] >> 4) & 0x07) + 1;
        var is3ds = (b[6] & 0x80) != 0 && (b[6] & 0x03) == 2;
        var logicalRanks = ranks * (is3ds ? dies : 1);
        var capacity = densityCode <= 1
            ? 0L
            : (long)densityGbit * 1024 * 1024 * 1024 / 8 * (busWidth / deviceWidth) * logicalRanks;

        var profiles = new List<SpdProfile> { Ddr4Jedec(b) };
        if (b.Length >= 487 && b[384] == 0x0C && b[385] == 0x4A)
        {
            var version = $"{b[387] >> 4}.{b[387] & 0x0F}";
            for (var n = 1; n <= 2; n++)
            {
                if ((b[386] & (1 << (n - 1))) != 0 && Ddr4Xmp(b, n, version) is { } xmp)
                {
                    profiles.Add(xmp);
                }
            }
        }

        return new SpdModule
        {
            Slot = image.Slot,
            MemoryType = "DDR4",
            ModuleType = ModuleType(b[3] & 0x0F, ddr5: false),
            CapacityBytes = capacity,
            Ranks = ranks,
            DeviceWidth = deviceWidth,
            DieDensityGbit = densityGbit,
            DiesPerPackage = dies,
            BankGroups = ((b[4] >> 6) & 0x03) switch { 1 => 2, 2 => 4, _ => 1 },
            BanksPerGroup = ((b[4] >> 4) & 0x03) == 1 ? 8 : 4,
            ColumnBits = 9 + (b[5] & 0x07),
            RowBits = 12 + ((b[5] >> 3) & 0x07),
            Ecc = ((b[13] >> 3) & 0x03) == 1,
            SpdRevision = $"{b[1] >> 4}.{b[1] & 0x0F}",
            ChecksumOk = Crc16(b.AsSpan(0, 126)) == (b[126] | (b[127] << 8)),
            HasThermalSensor = (b[14] & 0x80) != 0,
            PartNumber = b.Length >= 349 ? Ascii(b, 329, 20) : string.Empty,
            ModuleManufacturer = image.ModuleManufacturer,
            DramManufacturer = image.DramManufacturer,
            DramStepping = b.Length > 352 && b[352] is not (0 or 0xFF) ? b[352] : null,
            ManufacturedYear = b.Length > 324 ? Year(b[323]) : null,
            ManufacturedWeek = b.Length > 324 ? Week(b[324]) : null,
            Profiles = profiles,
        };
    }

    private static SpdProfile Ddr4Jedec(byte[] b)
    {
        var tck = b[18] * Mtb + (sbyte)b[125] * Ftb;
        var timings = new Ddr4Timings(tck)
        {
            Cl = b[24] * Mtb + (sbyte)b[123] * Ftb,
            Rcd = b[25] * Mtb + (sbyte)b[122] * Ftb,
            Rp = b[26] * Mtb + (sbyte)b[121] * Ftb,
            Ras = (((b[27] & 0x0F) << 8) | b[28]) * Mtb,
            Rc = (((b[27] >> 4) << 8) | b[29]) * Mtb + (sbyte)b[120] * Ftb,
            Rfc1 = ((b[31] << 8) | b[30]) * Mtb,
            Rfc2 = ((b[33] << 8) | b[32]) * Mtb,
            Rfc4 = ((b[35] << 8) | b[34]) * Mtb,
            Faw = (((b[36] & 0x0F) << 8) | b[37]) * Mtb,
            Rrds = b[38] * Mtb + (sbyte)b[119] * Ftb,
            Rrdl = b[39] * Mtb + (sbyte)b[118] * Ftb,
            Ccdl = b[40] * Mtb + (sbyte)b[117] * Ftb,
            Wr = (((b[41] & 0x0F) << 8) | b[42]) * Mtb,
            Wtrs = (((b[43] & 0x0F) << 8) | b[44]) * Mtb,
            Wtrl = (((b[43] >> 4) << 8) | b[45]) * Mtb,
        };
        return timings.ToProfile(ProfileKind.Jedec, 0, null, null, Ddr4ClList(b, 20), 1.2, null, null);
    }

    private static SpdProfile? Ddr4Xmp(byte[] b, int number, string version)
    {
        // Profil 1 aux octets 393 à 439, profil 2 aux octets 440 à 486 ; corrections fines rangées à l'envers en fin de profil.
        var p = 393 + (47 * (number - 1));
        var tck = b[p + 3] * Mtb + (sbyte)b[p + 38] * Ftb;
        if (tck <= 0.2)
        {
            return null;
        }

        var timings = new Ddr4Timings(tck)
        {
            Cl = b[p + 8] * Mtb + (sbyte)b[p + 37] * Ftb,
            Rcd = b[p + 9] * Mtb + (sbyte)b[p + 36] * Ftb,
            Rp = b[p + 10] * Mtb + (sbyte)b[p + 35] * Ftb,
            Ras = (((b[p + 11] & 0x0F) << 8) | b[p + 12]) * Mtb,
            Rc = (((b[p + 11] >> 4) << 8) | b[p + 13]) * Mtb + (sbyte)b[p + 34] * Ftb,
            Rfc1 = ((b[p + 15] << 8) | b[p + 14]) * Mtb,
            Rfc2 = ((b[p + 17] << 8) | b[p + 16]) * Mtb,
            Rfc4 = ((b[p + 19] << 8) | b[p + 18]) * Mtb,
            Faw = (((b[p + 20] & 0x0F) << 8) | b[p + 21]) * Mtb,
            Rrds = b[p + 22] * Mtb + (sbyte)b[p + 33] * Ftb,
            Rrdl = b[p + 23] * Mtb + (sbyte)b[p + 32] * Ftb,
        };

        // Tension : bit 7 = volts entiers, bits 6:0 = centièmes (1,35 V = 0xA3), comme DDR4XMPEditor.
        var voltage = ((b[p] >> 7) & 1) + (b[p] & 0x7F) / 100.0;
        // Latences CAS d'un profil XMP : 3 octets seulement (CL 7 à 30), l'octet suivant n'est pas documenté.
        var xmpCl = b[p + 4] | (b[p + 5] << 8) | (b[p + 6] << 16);
        var cls = Enumerable.Range(0, 24).Where(bit => (xmpCl & (1 << bit)) != 0).Select(bit => 7 + bit).ToList();
        return timings.ToProfile(ProfileKind.Xmp, number, version, null, cls, voltage is > 0.9 and < 2.0 ? voltage : null, null, null);
    }

    private static List<int> Ddr4ClList(byte[] b, int offset)
    {
        var mask = b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | ((b[offset + 3] & 0x3F) << 24);
        var first = (b[offset + 3] & 0x80) != 0 ? 23 : 7;
        return Enumerable.Range(0, 30).Where(bit => (mask & (1 << bit)) != 0).Select(bit => first + bit).ToList();
    }

    /// <summary>Durées DDR4 en nanosecondes, converties en cycles à la fréquence du profil.</summary>
    private sealed class Ddr4Timings(double tck)
    {
        public double Cl { get; init; }

        public double Rcd { get; init; }

        public double Rp { get; init; }

        public double Ras { get; init; }

        public double Rc { get; init; }

        public double Rfc1 { get; init; }

        public double Rfc2 { get; init; }

        public double Rfc4 { get; init; }

        public double Faw { get; init; }

        public double Rrds { get; init; }

        public double Rrdl { get; init; }

        public double Ccdl { get; init; }

        public double Wr { get; init; }

        public double Wtrs { get; init; }

        public double Wtrl { get; init; }

        public SpdProfile ToProfile(ProfileKind kind, int number, string? version, string? name, IReadOnlyList<int> cls, double? vdd, double? vddq, double? vpp)
        {
            var speed = Ddr4Speed(tck);
            var cycle = 2000.0 / speed;
            var timings = new List<MemoryTiming>();
            void Add(string key, TimingGroup group, double ns, bool showNs = false)
            {
                if (ns > 0 && ns < 5000)
                {
                    timings.Add(new MemoryTiming(key, group, Clocks(ns, cycle), showNs ? ns : null));
                }
            }

            Add("tCL", TimingGroup.Primary, Cl);
            Add("tRCD", TimingGroup.Primary, Rcd);
            Add("tRP", TimingGroup.Primary, Rp);
            Add("tRAS", TimingGroup.Primary, Ras);
            Add("tRC", TimingGroup.Primary, Rc);
            Add("tRRDS", TimingGroup.Secondary, Rrds);
            Add("tRRDL", TimingGroup.Secondary, Rrdl);
            Add("tFAW", TimingGroup.Secondary, Faw);
            Add("tCCDL", TimingGroup.Secondary, Ccdl);
            Add("tWTRS", TimingGroup.Secondary, Wtrs);
            Add("tWTRL", TimingGroup.Secondary, Wtrl);
            Add("tWR", TimingGroup.Secondary, Wr);
            Add("tRFC", TimingGroup.Secondary, Rfc1, showNs: true);
            Add("tRFC2", TimingGroup.Secondary, Rfc2, showNs: true);
            Add("tRFC4", TimingGroup.Secondary, Rfc4, showNs: true);
            return new SpdProfile(kind, number, version, name, speed, tck, cls, timings, vdd, vddq, vpp);
        }
    }

    /// <summary>Fréquence DDR4 arrondie aux paliers usuels (x00, x33, x66), comme memtest86+.</summary>
    internal static int Ddr4Speed(double tckNs)
    {
        var raw = 2000.0 / tckNs;
        var rounded = Math.Round(raw / 100) * 100;
        var delta = rounded - raw;
        if (delta < -16.5)
        {
            rounded += 33;
        }
        else if (delta > 16.5)
        {
            rounded -= 34;
        }

        return (int)rounded;
    }

    // ---------------------------------------------------------------- DDR5

    private static SpdModule DecodeDdr5(SpdImage image)
    {
        var b = image.Bytes;
        int[] densities = [0, 4, 8, 12, 16, 24, 32, 48, 64];
        var densityCode = b[4] & 0x1F;
        var densityGbit = densityCode < densities.Length ? densities[densityCode] : 0;
        var dies = ((b[4] >> 5) & 0x07) switch { 2 => 2, 3 => 4, 4 => 8, 5 => 16, _ => 1 };
        var deviceWidth = 4 << ((b[6] >> 5) & 0x07);
        var ranks = ((b[234] >> 3) & 0x07) + 1;
        var busWidth = 8 << (b[235] & 0x07);
        var subChannels = ((b[235] >> 5) & 0x03) + 1;
        var capacity = densityGbit == 0 || deviceWidth == 0
            ? 0L
            : (long)densityGbit * 1024 * 1024 * 1024 / 8 * dies * (busWidth / deviceWidth) * ranks * subChannels;

        var profiles = new List<SpdProfile> { Ddr5Jedec(b) };
        if (b.Length >= 896 && b[640] == 0x0C && b[641] == 0x4A)
        {
            var version = $"{b[642] >> 4}.{b[642] & 0x0F}";
            for (var n = 1; n <= 3; n++)
            {
                var name = Ascii(b, 654 + (16 * (n - 1)), 16);
                if ((b[643] & (1 << (n - 1))) != 0
                    && Ddr5Profile(b, ProfileKind.Xmp, n, version, name.Length > 0 ? name : null, 704 + (64 * (n - 1)), vppAt: 0, vddAt: 1, vddqAt: 2, tckAt: 5) is { } xmp)
                {
                    profiles.Add(xmp);
                }
            }
        }

        if (b.Length >= 922 && b[832] == 'E' && b[833] == 'X' && b[834] == 'P' && b[835] == 'O')
        {
            var version = $"{b[836] >> 4}.{b[836] & 0x0F}";
            for (var n = 1; n <= 2; n++)
            {
                // Bits d'activation EXPO : profil 1 = bit 0, profil 2 = bit 4 (DDR5SPDEditor).
                if ((b[837] & (1 << (n == 1 ? 0 : 4))) != 0
                    && Ddr5Profile(b, ProfileKind.Expo, n, version, null, 842 + (40 * (n - 1)), vppAt: 2, vddAt: 0, vddqAt: 1, tckAt: 4) is { } expo)
                {
                    profiles.Add(expo);
                }
            }
        }

        string? pmic = (b[200] & 0x80) != 0
            ? (b[200] & 0x0F) switch
            {
                0 => "PMIC5000",
                1 => "PMIC5010",
                2 => "PMIC5100",
                3 => "PMIC5020",
                4 => "PMIC5120",
                5 => "PMIC5200",
                6 => "PMIC5030",
                _ => null,
            }
            : null;

        return new SpdModule
        {
            Slot = image.Slot,
            MemoryType = "DDR5",
            ModuleType = ModuleType(b[3] & 0x0F, ddr5: true),
            CapacityBytes = capacity,
            Ranks = ranks,
            DeviceWidth = deviceWidth,
            DieDensityGbit = densityGbit,
            DiesPerPackage = dies,
            BankGroups = 1 << ((b[7] >> 5) & 0x07),
            BanksPerGroup = 1 << (b[7] & 0x07),
            ColumnBits = 10 + ((b[5] >> 5) & 0x07),
            RowBits = 16 + (b[5] & 0x1F),
            SubChannels = subChannels,
            Ecc = ((b[235] >> 3) & 0x03) != 0,
            SpdRevision = $"{b[1] >> 4}.{b[1] & 0x0F}",
            ChecksumOk = Crc16(b.AsSpan(0, 510)) == (b[510] | (b[511] << 8)),
            HasThermalSensor = ((b[14] >> 3) & 1) != 0,
            Pmic = pmic,
            PartNumber = b.Length >= 551 ? Ascii(b, 521, 30) : string.Empty,
            ModuleManufacturer = image.ModuleManufacturer,
            DramManufacturer = image.DramManufacturer,
            DramStepping = b.Length > 554 && b[554] is not (0 or 0xFF) ? b[554] : null,
            ManufacturedYear = b.Length > 516 ? Year(b[515]) : null,
            ManufacturedWeek = b.Length > 516 ? Week(b[516]) : null,
            Profiles = profiles,
        };
    }

    private static SpdProfile Ddr5Jedec(byte[] b)
    {
        var tckPs = U16(b, 20);
        var speed = Ddr5Speed(tckPs);
        var cycle = 2000.0 / speed;
        var timings = Ddr5Core(b, 30, cycle);

        // Timings secondaires JEDEC (octets 70 à 93, confirmés par spdr) : durée minimale en ps puis plancher en cycles.
        void Secondary(string key, int offset)
        {
            var ps = U16(b, offset);
            if (ps is > 0 and < 100_000)
            {
                timings.Add(new MemoryTiming(key, TimingGroup.Secondary, Math.Max(Clocks(ps / 1000.0, cycle), b[offset + 2])));
            }
        }

        Secondary("tRRDL", 70);
        Secondary("tCCDL", 73);
        Secondary("tCCDL_WR", 76);
        Secondary("tCCDL_WR2", 79);
        Secondary("tFAW", 82);
        Secondary("tWTRL", 85);
        Secondary("tWTRS", 88);
        Secondary("tRTP", 91);
        var cls = Enumerable.Range(0, 40).Where(bit => (b[24 + (bit / 8)] & (1 << (bit % 8))) != 0).Select(bit => 20 + (2 * bit)).ToList();
        return new SpdProfile(ProfileKind.Jedec, 0, null, null, speed, tckPs / 1000.0, cls, timings, 1.1, 1.1, 1.8);
    }

    private static SpdProfile? Ddr5Profile(byte[] b, ProfileKind kind, int number, string version, string? name, int p, int vppAt, int vddAt, int vddqAt, int tckAt)
    {
        var tckPs = U16(b, p + tckAt);
        if (tckPs is < 100 or > 2000)
        {
            return null;
        }

        var speed = (int)Math.Round(2_000_000.0 / tckPs / 100) * 100;
        var cycle = 2000.0 / speed;
        var timings = Ddr5Core(b, p + tckAt + (kind == ProfileKind.Xmp ? 8 : 2), cycle);
        if (kind == ProfileKind.Xmp)
        {
            // Profil XMP 3.0 (64 octets, DDR5SPDEditor) : durée en ps puis plancher en cycles, à partir de +31.
            foreach (var (key, at) in new[] { ("tRRDL", 31), ("tCCDL_WR", 34), ("tCCDL_WR2", 37), ("tWTRL", 40), ("tWTRS", 43), ("tCCDL", 46), ("tRTP", 49), ("tFAW", 52) })
            {
                var ps = U16(b, p + at);
                if (ps is > 0 and < 100_000)
                {
                    timings.Add(new MemoryTiming(key, TimingGroup.Secondary, Math.Max(Clocks(ps / 1000.0, cycle), b[p + at + 2])));
                }
            }
        }
        else
        {
            // Fin du profil EXPO (DDR5SPDEditor) : tRRD_L, tCCD_L, tCCD_L_WR, tCCD_L_WR2, tFAW, tCCD_L_WTR, tCCD_S_WTR, tRTP en ps.
            string[] keys = ["tRRDL", "tCCDL", "tCCDL_WR", "tCCDL_WR2", "tFAW", "tWTRL", "tWTRS", "tRTP"];
            for (var i = 0; i < keys.Length; i++)
            {
                var ps = U16(b, p + 24 + (2 * i));
                if (ps is > 0 and < 100_000)
                {
                    timings.Add(new MemoryTiming(keys[i], TimingGroup.Secondary, Clocks(ps / 1000.0, cycle)));
                }
            }
        }

        var cls = kind == ProfileKind.Xmp
            ? Enumerable.Range(0, 40).Where(bit => (b[p + 7 + (bit / 8)] & (1 << (bit % 8))) != 0).Select(bit => 20 + (2 * bit)).ToList()
            : [];
        // Somme de contrôle : 2 derniers octets de chaque profil XMP, ou du bloc EXPO entier (832 à 959).
        var checksum = kind == ProfileKind.Xmp
            ? Crc16(b.AsSpan(p, 62)) == U16(b, p + 62)
            : Crc16(b.AsSpan(832, 126)) == U16(b, 958);
        return new SpdProfile(kind, number, version, name, speed, tckPs / 1000.0, cls, timings, Volts(b[p + vddAt]), Volts(b[p + vddqAt]), Volts(b[p + vppAt]))
        {
            ChecksumOk = checksum,
        };
    }

    /// <summary>tAA, tRCD, tRP, tRAS, tRC, tWR (ps) puis tRFC1, tRFC2, tRFCsb (ns), dans cet ordre à partir de <paramref name="offset"/>.</summary>
    private static List<MemoryTiming> Ddr5Core(byte[] b, int offset, double cycle)
    {
        var timings = new List<MemoryTiming>();
        var cl = Clocks(U16(b, offset) / 1000.0, cycle);
        timings.Add(new MemoryTiming("tCL", TimingGroup.Primary, cl + (cl % 2)));
        timings.Add(new MemoryTiming("tRCD", TimingGroup.Primary, Clocks(U16(b, offset + 2) / 1000.0, cycle)));
        timings.Add(new MemoryTiming("tRP", TimingGroup.Primary, Clocks(U16(b, offset + 4) / 1000.0, cycle)));
        timings.Add(new MemoryTiming("tRAS", TimingGroup.Primary, Clocks(U16(b, offset + 6) / 1000.0, cycle)));
        timings.Add(new MemoryTiming("tRC", TimingGroup.Primary, Clocks(U16(b, offset + 8) / 1000.0, cycle)));
        timings.Add(new MemoryTiming("tWR", TimingGroup.Secondary, Clocks(U16(b, offset + 10) / 1000.0, cycle)));
        foreach (var (key, at) in new[] { ("tRFC", 12), ("tRFC2", 14), ("tRFCsb", 16) })
        {
            var ns = U16(b, offset + at);
            if (ns is > 0 and < 5000)
            {
                timings.Add(new MemoryTiming(key, TimingGroup.Secondary, Clocks(ns, cycle), ns));
            }
        }

        return timings.Where(t => t.Clocks > 0).ToList();
    }

    /// <summary>Fréquence DDR5 : palier JEDEC le plus proche (multiples de 400, puis de 200 au-delà de 6400).</summary>
    internal static int Ddr5Speed(int tckPs) => tckPs <= 0 ? 0 : (int)Math.Round(2_000_000.0 / tckPs / 200) * 200;

    /// <summary>Tension XMP 3.0 / EXPO : bits 7:5 = volts entiers, bits 4:0 = pas de 50 mV (DDR5SPDEditor, <c>ConvertByteToVoltageDDR5</c>).</summary>
    private static double? Volts(byte code)
    {
        var volts = ((code >> 5) & 0x07) + ((code & 0x1F) * 0.05);
        return volts is > 0.9 and < 2.2 ? Math.Round(volts, 3) : null;
    }

    // ---------------------------------------------------------------- communs

    /// <summary>Durée convertie en cycles, arrondie au cycle supérieur avec la tolérance JEDEC (2,5 %).</summary>
    internal static int Clocks(double ns, double cycleNs) => ns <= 0 || cycleNs <= 0 ? 0 : (int)Math.Ceiling((ns / cycleNs) - 0.025);

    private static int U16(byte[] b, int offset) => offset + 1 < b.Length ? b[offset] | (b[offset + 1] << 8) : 0;

    private static string ModuleType(int code, bool ddr5) => code switch
    {
        1 => "RDIMM",
        2 => "UDIMM",
        3 => "SO-DIMM",
        4 => "LRDIMM",
        5 => ddr5 ? "CUDIMM" : "Mini-RDIMM",
        6 => ddr5 ? "CSODIMM" : "Mini-UDIMM",
        7 when ddr5 => "MRDIMM",
        8 => ddr5 ? "CAMM2" : "SO-RDIMM 72 bits",
        9 when !ddr5 => "SO-UDIMM 72 bits",
        _ => string.Create(CultureInfo.InvariantCulture, $"type {code}"),
    };

    private static string Ascii(byte[] b, int offset, int length)
    {
        var text = new StringBuilder();
        for (var i = offset; i < Math.Min(b.Length, offset + length); i++)
        {
            if (b[i] == 0)
            {
                break;
            }

            if (b[i] is >= 0x20 and <= 0x7E)
            {
                text.Append((char)b[i]);
            }
        }

        return text.ToString().Trim();
    }

    private static int? Year(byte bcd) => Bcd(bcd) is { } year and < 80 ? 2000 + year : null;

    private static int? Week(byte bcd) => Bcd(bcd) is { } week and > 0 and <= 53 ? week : null;

    private static int? Bcd(byte value) => (value >> 4) <= 9 && (value & 0x0F) <= 9 ? ((value >> 4) * 10) + (value & 0x0F) : null;
}
