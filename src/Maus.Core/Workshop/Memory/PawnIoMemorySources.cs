using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.PawnIo;
using RAMSPDToolkit.I2CSMBus;
using RAMSPDToolkit.SPD;
using Maus.Core.Workshop.Memory.PawnIo;
using RAMSPDToolkit.SPD.Interop.Shared;
using RAMSPDToolkit.Windows.Driver;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop.Memory;

/// <summary>Bus SMBus indisponible : un autre programme le garde, ou une lecture est déjà en cours.</summary>
public sealed class SmbusBusyException(string message) : Exception(message);

/// <summary>
/// Lecture SPD par RAMSPDToolkit (MPL-2.0) sur le bus SMBus, via le pilote PawnIO, pendant qu'un « Computer »
/// LibreHardwareMonitor tient le pilote ouvert. Seules des lectures, plus le choix de la page SPD (registre prévu pour cela).
/// </summary>
public sealed class PawnIoSpdSource : ISpdSource
{
    /// <summary>Verrou partagé par les outils de surveillance pour ne pas se marcher dessus sur le bus SMBus.</summary>
    private const string SmbusMutexName = @"Global\Access_SMBUS.HTP.Method";

    /// <summary>Attente maximale du bus : au-delà, un autre programme le garde et la lecture ramperait (2 s par octet).</summary>
    private static readonly TimeSpan BusWait = TimeSpan.FromSeconds(10);

    /// <summary>Attente maximale d'une lecture des puces (celle qu'on lance ou celle déjà en cours).</summary>
    private static readonly TimeSpan ReadWait = TimeSpan.FromSeconds(75);

    private static readonly Lock ReadGate = new();

    /// <summary>
    /// Lecture partagée : « Mon PC » et « Mémoire » attendent la même, et le résultat sert jusqu'à la fermeture de MAUS
    /// (les barrettes ne changent pas sans redémarrer le PC). Une lecture qui a échoué est refaite à la demande suivante.
    /// </summary>
    private static Task<List<SpdImage>>? s_read;

    public IReadOnlyList<SpdImage> ReadAll()
    {
        Task<List<SpdImage>> read;
        lock (ReadGate)
        {
            if (s_read is null || s_read.IsFaulted || s_read.IsCanceled)
            {
                s_read = Task.Run(ReadLocked);
            }

            read = s_read;
        }

        try
        {
            if (!read.Wait(ReadWait))
            {
                throw new SmbusBusyException(T("La lecture des puces des barrettes ne se termine pas : le bus SMBus est très lent ou bloqué par un autre programme (une autre fenêtre de MAUS, HWiNFO, CPU-Z, un logiciel d'éclairage RGB ou de la carte mère…)."));
            }
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }

        return read.Result;
    }

    private static List<SpdImage> ReadLocked()
    {
        // Le verrou du bus d'abord : la bibliothèque de lecture le reprend à chaque octet, sans l'attendre plus de 2 secondes ;
        // le tenir nous-mêmes pendant toute la lecture évite qu'elle rampe si un autre programme s'en sert.
        using var smbus = OpenMutex(SmbusMutexName);
        var locked = TryAcquire(smbus, BusWait);
        if (smbus is not null && !locked)
        {
            throw new SmbusBusyException(Platform.SingleInstance.OtherInterfaceProcesses() > 0
                ? T("Le bus des barrettes est gardé par un autre MAUS qui tourne encore, peut-être sans fenêtre visible (une ancienne version restée ouverte). Fermez-le : Gestionnaire des tâches, onglet Détails, « MAUS.exe », Fin de tâche ; puis relancez la lecture.")
                : T("Le bus des barrettes est occupé par un autre programme (une autre fenêtre de MAUS, HWiNFO, CPU-Z, un logiciel d'éclairage RGB ou de la carte mère…). Fermez-le, puis relancez la lecture."));
        }

        // Horloge système à 1 ms pendant la lecture : la bibliothèque attend chaque échange par pauses de 0,25 ms, que
        // Windows arrondit sinon à 15,6 ms (lecture 10 à 15 fois plus lente).
        using var fineTimer = Platform.TimerResolution.OneMillisecond();
        try
        {
            Step("pilote SMBus : chargement");
            EnsureDriver();
            if (SMBusManager.RegisteredSMBuses.Count == 0)
            {
                Step("bus SMBus : détection");
                SMBusManager.DetectSMBuses();
            }

            Step($"bus SMBus : {SMBusManager.RegisteredSMBuses.Count} trouvé(s)");
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
                    Step($"barrette à l'adresse 0x{address:X2} : lecture de {length} octets");
                    var bytes = new byte[length];
                    for (var i = 0; i < length; i++)
                    {
                        bytes[i] = accessor.At((ushort)i);
                    }

                    images.Add(new SpdImage(accessor.Index, bytes, Safe(accessor.GetModuleManufacturerString), Safe(accessor.GetDRAMManufacturerString)));
                    Step($"barrette à l'adresse 0x{address:X2} : lue");
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
        }
    }

    /// <summary>Journal des étapes (diagnostic), fourni par l'appelant ; rien par défaut.</summary>
    public static Action<string>? StepLog { get; set; }

    private static void Step(string step)
    {
        Diagnostics.Breadcrumbs.Add(step);
        StepLog?.Invoke(step);
    }

    /// <summary>Pilote SMBus de MAUS (modules PawnIO officiels, écritures filtrées), chargé une fois par session.</summary>
    private static void EnsureDriver()
    {
        if (DriverManager.Driver is not SpdBusDriver)
        {
            DriverManager.Driver = new SpdBusDriver();
            SMBusManager.UseWMI = false;
        }

        if (!DriverManager.LoadDriver())
        {
            throw new SmbusBusyException(T("Le pilote du bus des barrettes n'a pas pu être chargé."));
        }
    }

    /// <summary>
    /// Prend un verrou partagé. S'il a été abandonné (programme fermé en pleine lecture), Windows le donne quand même :
    /// il est alors bien à nous et doit être rendu comme les autres.
    /// </summary>
    internal static bool TryAcquire(Mutex? mutex, TimeSpan timeout)
    {
        if (mutex is null)
        {
            return false;
        }

        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true;
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
        var locked = PawnIoSpdSource.TryAcquire(_pci, TimeSpan.FromSeconds(5));
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
    /// <summary>
    /// Lit la fiche mémoire. <paramref name="log"/> reçoit chaque étape (journal de diagnostic
    /// <c>%LOCALAPPDATA%\MAUS\logs\lecture-memoire.txt</c>, utile si une lecture bloque).
    /// </summary>
    public static MemoryDetailReport Read(CpuIdInfo? cpu, bool? ddr5Hint, Action<string>? log = null)
    {
        log ??= _ => { };
        PawnIoSpdSource.StepLog = log;
        log($"processeur : {cpu?.Vendor} famille 0x{cpu?.Family:X} modèle 0x{cpu?.Model:X2}");
        ISpdSource spd = new LoggedSpdSource(new PawnIoSpdSource(), log);
        if (cpu is { IsIntel: true })
        {
            var intel = new IntelControllerAccess(
                cpu.Family,
                cpu.Model,
                () => Logged(log, "module IntelMCHBAR", () => new PawnIoMchbarReader()),
                () => Logged(log, "module IntelMSR", () => new PawnIoMsrReader()));
            var report = MemoryDetails.Read(spd, null, null, ddr5Hint, intel);
            log($"contrôleur Intel : {(report.Intel is { } i ? $"{i.Timings.Count} timings, canaux {string.Join(",", i.Channels)}" : "non lu")}");
            return report;
        }

        if (cpu is not { IsAmd: true, Family: >= 0x17 })
        {
            return MemoryDetails.Read(spd, null, null, ddr5Hint);
        }

        using var smn = Logged(log, "module AMDFamily17", () => new PawnIoSmnReader());
        using var pm = Logged(log, "module RyzenSMU", () => new PawnIoPmTableReader());
        return MemoryDetails.Read(spd, smn, pm, ddr5Hint);
    }

    private static T Logged<T>(Action<string> log, string step, Func<T> open)
    {
        log(step + " : ouverture");
        try
        {
            var value = open();
            log(step + " : ouvert");
            return value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log($"{step} : échec ({ex.GetType().Name}) {ex.Message}");
            throw;
        }
    }

    /// <summary>Note le début, la fin et la durée de la lecture des puces SPD.</summary>
    private sealed class LoggedSpdSource(ISpdSource inner, Action<string> log) : ISpdSource
    {
        public IReadOnlyList<SpdImage> ReadAll()
        {
            log("puces SPD : lecture");
            var started = DateTime.Now;
            try
            {
                var images = inner.ReadAll();
                log($"puces SPD : {images.Count} lue(s) en {(DateTime.Now - started).TotalSeconds:0.0} s");
                return images;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log($"puces SPD : échec après {(DateTime.Now - started).TotalSeconds:0.0} s ({ex.GetType().Name}) {ex.Message}");
                throw;
            }
        }
    }

    /// <summary>Message si PawnIO manque : la fiche détaillée en a besoin.</summary>
    public static string DriverRequired => T("La fiche mémoire détaillée lit la puce SPD des barrettes et les registres du contrôleur mémoire : il faut le pilote PawnIO (Atelier, En direct) et MAUS en administrateur.");
}
