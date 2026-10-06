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
/// </summary>
public sealed partial class EtwDriverLatencyTracer : IDriverLatencyTracer
{
    private const string KernelLoggerName = "NT Kernel Logger";
    private const uint DpcFlag = 0x00000020;
    private const uint InterruptFlag = 0x00000040;
    private const uint RealTimeMode = 0x00000100;
    private const uint TracedGuid = 0x00020000;
    private const uint ControlStop = 1;
    private const int AlreadyExists = 183;
    private const int AccessDenied = 5;
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
            if (status == AlreadyExists)
            {
                throw new InvalidOperationException(T("La trace du noyau de Windows est déjà utilisée par un autre outil (LatencyMon, Process Monitor, enregistreur de performances…). Fermez-le, puis relancez la mesure."));
            }

            if (status == AccessDenied)
            {
                throw new InvalidOperationException(T("La mesure de latence des pilotes demande les droits administrateur."));
            }

            if (status != 0)
            {
                throw new InvalidOperationException(T("La trace du noyau n'a pas pu démarrer (code {0}).", status));
            }

            *(nint*)(logFile + 8) = loggerName;
            *(uint*)(logFile + 28) = ProcessTraceRealTime | ProcessTraceEventRecord | ProcessTraceRawTimestamp;
            *(delegate* unmanaged<byte*, void>*)(logFile + 424) = &OnEvent;
            trace = OpenTrace(logFile);
            if (trace == ulong.MaxValue)
            {
                throw new InvalidOperationException(T("La trace du noyau n'a pas pu être lue (code {0}).", Marshal.GetLastPInvokeError()));
            }

            var opened = trace;
            var reader = new Thread(() =>
            {
                var handle = opened;
                _ = ProcessTrace(&handle, 1, 0, 0);
            })
            { IsBackground = true, Name = "MAUS latence des pilotes" };
            reader.Start();
            var started = DateTime.UtcNow;
            cancellationToken.WaitHandle.WaitOne(duration);

            Prepare(properties, size, 0);
            _ = ControlTrace(0, KernelLoggerName, properties, ControlStop);
            session = 0;
            reader.Join(TimeSpan.FromSeconds(10));
            var lost = *(uint*)(properties + 88);
            return aggregator.Result(DateTime.UtcNow - started, lost);
        }
        finally
        {
            if (session != 0)
            {
                // Erreur après le démarrage : la session du noyau ne doit jamais rester ouverte.
                Prepare(properties, size, 0);
                _ = ControlTrace(0, KernelLoggerName, properties, ControlStop);
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

    /// <summary>Pilotes chargés et leur adresse (EnumDeviceDrivers, réelles seulement pour un administrateur).</summary>
    private static List<(ulong Base, string Name)> LoadedDrivers()
    {
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
