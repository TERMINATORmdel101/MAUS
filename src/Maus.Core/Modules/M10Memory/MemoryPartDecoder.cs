using System.Globalization;
using System.Text.RegularExpressions;

namespace Maus.Core.Modules.M10Memory;

/// <summary>Ce qu'annonce une référence de barrette : marque, vitesse nominale et, si elle est lisible, la latence CAS.</summary>
/// <param name="Brand">Marque déduite de la référence.</param>
/// <param name="RatedSpeed">Vitesse nominale en MT/s (celle du profil XMP/EXPO pour un kit de performance).</param>
/// <param name="Generation">3, 4 ou 5 pour DDR3, DDR4 ou DDR5, si la référence l'indique.</param>
/// <param name="CasLatency">Latence CAS annoncée, si la référence l'indique.</param>
/// <param name="IsJedec">Module standard : il tourne à sa vitesse nominale sans profil XMP/EXPO.</param>
internal sealed record MemoryPart(string Brand, int RatedSpeed, int? Generation, int? CasLatency, bool IsJedec);

/// <summary>
/// Décodage des références (<c>Win32_PhysicalMemory.PartNumber</c>) des principales marques.
/// Une référence inconnue renvoie <c>null</c> : la vitesse nominale reste alors indéterminée, jamais supposée.
/// </summary>
internal static partial class MemoryPartDecoder
{
    private static readonly Func<string, MemoryPart?>[] Decoders =
    [
        DecodeCorsair,
        DecodeGSkill,
        DecodeKingston,
        DecodeCrucial,
        DecodeTeamGroup,
        DecodePatriot,
        DecodeAdata,
        DecodeSamsung,
        DecodeSkHynix,
        DecodeMicron,
    ];

    public static MemoryPart? Decode(string? partNumber)
    {
        if (string.IsNullOrWhiteSpace(partNumber))
        {
            return null;
        }

        var part = partNumber.Trim().ToUpperInvariant();
        foreach (var decoder in Decoders)
        {
            if (decoder(part) is { } decoded)
            {
                return decoded;
            }
        }

        return null;
    }

    /// <summary>Corsair : CMK32GX5M2B6000C36 = Vengeance, DDR5, kit de 2, 6000 MT/s, CL36. CMV et CMSO (ValueSelect) sont JEDEC.</summary>
    private static MemoryPart? DecodeCorsair(string part)
    {
        var match = CorsairPattern().Match(part);
        if (!match.Success)
        {
            return null;
        }

        var series = match.Groups[1].Value;
        var generation = Int(match.Groups[2]);
        var speed = Int(match.Groups[3])!.Value;
        var cas = Int(match.Groups[4]);
        var jedec = series is "V" or "SO" || MemorySpeeds.IsJedecTiming(generation, speed, cas);
        return new MemoryPart("Corsair", speed, generation, cas, jedec);
    }

    /// <summary>G.Skill : F4-3200C16D-16GVKB = DDR4-3200 CL16 ; F5-6000J3038F16GX2 = DDR5-6000 CL30.</summary>
    private static MemoryPart? DecodeGSkill(string part)
    {
        var match = GSkillPattern().Match(part);
        if (!match.Success)
        {
            return null;
        }

        var generation = Int(match.Groups[1]);
        var speed = Int(match.Groups[2])!.Value;
        var cas = Int(match.Groups[3]);
        return new MemoryPart("G.Skill", speed, generation, cas, MemorySpeeds.IsJedecTiming(generation, speed, cas));
    }

    /// <summary>
    /// Kingston : KF560C36BBEK2-32 (Fury DDR5-6000 CL36), HX432C16FB3/8 (HyperX DDR4-3200), références SPD KF3200C16D4/16GX
    /// ou KHX3200C16D4/8GX ; ValueRAM (KVR32N22S8/8), Client Premier (KCP432NS8/8) et OEM (ACR26D4…) sont JEDEC.
    /// </summary>
    private static MemoryPart? DecodeKingston(string part)
    {
        if (KingstonFuryPattern().Match(part) is { Success: true } fury)
        {
            var generation = Int(fury.Groups[1]);
            var speed = MemorySpeeds.FromTwoDigits(Int(fury.Groups[2])!.Value);
            var cas = Int(fury.Groups[3]);
            return new MemoryPart("Kingston", speed, generation, cas, MemorySpeeds.IsJedecTiming(generation, speed, cas));
        }

        if (KingstonSpdPattern().Match(part) is { Success: true } spd)
        {
            var speed = Int(spd.Groups[1])!.Value;
            var cas = Int(spd.Groups[2]);
            var generation = Int(spd.Groups[3]) ?? MemorySpeeds.GuessGeneration(speed);
            return new MemoryPart("Kingston", speed, generation, cas, MemorySpeeds.IsJedecTiming(generation, speed, cas));
        }

        if (KingstonValuePattern().Match(part) is { Success: true } value)
        {
            var speed = MemorySpeeds.FromTwoDigits(Int(value.Groups[1])!.Value);
            return new MemoryPart("Kingston", speed, MemorySpeeds.GuessGeneration(speed), Int(value.Groups[2]), IsJedec: true);
        }

        if (KingstonClientPattern().Match(part) is { Success: true } client)
        {
            return new MemoryPart("Kingston", MemorySpeeds.FromTwoDigits(Int(client.Groups[2])!.Value), Int(client.Groups[1]), null, IsJedec: true);
        }

        if (KingstonOemPattern().Match(part) is { Success: true } oem)
        {
            return new MemoryPart("Kingston", MemorySpeeds.FromTwoDigits(Int(oem.Groups[1])!.Value), Int(oem.Groups[2]), null, IsJedec: true);
        }

        return null;
    }

    /// <summary>
    /// Crucial : Ballistix BL2K16G32C16U4B (DDR4-3200 CL16) et BLS8G4D240FSB (DDR4-2400) ; Crucial Pro CP2K16G60C36U5B
    /// (DDR5-6000 CL36) ; CT16G48C40U5 (DDR5-4800) ; CT16G4DFRA32A et CP16G4DFRA32A (DDR4-3200 JEDEC).
    /// </summary>
    private static MemoryPart? DecodeCrucial(string part)
    {
        if (BallistixPattern().Match(part) is { Success: true } ballistix)
        {
            var generation = Int(ballistix.Groups[3]);
            var speed = MemorySpeeds.FromTwoDigits(Int(ballistix.Groups[1])!.Value);
            return new MemoryPart("Crucial Ballistix", speed, generation, Int(ballistix.Groups[2]), IsJedec: false);
        }

        if (BallistixSportPattern().Match(part) is { Success: true } sport)
        {
            return new MemoryPart("Crucial Ballistix", MemorySpeeds.FromDigits(sport.Groups[2].Value), Int(sport.Groups[1]), null, IsJedec: false);
        }

        if (CrucialTimedPattern().Match(part) is { Success: true } timed)
        {
            var speed = MemorySpeeds.FromTwoDigits(Int(timed.Groups[2])!.Value);
            var cas = Int(timed.Groups[3]);
            var generation = Int(timed.Groups[4]) ?? MemorySpeeds.GuessGeneration(speed);
            var jedec = timed.Groups[1].Value == "T" || MemorySpeeds.IsJedecTiming(generation, speed, cas);
            return new MemoryPart("Crucial", speed, generation, cas, jedec);
        }

        if (CrucialDdr4Pattern().Match(part) is { Success: true } ddr4)
        {
            return new MemoryPart("Crucial", MemorySpeeds.FromDigits(ddr4.Groups[2].Value), Int(ddr4.Groups[1]), null, IsJedec: true);
        }

        return null;
    }

    /// <summary>TeamGroup : référence SPD TEAMGROUP-UD4-3200, ou étiquette TF3D416G3200HC16FDC01 ; la gamme Elite (TED) est JEDEC.</summary>
    private static MemoryPart? DecodeTeamGroup(string part)
    {
        if (TeamGroupSpdPattern().Match(part) is { Success: true } spd)
        {
            return new MemoryPart("TeamGroup", Int(spd.Groups[2])!.Value, Int(spd.Groups[1]), null, IsJedec: false);
        }

        if (TeamGroupLabelPattern().Match(part) is { Success: true } label)
        {
            var generation = Int(label.Groups[1]);
            var speed = Int(label.Groups[2])!.Value;
            var cas = Int(label.Groups[3]);
            var jedec = part.StartsWith("TED", StringComparison.Ordinal) || MemorySpeeds.IsJedecTiming(generation, speed, cas);
            return new MemoryPart("TeamGroup", speed, generation, cas, jedec);
        }

        return null;
    }

    /// <summary>
    /// Patriot : Viper PVS416G320C6K (DDR4-3200 CL16) ou PVV532G600C36K (DDR5-6000 CL36) ; Signature PSD416G32002 (JEDEC) ;
    /// référence SPD « 3200 Series » ou « 3200 C16 Series ».
    /// </summary>
    private static MemoryPart? DecodePatriot(string part)
    {
        if (PatriotViperPattern().Match(part) is { Success: true } viper)
        {
            var clDigits = viper.Groups[3].Value;
            var cas = int.Parse(clDigits, CultureInfo.InvariantCulture) + (clDigits.Length == 1 ? 10 : 0);
            return new MemoryPart("Patriot", MemorySpeeds.FromThreeDigits(Int(viper.Groups[2])!.Value), Int(viper.Groups[1]), cas, IsJedec: false);
        }

        if (PatriotSignaturePattern().Match(part) is { Success: true } signature)
        {
            return new MemoryPart("Patriot", Int(signature.Groups[2])!.Value, Int(signature.Groups[1]), null, IsJedec: true);
        }

        if (PatriotSpdPattern().Match(part) is { Success: true } spd)
        {
            var speed = Int(spd.Groups[1])!.Value;
            var cas = Int(spd.Groups[2]);
            return new MemoryPart("Patriot", speed, null, cas, MemorySpeeds.IsJedecTiming(MemorySpeeds.GuessGeneration(speed), speed, cas));
        }

        return null;
    }

    /// <summary>ADATA / XPG : AX4U320016G16A-ST41 (XPG DDR4-3200), AX5U6000C3016G (XPG DDR5-6000) ; AD4U320016G22 (Premier, JEDEC).</summary>
    private static MemoryPart? DecodeAdata(string part)
    {
        var match = AdataPattern().Match(part);
        if (!match.Success)
        {
            return null;
        }

        var jedec = match.Groups[1].Value == "D";
        return new MemoryPart(jedec ? "ADATA" : "ADATA XPG", Int(match.Groups[3])!.Value, Int(match.Groups[2]), null, jedec);
    }

    /// <summary>Samsung (modules d'origine, JEDEC) : M378A1K43CB2-CTD = DDR4-2666 ; M323R2GA3BB0-CQK = DDR5-4800.</summary>
    private static MemoryPart? DecodeSamsung(string part)
    {
        var match = SamsungPattern().Match(part);
        if (!match.Success)
        {
            return null;
        }

        var generation = match.Groups[1].Value == "R" ? 5 : 4;
        int? speed = (generation, match.Groups[2].Value) switch
        {
            (4, "PB") => 2133,
            (4, "RC") => 2400,
            (4, "TD") => 2666,
            (4, "VF") => 2933,
            (4, "WE") => 3200,
            (5, "QK") => 4800,
            (5, "WM") => 5600,
            _ => null,
        };
        return speed is null ? null : new MemoryPart("Samsung", speed.Value, generation, null, IsJedec: true);
    }

    /// <summary>SK Hynix DDR4 (modules d'origine, JEDEC) : HMA81GU6CJR8N-VK = 2666, -XN = 3200.</summary>
    private static MemoryPart? DecodeSkHynix(string part)
    {
        var match = SkHynixPattern().Match(part);
        if (!match.Success)
        {
            return null;
        }

        int? speed = match.Groups[1].Value switch
        {
            "TF" => 2133,
            "UH" => 2400,
            "VK" => 2666,
            "WM" => 2933,
            "XN" => 3200,
            _ => null,
        };
        return speed is null ? null : new MemoryPart("SK Hynix", speed.Value, 4, null, IsJedec: true);
    }

    /// <summary>Micron (modules d'origine, JEDEC) : MTA8ATF1G64AZ-3G2E1 = DDR4-3200 ; MTC8C1084S1UC48BA1 = DDR5-4800.</summary>
    private static MemoryPart? DecodeMicron(string part)
    {
        if (MicronDdr4Pattern().Match(part) is { Success: true } ddr4)
        {
            int? speed = (ddr4.Groups[1].Value + "G" + ddr4.Groups[2].Value) switch
            {
                "2G1" => 2133,
                "2G4" => 2400,
                "2G6" => 2666,
                "2G9" => 2933,
                "3G2" => 3200,
                _ => null,
            };
            return speed is null ? null : new MemoryPart("Micron", speed.Value, 4, null, IsJedec: true);
        }

        if (MicronDdr5Pattern().Match(part) is { Success: true } ddr5)
        {
            return new MemoryPart("Micron", MemorySpeeds.FromTwoDigits(Int(ddr5.Groups[1])!.Value), 5, null, IsJedec: true);
        }

        return null;
    }

    private static int? Int(Group group) =>
        group.Success && int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    [GeneratedRegex(@"^CM([A-Z]{1,4}?)\d{1,3}GX([345])M\d[A-Z](\d{4})(?:C(\d{2}))?", RegexOptions.CultureInvariant)]
    private static partial Regex CorsairPattern();

    [GeneratedRegex(@"^F([2345])-(\d{4})[A-Z](\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex GSkillPattern();

    [GeneratedRegex(@"^(?:KF|HX)([345])(\d{2})[CS](\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex KingstonFuryPattern();

    [GeneratedRegex(@"^K(?:HX|F)(\d{4})C(\d{2})(?:[A-Z]*D([345]))?", RegexOptions.CultureInvariant)]
    private static partial Regex KingstonSpdPattern();

    [GeneratedRegex(@"^KVR(\d{2})[A-Z](\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex KingstonValuePattern();

    [GeneratedRegex(@"^KCP([345])(\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex KingstonClientPattern();

    [GeneratedRegex(@"^ACR(\d{2})D([345])", RegexOptions.CultureInvariant)]
    private static partial Regex KingstonOemPattern();

    [GeneratedRegex(@"^BL[A-Z]?(?:\d+K)?\d+G(\d{2})C(\d{2})[US]([345])", RegexOptions.CultureInvariant)]
    private static partial Regex BallistixPattern();

    [GeneratedRegex(@"^BL[A-Z]{1,2}(?:\d+K)?\d+G([345])D(\d{2,3})", RegexOptions.CultureInvariant)]
    private static partial Regex BallistixSportPattern();

    [GeneratedRegex(@"^C([TP])(?:\d+K)?\d+G(\d{2})C(\d{2})(?:[USK]([345]))?", RegexOptions.CultureInvariant)]
    private static partial Regex CrucialTimedPattern();

    [GeneratedRegex(@"^C[TP](?:\d+K)?\d+G([345])[DS]F[A-Z][A-Z0-9]?(\d{2,3})", RegexOptions.CultureInvariant)]
    private static partial Regex CrucialDdr4Pattern();

    [GeneratedRegex(@"^TEAMGROUP-[US]D([345])-(\d{4})", RegexOptions.CultureInvariant)]
    private static partial Regex TeamGroupSpdPattern();

    [GeneratedRegex(@"^[TF][A-Z0-9]{2,5}?([45])\d{1,3}G(\d{4})H?C(\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex TeamGroupLabelPattern();

    [GeneratedRegex(@"^PV[A-Z]{0,2}\d?([45])\d{1,3}G(\d{3})C(\d{1,2})", RegexOptions.CultureInvariant)]
    private static partial Regex PatriotViperPattern();

    [GeneratedRegex(@"^PSD([345])\d{1,3}G(\d{4})", RegexOptions.CultureInvariant)]
    private static partial Regex PatriotSignaturePattern();

    [GeneratedRegex(@"^(\d{4})\s*(?:C(\d{2})\s*)?SERIES$", RegexOptions.CultureInvariant)]
    private static partial Regex PatriotSpdPattern();

    [GeneratedRegex(@"^A([DX])([45])[USB](\d{4})", RegexOptions.CultureInvariant)]
    private static partial Regex AdataPattern();

    [GeneratedRegex(@"^M[34]\d{2}([AR])[0-9A-Z]+-C([A-Z]{2})", RegexOptions.CultureInvariant)]
    private static partial Regex SamsungPattern();

    [GeneratedRegex(@"^HMA[0-9A-Z]+-([A-Z]{2})", RegexOptions.CultureInvariant)]
    private static partial Regex SkHynixPattern();

    [GeneratedRegex(@"^MTA[0-9A-Z]+-(\d)G(\d)", RegexOptions.CultureInvariant)]
    private static partial Regex MicronDdr4Pattern();

    [GeneratedRegex(@"^MTC[0-9A-Z]+?[US]C(\d{2})B", RegexOptions.CultureInvariant)]
    private static partial Regex MicronDdr5Pattern();
}
