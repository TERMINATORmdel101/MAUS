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

    public double? PowerWatts { get; init; }

    public double? PowerLimitWatts { get; init; }

    public double? DefaultPowerLimitWatts { get; init; }

    public double? MaxPowerLimitWatts { get; init; }

    public int? GraphicsClockMhz { get; init; }

    public int? MaxGraphicsClockMhz { get; init; }

    public int? MaxMemoryClockMhz { get; init; }

    public int? FanPercent { get; init; }

    public int? UtilizationPercent { get; init; }

    public long? MemoryTotalBytes { get; init; }

    public long? MemoryUsedBytes { get; init; }

    public int? PcieGeneration { get; init; }

    public int? PcieMaxGeneration { get; init; }

    public int? PcieWidth { get; init; }

    public int? PcieMaxWidth { get; init; }

    /// <summary>
    /// Raisons qui limitent la fréquence en ce moment : masque de bits <c>nvmlClocksEventReason*</c> de nvml.h (NVIDIA), voir
    /// <see cref="Benchmark.NvidiaClockReasons"/>.
    /// </summary>
    public ulong? ClockEventReasons { get; init; }
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
    internal const int Success = 0;
    internal static readonly Lock Gate = new();

    public IReadOnlyList<NvidiaGpuState>? Read()
    {
        lock (Gate)
        {
            if (Api.Load() is not { } api || api.Init is null || api.Init() != Success)
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
        ulong reasons = 0;
        var hasReasons = api.ClockEvents is not null && api.ClockEvents(device, &reasons) == Success;

        return new NvidiaGpuState
        {
            Name = CardName(api, device),
            VbiosVersion = Text(api.Vbios, device, 32),
            TemperatureC = Typed(api.Temperature, 0),
            ShutdownTemperatureC = Typed(api.TemperatureThreshold, 0),
            SlowdownTemperatureC = Typed(api.TemperatureThreshold, 1),
            PowerWatts = Watts(U(api.PowerUsage)),
            PowerLimitWatts = Watts(U(api.EnforcedPowerLimit)),
            DefaultPowerLimitWatts = Watts(U(api.DefaultPowerLimit)),
            MaxPowerLimitWatts = constraints ? max / 1000.0 : null,
            GraphicsClockMhz = Typed(api.Clock, 0),
            MaxGraphicsClockMhz = Typed(api.MaxClock, 0),
            MaxMemoryClockMhz = Typed(api.MaxClock, 2),
            FanPercent = U(api.FanSpeed),
            UtilizationPercent = hasUtilization ? (int)utilization.Gpu : null,
            MemoryTotalBytes = hasMemory ? (long)memory.Total : null,
            MemoryUsedBytes = hasMemory ? (long)memory.Used : null,
            PcieGeneration = U(api.PcieGeneration),
            PcieMaxGeneration = U(api.PcieMaxGeneration),
            PcieWidth = U(api.PcieWidth),
            PcieMaxWidth = U(api.PcieMaxWidth),
            ClockEventReasons = hasReasons ? reasons : null,
        };
    }

    internal static string CardName(Api api, nint device) => Text(api.Name, device, 96) ?? "NVIDIA";

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
    internal struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Memory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    /// <summary>Points d'entrée NVML (null si absents de cette version de la bibliothèque).</summary>
    internal sealed class Api(nint library)
    {
        private static Api? s_loaded;

        /// <summary>Bibliothèque chargée une fois pour toutes, puisqu'elle n'est jamais libérée ; à appeler sous <see cref="Gate"/>.</summary>
        public static Api? Load()
        {
            if (s_loaded is null && TryLoad(out var library))
            {
                s_loaded = new Api(library);
            }

            return s_loaded;
        }

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

        // Nom actuel depuis le pilote R535 ; l'ancien (« Throttle »), marqué obsolète, reste lu sur les pilotes plus anciens.
        public readonly delegate* unmanaged<nint, ulong*, int> ClockEvents = (delegate* unmanaged<nint, ulong*, int>)Export(library, "nvmlDeviceGetCurrentClocksEventReasons", "nvmlDeviceGetCurrentClocksThrottleReasons");

        private static nint Export(nint library, string name) => NativeLibrary.TryGetExport(library, name, out var address) ? address : 0;

        private static nint Export(nint library, string name, string older) => Export(library, name) is var address and not 0 ? address : Export(library, older);
    }
}

/// <summary>
/// NVML pour les mesures en direct (une par seconde) : la bibliothèque reste ouverte tant que la source vit, ce qui ne change
/// jamais sur une carte n'est lu qu'une fois, et seules les valeurs affichées sont relues. La fiche complète de l'inventaire
/// reste lue par <see cref="WindowsNvmlSource"/>.
/// </summary>
public sealed unsafe class LiveNvmlSource : INvmlSource, IDisposable
{
    /// <summary>Codes de nvml.h qui disent que l'ouverture ne vaut plus : non initialisée, handle refusé, pilote absent, carte perdue, à réinitialiser, pilote changé.</summary>
    private static readonly int[] OpeningLost = [1, 2, 9, 15, 16, 18];

    private WindowsNvmlSource.Api? _api;
    private List<Card> _cards = [];
    private bool _disposed;

    public IReadOnlyList<NvidiaGpuState>? Read()
    {
        lock (WindowsNvmlSource.Gate)
        {
            if (_disposed || !Open())
            {
                return null;
            }

            var states = ReadCards(out var lost);
            if (!lost)
            {
                return states;
            }

            // Pilote remis à jour ou carte relancée : on rouvre une fois, et la mesure suivante réessaiera au besoin.
            Close();
            return Open() ? ReadCards(out _) : null;
        }
    }

    public void Dispose()
    {
        lock (WindowsNvmlSource.Gate)
        {
            _disposed = true;
            Close();
        }
    }

    /// <summary>L'ouverture ne vaut plus d'après ce code de retour de NVML.</summary>
    internal static bool LosesOpening(int status) => OpeningLost.Contains(status);

    private bool Open()
    {
        if (_api is not null)
        {
            return true;
        }

        if (WindowsNvmlSource.Api.Load() is not { } api || api.Init is null || api.Init() != WindowsNvmlSource.Success)
        {
            return false;
        }

        uint count = 0;
        if (api.GetCount is null || api.GetHandle is null || api.GetCount(&count) != WindowsNvmlSource.Success)
        {
            if (api.Shutdown is not null)
            {
                _ = api.Shutdown();
            }

            return false;
        }

        var cards = new List<Card>();
        for (uint index = 0; index < count; index++)
        {
            nint device = 0;
            if (api.GetHandle(index, &device) == WindowsNvmlSource.Success)
            {
                cards.Add(new Card(device, WindowsNvmlSource.CardName(api, device)));
            }
        }

        _api = api;
        _cards = cards;
        return true;
    }

    private void Close()
    {
        if (_api is { } api && api.Shutdown is not null)
        {
            _ = api.Shutdown();
        }

        _api = null;
        _cards = [];
    }

    private List<NvidiaGpuState>? ReadCards(out bool lost)
    {
        var api = _api!;
        var states = new List<NvidiaGpuState>(_cards.Count);
        lost = false;
        foreach (var card in _cards)
        {
            var device = card.Device;
            uint value = 0;
            int? temperature = null;
            if (api.Temperature is not null)
            {
                var read = api.Temperature(device, 0, &value);
                if (read == WindowsNvmlSource.Success)
                {
                    temperature = (int)value;
                }
                else if (LosesOpening(read))
                {
                    lost = true;
                    return null;
                }
            }

            if (card.SlowdownC is null && api.TemperatureThreshold is not null && api.TemperatureThreshold(device, 1, &value) == WindowsNvmlSource.Success)
            {
                card.SlowdownC = (int)value;
            }

            WindowsNvmlSource.Utilization utilization = default;
            var hasUtilization = api.GetUtilization is not null && api.GetUtilization(device, &utilization) == WindowsNvmlSource.Success;
            WindowsNvmlSource.Memory memory = default;
            var hasMemory = api.GetMemoryInfo is not null && api.GetMemoryInfo(device, &memory) == WindowsNvmlSource.Success;
            double? watts = api.PowerUsage is not null && api.PowerUsage(device, &value) == WindowsNvmlSource.Success ? value / 1000.0 : null;
            int? clock = api.Clock is not null && api.Clock(device, 0, &value) == WindowsNvmlSource.Success ? (int)value : null;
            int? fan = api.FanSpeed is not null && api.FanSpeed(device, &value) == WindowsNvmlSource.Success ? (int)value : null;
            states.Add(new NvidiaGpuState
            {
                Name = card.Name,
                TemperatureC = temperature,
                SlowdownTemperatureC = card.SlowdownC,
                PowerWatts = watts,
                GraphicsClockMhz = clock,
                FanPercent = fan,
                UtilizationPercent = hasUtilization ? (int)utilization.Gpu : null,
                MemoryUsedBytes = hasMemory ? (long)memory.Used : null,
            });
        }

        return states;
    }

    /// <summary>Une carte ouverte et ce qui ne change pas sur elle.</summary>
    private sealed class Card(nint device, string name)
    {
        public nint Device { get; } = device;

        public string Name { get; } = name;

        public int? SlowdownC { get; set; }
    }
}
