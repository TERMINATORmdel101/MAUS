using System.Text.RegularExpressions;

namespace Maus.Core.Hardware;

public static partial class CpuClassifier
{
    public static HardwareVendor VendorOf(string? manufacturer, string? name)
    {
        var text = $"{manufacturer} {name}";
        if (text.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return HardwareVendor.Intel;
        }

        return text.Contains("AMD", StringComparison.OrdinalIgnoreCase) || text.Contains("Ryzen", StringComparison.OrdinalIgnoreCase)
            ? HardwareVendor.Amd
            : HardwareVendor.Other;
    }

    public static bool IsX3D(string? name) => name is not null && X3DPattern().IsMatch(name);

    /// <summary>7900X3D, 7950X3D, 9900X3D, 9950X3D. Le 9950X3D2 porte du V-Cache sur ses deux CCD et n'est pas concerné.</summary>
    public static bool IsAsymmetricDualCcdX3D(string? name) =>
        name is not null && AsymmetricDualCcdX3DPattern().IsMatch(name);

    /// <summary>Core i5, i7 ou i9 13xxx ou 14xxx de bureau (suffixes K, KF, KS, F, T ou aucun). Les puces mobiles (H, HX, U, P) sont exclues.</summary>
    public static bool IsIntelRaptorLakeDesktop(string? name) =>
        name is not null && RaptorLakeDesktopPattern().IsMatch(name);

    [GeneratedRegex(@"\b\d{4}X3D\d?\b", RegexOptions.IgnoreCase)]
    private static partial Regex X3DPattern();

    [GeneratedRegex(@"\b(7900|7950|9900|9950)X3D\b(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex AsymmetricDualCcdX3DPattern();

    [GeneratedRegex(@"\bi[579]-1[34]\d{3}(K|KF|KS|F|T)?\b(?![A-Z])", RegexOptions.IgnoreCase)]
    private static partial Regex RaptorLakeDesktopPattern();
}

public static class GpuClassifier
{
    public static HardwareVendor VendorOf(string? pnpDeviceId, string? name)
    {
        var id = pnpDeviceId ?? string.Empty;
        if (id.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase))
        {
            return HardwareVendor.Nvidia;
        }

        if (id.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase))
        {
            return HardwareVendor.Amd;
        }

        if (id.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase))
        {
            return HardwareVendor.Intel;
        }

        var text = name ?? string.Empty;
        if (text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || text.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
        {
            return HardwareVendor.Nvidia;
        }

        if (text.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || text.Contains("AMD", StringComparison.OrdinalIgnoreCase))
        {
            return HardwareVendor.Amd;
        }

        return text.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? HardwareVendor.Intel : HardwareVendor.Other;
    }

    /// <summary>
    /// Estimation par le nom : iGPU Intel (sauf Arc A et B dédiées), « Radeon Graphics » sans numéro de modèle chez AMD.
    /// Le Module 14 confirme par le drapeau UMA de Direct3D.
    /// </summary>
    public static bool IsLikelyIntegrated(HardwareVendor vendor, string? name)
    {
        var text = name ?? string.Empty;
        return vendor switch
        {
            HardwareVendor.Nvidia => false,
            HardwareVendor.Intel => !Regex.IsMatch(text, @"Arc(\(TM\))?\s+[AB]\d{3}", RegexOptions.IgnoreCase),
            HardwareVendor.Amd => !Regex.IsMatch(text, @"\b(RX|R[579]|Pro\s+W)\s*\d", RegexOptions.IgnoreCase),
            _ => false,
        };
    }
}

public static class FormFactorClassifier
{
    /// <summary>Types de châssis SMBIOS correspondant à un portable ou une tablette.</summary>
    private static readonly HashSet<long> PortableChassis = [8, 9, 10, 11, 14, 30, 31, 32];

    /// <summary>Portable si au moins deux indices sur trois concordent : châssis, <c>PCSystemTypeEx</c>, batterie.</summary>
    public static FormFactor Classify(IReadOnlyList<long> chassisTypes, long? pcSystemTypeEx, bool hasBattery)
    {
        var chassisPortable = chassisTypes.Any(PortableChassis.Contains);
        var systemTypePortable = pcSystemTypeEx is 2 or 8;
        var votes = (chassisPortable ? 1 : 0) + (systemTypePortable ? 1 : 0) + (hasBattery ? 1 : 0);

        if (votes >= 2)
        {
            return FormFactor.Laptop;
        }

        return chassisTypes.Count > 0 || pcSystemTypeEx is not null ? FormFactor.Desktop : FormFactor.Unknown;
    }
}
