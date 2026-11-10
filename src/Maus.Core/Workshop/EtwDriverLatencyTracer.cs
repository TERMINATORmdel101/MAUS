using System.Buffers.Binary;
using System.Runtime.InteropServices;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>
/// Latence des pilotes par la trace du noyau de Windows (ETW, session « NT Kernel Logger », temps réel), en lecture
/// seule : la session ne fait qu'observer et s'arrête à la fin de la mesure. Sources (Microsoft Learn) : « PerfInfo class »
/// (GUID ce1dbfb4-137e-4da6-87b0-3f59aa102cbc, types 66 DPC en fil, 67 interruption, 68 DPC, 69 DPC de minuterie),
/// « DPC class » et « ISR class » (heure d'entrée InitialTime puis adresse Routine ; durée = heure de l'événement moins
/// heure d'entrée), « EVENT_TRACE_PROPERTIES », « StartTraceW », « OpenTraceW », « ProcessTrace », « ControlTraceW »,
/// « EVENT_RECORD », « EnumDeviceDrivers ». Horloge : compteur de performance (ClientContext = 1) lu sans conversion
/// (PROCESS_TRACE_MODE_RAW_TIMESTAMP), pour que l'heure de l'événement et l'heure d'entrée soient dans la même unité.
/// La session survit au processus qui l'a ouverte : elle est refermée aussi à la sortie du processus, et celle d'un moteur
/// tué l'est au lancement suivant, d'après <see cref="LatencySessionMarker"/>.
/// </summary>
public sealed partial class EtwDriverLatencyTracer : IDriverLatencyTracer
{
    private const string KernelLoggerName = "NT Kernel Logger";
    private const uint DpcFlag = 0x00000020;
    private const uint InterruptFlag = 0x00000040;
    private const uint RealTimeMode = 0x00000100;
    private const uint TracedGuid = 0x00020000;
    private const uint ControlQuery = 0;
    private const uint ControlStop = 1;
    private const int AlreadyExists = 183;
    private const int AccessDenied = 5;
    private const int Cancelled = 1223;
    private const int MoreData = 234;
    private const int SessionNotFound = 4201;
    private const uint ProcessTraceRealTime = 0x00000100;
    private const uint ProcessTraceRawTimestamp = 0x00001000;
    private const uint ProcessTraceEventRecord = 0x10000000;
    private const int PropertiesSize = 120;
    private const int LogFileSize = 448;
    private const ushort Header64Bit = 0x0020;

    private static readonly Guid SystemTraceControlGuid = new("9e814aad-3204-11d2-9a82-006008a86939");
    private static readonly Guid PerfInfoGuid = new("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");

    /// <summary>Une seule mesure à la fois : la session du noyau est unique sur le PC.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static DriverLatencyAggregator? s_current;
    private static double s_ticksPerMicrosecond;
    private static bool s_exitHooked;

    /// <summary>1 tant que ce processus a une session ouverte.</summary>
    private static int s_sessionOpen;

    /// <summary>Dossier où la mesure en cours a laissé sa trace.</summary>
    private static string? s_markerFolder;

    /// <summary>Session laissée par un moteur tué en pleine mesure : refermée au lancement suivant. Sans droits : rien à faire.</summary>
    public static void CloseLeftoverSession()
    {
        if (!OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess)
        {
            return;
        }

        try
        {
            Gate.Wait();
            try
            {
                CloseLeftover();
            }
            finally
            {
                Gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Diagnostics.Breadcrumbs.Add("latence : session précédente non vérifiée (" + ex.GetType().Name + ")");
        }
    }

    public async Task<DriverLatencyResult> MeasureAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException(T("La mesure de latence des pilotes n'existe que sous Windows."));
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Measure(duration, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static unsafe DriverLatencyResult Measure(TimeSpan duration, CancellationToken cancellationToken)
    {
        QueryPerformanceFrequency(out var frequency);
        s_ticksPerMicrosecond = frequency / 1_000_000.0;
        var aggregator = new DriverLatencyAggregator(LoadedDrivers());
        s_current = aggregator;
        HookProcessExit();

        var nameBytes = (KernelLoggerName.Length + 1) * 2;
        var size = PropertiesSize + nameBytes + 2;
        var properties = (byte*)NativeMemory.AllocZeroed((nuint)size);
        var loggerName = Marshal.StringToHGlobalUni(KernelLoggerName);
        var logFile = (byte*)NativeMemory.AllocZeroed(LogFileSize);
        ulong session = 0;
        var trace = ulong.MaxValue;
        try
        {
            Prepare(properties, size, DpcFlag | InterruptFlag);
            var status = StartTrace(out session, KernelLoggerName, properties);
            if (status == AlreadyExists && CloseLeftover())
            {
                Prepare(properties, size, DpcFlag | InterruptFlag);
                status = StartTrace(out session, KernelLoggerName, properties);
            }

            if (status == AlreadyExists)
            {
                throw new InvalidOperationException(T("La trace du noyau de Windows est déjà utilisée par un autre outil (LatencyMon, Process Monitor, enregistreur de performances…) ou par une mesure de MAUS interrompue. Fermez cet outil ou redémarrez le PC, puis relancez la mesure."));
            }

            if (status == AccessDenied)
            {
                throw new InvalidOperationException(T("La mesure de latence des pilotes demande les droits administrateur."));
            }

            if (status != 0)
            {
                throw new InvalidOperationException(T("La trace du noyau n'a pas pu démarrer (code {0}).", status));
            }

            NoteSessionOpen();
            *(nint*)(logFile + 8) = loggerName;
            *(uint*)(logFile + 28) = ProcessTraceRealTime | ProcessTraceEventRecord | ProcessTraceRawTimestamp;
            *(delegate* unmanaged<byte*, void>*)(logFile + 424) = &OnEvent;
            trace = OpenTrace(logFile);
            if (trace == ulong.MaxValue)
            {
                throw new InvalidOperationException(T("La trace du noyau n'a pas pu être lue (code {0}).", Marshal.GetLastPInvokeError()));
            }

            var opened = trace;
            var readStatus = 0;
            var reader = new Thread(() =>
            {
                var handle = opened;
                readStatus = ProcessTrace(&handle, 1, 0, 0);
            })
            { IsBackground = true, Name = "MAUS latence des pilotes" };
            reader.Start();
            var started = DateTime.UtcNow;
            cancellationToken.WaitHandle.WaitOne(duration);

            StopSession(properties, size);
            session = 0;
            reader.Join(TimeSpan.FromSeconds(10));
            var lost = *(uint*)(properties + 88);
            var result = aggregator.Result(DateTime.UtcNow - started, lost);

            // Une lecture qui échoue dès le départ ne rend rien : ce n'est pas une mesure sans latence.
            if (result.Events == 0 && readStatus is not (0 or Cancelled))
            {
                throw new InvalidOperationException(T("La trace du noyau n'a pas pu être lue (code {0}).", readStatus));
            }

            return result;
        }
        finally
        {
            if (session != 0)
            {
                // Erreur après le démarrage : la session du noyau ne doit jamais rester ouverte.
                StopSession(properties, size);
            }

            if (trace != ulong.MaxValue)
            {
                _ = CloseTrace(trace);
            }

            s_current = null;
            Marshal.FreeHGlobal(loggerName);
            NativeMemory.Free(logFile);
            NativeMemory.Free(properties);
        }
    }

    /// <summary>La session est ouverte : si le processus disparaît maintenant, le lancement suivant saura qu'elle est à MAUS.</summary>
    private static void NoteSessionOpen()
    {
        Volatile.Write(ref s_sessionOpen, 1);
        s_markerFolder = LatencySessionMarker.ProtectedFolder();
        if (s_markerFolder is { } folder)
        {
            LatencySessionMarker.ForThisProcess().Write(folder);
        }
    }

    /// <summary>Referme la session. Sa trace n'est effacée que si Windows l'a vraiment refermée.</summary>
    private static unsafe void StopSession(byte* properties, int size)
    {
        Volatile.Write(ref s_sessionOpen, 0);
        Prepare(properties, size, 0);
        var status = ControlTrace(0, KernelLoggerName, properties, ControlStop);
        if (status is 0 or MoreData or SessionNotFound && s_markerFolder is { } folder)
        {
            LatencySessionMarker.Delete(folder);
        }
    }

    /// <summary>
    /// Fin du processus en pleine mesure (<see cref="Environment.Exit(int)"/>, erreur fatale) : le fil de mesure ne reprendra
    /// pas la main, c'est ici que la session se referme.
    /// </summary>
    private static void HookProcessExit()
    {
        if (s_exitHooked)
        {
            return;
        }

        s_exitHooked = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopOnExit();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => StopOnExit();
    }

    private static unsafe void StopOnExit()
    {
        if (Volatile.Read(ref s_sessionOpen) == 0)
        {
            return;
        }

        var size = PropertiesSize + ((KernelLoggerName.Length + 1) * 2) + 2;
        var properties = (byte*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            StopSession(properties, size);
        }
        finally
        {
            NativeMemory.Free(properties);
        }
    }

    /// <summary>
    /// Referme la session du noyau si elle a été laissée par un moteur de MAUS disparu : il faut sa trace, écrite depuis le
    /// dernier démarrage du PC, un moteur qui ne tourne plus, et une session réglée comme les nôtres. Dans le doute, rien n'est
    /// coupé : la session peut être celle d'un autre outil.
    /// </summary>
    /// <returns>Vrai si une session laissée par MAUS vient d'être refermée.</returns>
    private static unsafe bool CloseLeftover()
    {
        if (LatencySessionMarker.ProtectedFolder() is not { } folder || LatencySessionMarker.Read(folder) is not { } marker)
        {
            return false;
        }

        var boot = LatencySessionMarker.CurrentBootUtc;
        if (marker.IsFromEarlierBoot(boot))
        {
            LatencySessionMarker.Delete(folder);
            return false;
        }

        if (!marker.IsLeftover(boot, LatencySessionMarker.IsRunning(marker.ProcessId, marker.ProcessStartUtc)))
        {
            return false;
        }

        var size = PropertiesSize + ((KernelLoggerName.Length + 1) * 2) + 2;
        var properties = (byte*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            Prepare(properties, size, 0);
            var status = ControlTrace(0, KernelLoggerName, properties, ControlQuery);
            if (status == SessionNotFound)
            {
                LatencySessionMarker.Delete(folder);
                return false;
            }

            if (status != 0)
            {
                return false;
            }

            if (!LatencySessionMarker.LooksLikeOurSession(*(uint*)(properties + 72), *(uint*)(properties + 64), DpcFlag | InterruptFlag, RealTimeMode))
            {
                LatencySessionMarker.Delete(folder);
                return false;
            }

            Prepare(properties, size, 0);
            status = ControlTrace(0, KernelLoggerName, properties, ControlStop);
            Diagnostics.Breadcrumbs.Add("latence : session laissée par un moteur disparu, fermeture (code " + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
            if (status is not (0 or MoreData))
            {
                return false;
            }

            LatencySessionMarker.Delete(folder);
            return true;
        }
        finally
        {
            NativeMemory.Free(properties);
        }
    }

    /// <summary>EVENT_TRACE_PROPERTIES (120 octets en 64 bits) suivi du nom de la session.</summary>
    private static unsafe void Prepare(byte* properties, int size, uint flags)
    {
        new Span<byte>(properties, size).Clear();
        *(uint*)properties = (uint)size;
        *(Guid*)(properties + 24) = SystemTraceControlGuid;
        *(uint*)(properties + 40) = 1; // horloge : compteur de performance
        *(uint*)(properties + 44) = TracedGuid;
        *(uint*)(properties + 64) = RealTimeMode;
        *(uint*)(properties + 72) = flags;
        *(uint*)(properties + 116) = PropertiesSize;
    }

    /// <summary>EVENT_RECORD : en-tête (horodatage en 16, fournisseur en 24, type en 45, drapeaux en 4), données en 96.</summary>
    [UnmanagedCallersOnly]
    private static unsafe void OnEvent(byte* record)
    {
        try
        {
            if (s_current is not { } aggregator || *(Guid*)(record + 24) != PerfInfoGuid)
            {
                return;
            }

            var opcode = record[45];
            if (opcode is not (66 or 67 or 68 or 69))
            {
                return;
            }

            var timestamp = *(long*)(record + 16);
            var length = *(ushort*)(record + 86);
            var data = *(byte**)(record + 96);
            // Taille des adresses d'après la longueur des données (« DPC class » : 8 + adresse ; « ISR class » : 8 + adresse
            // + 4), plus sûre que le drapeau d'en-tête, absent en lecture brute.
            var pointerSize = length >= (opcode == 67 ? 20 : 16) || (*(ushort*)(record + 4) & Header64Bit) != 0 ? 8 : 4;
            if (data is null || length < 8 + pointerSize)
            {
                return;
            }

            var payload = new ReadOnlySpan<byte>(data, length);
            var initial = BinaryPrimitives.ReadInt64LittleEndian(payload);
            var routine = pointerSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(payload[8..]) : BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
            aggregator.Add(routine, (timestamp - initial) / s_ticksPerMicrosecond);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Un événement mal formé est ignoré : une exception ne doit jamais remonter dans le code de Windows.
        }
    }

    /// <summary>
    /// Pilotes chargés et leur adresse (EnumDeviceDrivers). Depuis Windows 11 24H2, les adresses ne sont réelles qu'avec
    /// SeDebugPrivilege actif : il est activé le temps de cette liste, et rien n'est activé sans droits administrateur.
    /// </summary>
    private static List<(ulong Base, string Name)> LoadedDrivers()
    {
        using var debug = Platform.TokenPrivileges.Enable(Platform.TokenPrivileges.Debug);
        var drivers = new List<(ulong, string)>();
        var bases = new nint[2048];
        if (!EnumDeviceDrivers(bases, bases.Length * IntPtr.Size, out var needed))
        {
            return drivers;
        }

        Span<char> name = stackalloc char[260];
        for (var i = 0; i < Math.Min(needed / IntPtr.Size, bases.Length); i++)
        {
            int length;
            unsafe
            {
                fixed (char* buffer = name)
                {
                    length = GetDeviceDriverBaseName(bases[i], buffer, name.Length);
                }
            }

            if (length > 0)
            {
                drivers.Add(((ulong)bases[i], new string(name[..length])));
            }
        }

        return drivers;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int StartTrace(out ulong session, string name, byte* properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int ControlTrace(ulong session, string name, byte* properties, uint control);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
    private static unsafe partial ulong OpenTrace(byte* logFile);

    [LibraryImport("advapi32.dll")]
    private static unsafe partial int ProcessTrace(ulong* handles, uint count, nint start, nint end);

    [LibraryImport("advapi32.dll")]
    private static partial int CloseTrace(ulong trace);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryPerformanceFrequency(out long frequency);

    [LibraryImport("kernel32.dll", EntryPoint = "K32EnumDeviceDrivers")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDeviceDrivers([Out] nint[] drivers, int size, out int needed);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetDeviceDriverBaseNameW")]
    private static unsafe partial int GetDeviceDriverBaseName(nint driver, char* name, int size);
}
