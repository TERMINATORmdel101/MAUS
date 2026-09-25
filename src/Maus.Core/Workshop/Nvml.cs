using System.Runtime.InteropServices;
using System.Text;

namespace Maus.Core.Workshop;

/// <summary>État d'une carte NVIDIA lu par NVML. Chaque valeur est facultative : une carte ou un pilote ancien peut ne pas la fournir.</summary>
public sealed record NvidiaGpuState
{
    public required string Name { get; init; }

    public string? VbiosVersion { get; init; }

    public int? TemperatureC { get; init; }

    /// <summary>Seuil où la carte ralentit pour se protéger (donné par la carte elle-même).</summary>
    public int? SlowdownTemperatureC { get; init; }

    /// <summary>Seuil où la carte s'éteint pour se protéger.</summary>
    public int? ShutdownTemperatureC { get; init; }

    /// <summary>Température maximale de fonctionnement annoncée pour le GPU.</summary>
    public int? MaxOperatingTemperatureC { get; init; }

    public double? PowerWatts { get; init; }

    public double? PowerLimitWatts { get; init; }

    public double? DefaultPowerLimitWatts { get; init; }

    public double? MaxPowerLimitWatts { get; init; }

    public int? GraphicsClockMhz { get; init; }

    public int? MaxGraphicsClockMhz { get; init; }

    public int? MemoryClockMhz { get; init; }

    public int? MaxMemoryClockMhz { get; init; }

    public int? FanPercent { get; init; }

    public int? UtilizationPercent { get; init; }

    public long? MemoryTotalBytes { get; init; }

    public long? MemoryUsedBytes { get; init; }

    public int? PcieGeneration { get; init; }

    public int? PcieMaxGeneration { get; init; }

    public int? PcieWidth { get; init; }

    public int? PcieMaxWidth { get; init; }
}

/// <summary>Accès en lecture à NVML (NVIDIA Management Library, API publique et documentée par NVIDIA).</summary>
public interface INvmlSource
{
    /// <summary>État de chaque carte NVIDIA, ou <c>null</c> si NVML est absente (pas de carte NVIDIA, pilote ancien).</summary>
    IReadOnlyList<NvidiaGpuState>? Read();
}

/// <summary>
/// NVML chargée dynamiquement (<c>nvml.dll</c> du pilote, dans System32 ou le dossier NVSMI) : MAUS ne l'installe jamais.
/// Seules des fonctions de lecture sont appelées.
/// </summary>
public sealed unsafe class WindowsNvmlSource : INvmlSource
{
    private const int Success = 0;
    private static readonly Lock Gate = new();

    public IReadOnlyList<NvidiaGpuState>? Read()
    {
        lock (Gate)
        {
            if (!TryLoad(out var library))
            {
                return null;
            }

            var api = new Api(library);
            if (api.Init is null || api.Init() != Success)
            {
                return null;
            }

            try
            {
                uint count = 0;
                if (api.GetCount is null || api.GetCount(&count) != Success)
                {
                    return null;
                }

                var gpus = new List<NvidiaGpuState>();
                for (uint index = 0; index < count; index++)
                {
                    nint device = 0;
                    if (api.GetHandle(index, &device) == Success)
                    {
                        gpus.Add(ReadDevice(api, device));
                    }
                }

                return gpus;
            }
            finally
            {
                if (api.Shutdown is not null)
                {
                    _ = api.Shutdown();
                }
            }
        }
    }

    private static NvidiaGpuState ReadDevice(Api api, nint device)
    {
        int? U(delegate* unmanaged<nint, uint*, int> f)
        {
            uint value = 0;
            return f is not null && f(device, &value) == Success ? (int)value : null;
        }

        int? Typed(delegate* unmanaged<nint, int, uint*, int> f, int type)
        {
            uint value = 0;
            return f is not null && f(device, type, &value) == Success ? (int)value : null;
        }

        double? Watts(int? milliwatts) => milliwatts is { } mw ? mw / 1000.0 : null;

        uint min = 0, max = 0;
        var constraints = api.PowerConstraints is not null && api.PowerConstraints(device, &min, &max) == Success;
        Utilization utilization = default;
        var hasUtilization = api.GetUtilization is not null && api.GetUtilization(device, &utilization) == Success;
        Memory memory = default;
        var hasMemory = api.GetMemoryInfo is not null && api.GetMemoryInfo(device, &memory) == Success;

        return new NvidiaGpuState
        {
            Name = Text(api.Name, device, 96) ?? "NVIDIA",
            VbiosVersion = Text(api.Vbios, device, 32),
            TemperatureC = Typed(api.Temperature, 0),
            ShutdownTemperatureC = Typed(api.TemperatureThreshold, 0),
            SlowdownTemperatureC = Typed(api.TemperatureThreshold, 1),
            MaxOperatingTemperatureC = Typed(api.TemperatureThreshold, 3),
            PowerWatts = Watts(U(api.PowerUsage)),
            PowerLimitWatts = Watts(U(api.EnforcedPowerLimit)),
            DefaultPowerLimitWatts = Watts(U(api.DefaultPowerLimit)),
            MaxPowerLimitWatts = constraints ? max / 1000.0 : null,
            GraphicsClockMhz = Typed(api.Clock, 0),
            MaxGraphicsClockMhz = Typed(api.MaxClock, 0),
            MemoryClockMhz = Typed(api.Clock, 2),
            MaxMemoryClockMhz = Typed(api.MaxClock, 2),
            FanPercent = U(api.FanSpeed),
            UtilizationPercent = hasUtilization ? (int)utilization.Gpu : null,
            MemoryTotalBytes = hasMemory ? (long)memory.Total : null,
            MemoryUsedBytes = hasMemory ? (long)memory.Used : null,
            PcieGeneration = U(api.PcieGeneration),
            PcieMaxGeneration = U(api.PcieMaxGeneration),
            PcieWidth = U(api.PcieWidth),
            PcieMaxWidth = U(api.PcieMaxWidth),
        };
    }

    private static string? Text(delegate* unmanaged<nint, byte*, uint, int> f, nint device, int length)
    {
        if (f is null)
        {
            return null;
        }

        var buffer = stackalloc byte[length];
        if (f(device, buffer, (uint)length) != Success)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(buffer, length).TrimEnd('\0');
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        return end >= 0 ? text[..end] : text;
    }

    private static bool TryLoad(out nint library)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var path in new[] { Path.Combine(system, "nvml.dll"), Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvml.dll") })
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out library))
            {
                return true;
            }
        }

        library = 0;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Memory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    /// <summary>Points d'entrée NVML (null si absents de cette version de la bibliothèque).</summary>
    private sealed class Api(nint library)
    {
        public readonly delegate* unmanaged<int> Init = (delegate* unmanaged<int>)Export(library, "nvmlInit_v2");
        public readonly delegate* unmanaged<int> Shutdown = (delegate* unmanaged<int>)Export(library, "nvmlShutdown");
        public readonly delegate* unmanaged<uint*, int> GetCount = (delegate* unmanaged<uint*, int>)Export(library, "nvmlDeviceGetCount_v2");
        public readonly delegate* unmanaged<uint, nint*, int> GetHandle = (delegate* unmanaged<uint, nint*, int>)Export(library, "nvmlDeviceGetHandleByIndex_v2");
        public readonly delegate* unmanaged<nint, byte*, uint, int> Name = (delegate* unmanaged<nint, byte*, uint, int>)Export(library, "nvmlDeviceGetName");
        public readonly delegate* unmanaged<nint, byte*, uint, int> Vbios = (delegate* unmanaged<nint, byte*, uint, int>)Export(library, "nvmlDeviceGetVbiosVersion");
        public readonly delegate* unmanaged<nint, int, uint*, int> Temperature = (delegate* unmanaged<nint, int, uint*, int>)Export(library, "nvmlDeviceGetTemperature");
        public readonly delegate* unmanaged<nint, int, uint*, int> TemperatureThreshold = (delegate* unmanaged<nint, int, uint*, int>)Export(library, "nvmlDeviceGetTemperatureThreshold");
        public readonly delegate* unmanaged<nint, uint*, int> PowerUsage = (delegate* unmanaged<nint, uint*, int>)Export(library, "nvmlDeviceGetPowerUsage");
        public readonly delegate* unmanaged<nint, uint*, int> EnforcedPowerLimit = (delegate* unmanaged<nint, uint*, int>)Export(library, "nvmlDeviceGetEnforcedPowerLimit");
        public readonly delegate* unmanaged<nint, uint*, int> DefaultPowerLimit = (delegate* unmanaged<nint, uint*, int>)Export(library, "nvmlDeviceGetPowerManagementDefaultLimit");
        public readonly delegate* unmanaged<nint, uint*, uint*, int> PowerConstraints = (delegate* unmanaged<nint, uint*, uint*, int>)Export(library, "nvmlDeviceGetPowerManagementLimitConstraints");
        public readonly delegate* unmanaged<nint, int, uint*, int> Clock = (delegate* unmanaged<nint, int, uint*, int>)Export(library, "nvmlDeviceGetClockInfo");
        public readonly delegate* unmanaged<nint, int, uint*, int> MaxClock = (delegate* unmanaged<nint, int, uint*, int>)Export(library, "nvmlDeviceGetMaxClockInfo");
        public readonly delegate* unmanaged<nint, uint*, int> FanSpeed = (delegate* unmanaged<nint, uint*, int>)Export(library, "nvmlDeviceGetFanSpeed");
        public readonly delegate* unmanaged<nint, Utilization*, int> GetUtilization = (delegate* unmanaged<nint, Utilization*, int>)Export(library, "nvmlDeviceGetUtilizationRates");
        public readonly delegate* unmanaged<nint, Memory*, int> GetMemoryInfo = (delegate* unmanaged<nint, Memory*, int>)Export(library, "nvmlDeviceGetMemoryInfo");
        public readonly delegate* unmanaged<nint, uint*, int> PcieGeneration = (delegate* unmanaged<nint, uint*, int>)Export(library, "nvmlDeviceGetCurrPcieLinkGeneration");
        public readonly delegate* unmanaged<nint, uint*, int> PcieMaxGeneration = (delegate* unmanaged<nint, uint*, int>)Export(library, "nvmlDeviceGetMaxPcieLinkGeneration");
        public readonly delegate* unmanaged<nint, uint*, int> PcieWidth = (delegate* unmanaged<nint, uint*, int>)Export(library, "nvmlDeviceGetCurrPcieLinkWidth");
        public readonly delegate* unmanaged<nint, uint*, int> PcieMaxWidth = (delegate* unmanaged<nint, uint*, int>)Export(library, "nvmlDeviceGetMaxPcieLinkWidth");

        private static nint Export(nint library, string name) => NativeLibrary.TryGetExport(library, name, out var address) ? address : 0;
    }
}
