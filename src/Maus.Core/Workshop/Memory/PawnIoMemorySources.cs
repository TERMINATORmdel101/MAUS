using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.PawnIo;
using RAMSPDToolkit.I2CSMBus;
using RAMSPDToolkit.SPD;
using Maus.Core.Workshop.Memory.PawnIo;
using RAMSPDToolkit.SPD.Interop.Shared;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop.Memory;

/// <summary>
/// Lecture SPD par RAMSPDToolkit (MPL-2.0) sur le bus SMBus, via le pilote PawnIO, pendant qu'un « Computer »
/// LibreHardwareMonitor tient le pilote ouvert. Seules des lectures, plus le choix de la page SPD (registre prévu pour cela).
/// </summary>
public sealed class PawnIoSpdSource : ISpdSource
{
    /// <summary>Verrou partagé par les outils de surveillance pour ne pas se marcher dessus sur le bus SMBus.</summary>
    private const string SmbusMutexName = @"Global\Access_SMBUS.HTP.Method";

    public IReadOnlyList<SpdImage> ReadAll()
    {
        var computer = new Computer { IsMemoryEnabled = true };
        computer.Open();
        using var smbus = OpenMutex(SmbusMutexName);
        var locked = smbus?.WaitOne(TimeSpan.FromSeconds(5)) ?? false;
        try
        {
            if (SMBusManager.RegisteredSMBuses.Count == 0)
            {
                SMBusManager.DetectSMBuses();
            }

            var images = new List<SpdImage>();
            foreach (var bus in SMBusManager.RegisteredSMBuses)
            {
                for (var address = SPDConstants.SPD_BEGIN; address <= SPDConstants.SPD_END; address++)
                {
                    if (new SPDDetector(bus, address).Accessor is not { } accessor)
                    {
                        continue;
                    }

                    var length = accessor.MemoryType() is SPDMemoryType.SPD_DDR5_SDRAM or SPDMemoryType.SPD_LPDDR5_SDRAM ? 1024 : 512;
                    var bytes = new byte[length];
                    for (var i = 0; i < length; i++)
                    {
                        bytes[i] = accessor.At((ushort)i);
                    }

                    images.Add(new SpdImage(accessor.Index, bytes, Safe(accessor.GetModuleManufacturerString), Safe(accessor.GetDRAMManufacturerString)));
                }
            }

            return images;
        }
        finally
        {
            if (locked)
            {
                smbus!.ReleaseMutex();
            }

            computer.Close();
        }
    }

    internal static Mutex? OpenMutex(string name)
    {
        try
        {
            return new Mutex(false, name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return null;
        }
    }

    private static string? Safe(Func<string> read)
    {
        try
        {
            return read() is { Length: > 0 } text ? text.Trim() : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}

/// <summary>Registres SMN des processeurs AMD par le module PawnIO « AMDFamily17 », sous le verrou PCI commun aux outils.</summary>
public sealed class PawnIoSmnReader : ISmnReader, IDisposable
{
    private const string PciMutexName = @"Global\Access_PCI";
    private readonly AmdFamily17 _module = new();
    private readonly Mutex? _pci = PawnIoSpdSource.OpenMutex(PciMutexName);

    public uint Read(uint address)
    {
        var locked = _pci?.WaitOne(TimeSpan.FromSeconds(5)) ?? false;
        try
        {
            return _module.ReadSmn(address);
        }
        finally
        {
            if (locked)
            {
                _pci!.ReleaseMutex();
            }
        }
    }

    public void Dispose()
    {
        _module.Close();
        _pci?.Dispose();
    }
}

/// <summary>Table PM du SMU AMD par le module PawnIO « RyzenSMU ».</summary>
public sealed class PawnIoPmTableReader : IPmTableReader, IDisposable
{
    private readonly RyzenSmu _smu = new();

    public uint? Version()
    {
        _smu.ResolvePmTable(out var version, out _);
        return version == 0 ? null : version;
    }

    public byte[] Read(int size)
    {
        _smu.UpdatePmTable();
        Thread.Sleep(50);
        var words = _smu.ReadPmTable((size + 7) / 8);
        var bytes = new byte[words.Length * 8];
        Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public void Dispose() => _smu.Close();
}

/// <summary>
/// Lecture réelle, sur Windows avec PawnIO : SPD, puis registres et table PM si le processeur est un AMD Ryzen,
/// ou registres du contrôleur mémoire (modules officiels IntelMCHBAR et IntelMSR, lecture seule) s'il est Intel.
/// </summary>
public static class PawnIoMemoryDetails
{
    public static MemoryDetailReport Read(CpuIdInfo? cpu, bool? ddr5Hint)
    {
        if (cpu is { IsIntel: true })
        {
            var intel = new IntelControllerAccess(cpu.Family, cpu.Model, () => new PawnIoMchbarReader(), () => new PawnIoMsrReader());
            return MemoryDetails.Read(new PawnIoSpdSource(), null, null, ddr5Hint, intel);
        }

        if (cpu is not { IsAmd: true, Family: >= 0x17 })
        {
            return MemoryDetails.Read(new PawnIoSpdSource(), null, null, ddr5Hint);
        }

        using var smn = new PawnIoSmnReader();
        using var pm = new PawnIoPmTableReader();
        return MemoryDetails.Read(new PawnIoSpdSource(), smn, pm, ddr5Hint);
    }

    /// <summary>Message si PawnIO manque : la fiche détaillée en a besoin.</summary>
    public static string DriverRequired => T("La fiche mémoire détaillée lit la puce SPD des barrettes et les registres du contrôleur mémoire : il faut le pilote PawnIO (Atelier, En direct) et MAUS en administrateur.");
}
