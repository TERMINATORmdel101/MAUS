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

    /// <summary>
    /// 7900X3D, 7950X3D, 9900X3D, 9950X3D, et le portable 9955HX3D : 16 cœurs et 128 Mo de cache L3 comme le 9950X3D (fiche AMD du
    /// 9955HX3D), donc un seul étage de V-Cache sur ses deux CCD. Le 9950X3D2 en porte sur les deux CCD et n'est pas concerné.
    /// Le portable 7945HX3D aussi : deux CCD, 128 Mo de L3, les 64 Mo de V-Cache sur un seul CCD (fiche Notebookcheck du processeur).
    /// </summary>
    public static bool IsAsymmetricDualCcdX3D(string? name) =>
        name is not null && AsymmetricDualCcdX3DPattern().IsMatch(name);

    /// <summary>Core i5, i7 ou i9 13xxx ou 14xxx de bureau (suffixes K, KF, KS, F, T ou aucun). Les puces mobiles (H, HX, U, P) sont exclues.</summary>
    public static bool IsIntelRaptorLakeDesktop(string? name) =>
        name is not null && RaptorLakeDesktopPattern().IsMatch(name);

    [GeneratedRegex(@"\b(7900X3D|7950X3D|9900X3D|9950X3D|7945HX3D|9955HX3D)\b", RegexOptions.IgnoreCase)]
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
            HardwareVendor.Intel => !Regex.IsMatch(text, @"Arc(\(TM\))?\s+(Pro\s+)?[AB]\d{2,3}", RegexOptions.IgnoreCase),
            HardwareVendor.Amd => !Regex.IsMatch(text, @"\b(RX|R[579]|Pro\s+WX?)\s*\d|\bRX\s+Vega\s+(56|64)\b", RegexOptions.IgnoreCase),
            _ => false,
        };
    }
}

public static class VirtualMachineClassifier
{
    /// <summary>
    /// D'après <c>Win32_ComputerSystem</c> : « Microsoft Corporation / Virtual Machine » (Hyper-V), VMware, VirtualBox (« innotek GmbH »), QEMU, KVM.
    /// Pas le bit « hyperviseur » de CPUID : il est aussi levé sur un vrai PC dès que Hyper-V ou la sécurité par virtualisation tourne.
    /// </summary>
    public static bool IsVirtual(string? manufacturer, string? model)
    {
        var maker = manufacturer ?? string.Empty;
        var product = model ?? string.Empty;
        return (maker.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) && product.Contains("Virtual Machine", StringComparison.OrdinalIgnoreCase))
            || maker.Contains("innotek", StringComparison.OrdinalIgnoreCase)
            || VirtualPlatforms.Any(name =>
                maker.Contains(name, StringComparison.OrdinalIgnoreCase) || product.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] VirtualPlatforms = ["VMware", "VirtualBox", "QEMU", "KVM"];
}

public static class FormFactorClassifier
{
    /// <summary>Types de châssis SMBIOS correspondant à un portable ou une tablette.</summary>
    private static readonly HashSet<long> PortableChassis = [8, 9, 10, 11, 14, 30, 31, 32];

    /// <summary>Portable si au moins deux indices sur trois concordent : châssis, <c>PCSystemTypeEx</c>, batterie.</summary>
    public static FormFactor Classify(IReadOnlyList<long> chassisTypes, long? pcSystemTypeEx, bool hasBattery)
    {
        ArgumentNullException.ThrowIfNull(chassisTypes);

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
