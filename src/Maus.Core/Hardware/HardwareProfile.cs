namespace Maus.Core.Hardware;

public enum FormFactor
{
    Unknown,
    Desktop,
    Laptop,
}

public enum HardwareVendor
{
    Other,
    Intel,
    Amd,
    Nvidia,
}

public sealed record CpuInfo(
    string Name,
    HardwareVendor Vendor,
    int Cores,
    int LogicalProcessors,
    int MaxClockMhz)
{
    public static CpuInfo Unknown { get; } = new("Inconnu", HardwareVendor.Other, 0, 0, 0);

    public bool IsX3D => CpuClassifier.IsX3D(Name);

    /// <summary>Ryzen X3D à deux CCD dont un seul porte le V-Cache (placement des jeux par la Game Bar, voir Modules 5 et 7).</summary>
    public bool IsAsymmetricDualCcdX3D => CpuClassifier.IsAsymmetricDualCcdX3D(Name);

    /// <summary>Core i5, i7 ou i9 de bureau de 13e ou 14e génération (microcode 0x12F requis, voir Module 8).</summary>
    public bool IsIntelRaptorLakeDesktop => CpuClassifier.IsIntelRaptorLakeDesktop(Name);
}

public sealed record GpuInfo(
    string Name,
    HardwareVendor Vendor,
    string? DriverVersion,
    DateTime? DriverDate,
    string PnpDeviceId,
    bool IsIntegrated);

/// <summary>Profil matériel commun, calculé une fois et partagé par tous les modules.</summary>
public sealed record HardwareProfile
{
    public FormFactor FormFactor { get; init; }

    public bool HasBattery { get; init; }

    public string Manufacturer { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string BoardManufacturer { get; init; } = string.Empty;

    public string BoardProduct { get; init; } = string.Empty;

    public CpuInfo Cpu { get; init; } = CpuInfo.Unknown;

    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];

    /// <summary>PC joint à un domaine ou inscrit dans une gestion MDM : les corrections y sont bloquées.</summary>
    public bool IsManaged { get; init; }

    public bool IsLaptop => FormFactor == FormFactor.Laptop;

    public bool HasDedicatedGpu => Gpus.Any(g => !g.IsIntegrated);
}
