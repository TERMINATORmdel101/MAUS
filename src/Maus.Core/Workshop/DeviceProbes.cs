using System.Runtime.InteropServices;

namespace Maus.Core.Workshop;

/// <summary>Son d'essai des haut-parleurs : 440 Hz pendant 0,8 s, sur la voie gauche, la droite ou les deux.</summary>
public static class TestTone
{
    private const int Rate = 22_050;
    private const int Frames = Rate * 4 / 5;

    public static byte[] Wave(bool left, bool right)
    {
        var bytes = new byte[44 + (Frames * 4)];
        using var writer = new BinaryWriter(new MemoryStream(bytes));
        writer.Write("RIFF"u8);
        writer.Write(36 + (Frames * 4));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(Rate);
        writer.Write(Rate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(Frames * 4);
        for (var i = 0; i < Frames; i++)
        {
            var fade = Math.Min(1, Math.Min(i, Frames - 1 - i) / (Rate * 0.03));
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / Rate) * 9000 * fade);
            writer.Write(left ? sample : (short)0);
            writer.Write(right ? sample : (short)0);
        }

        return bytes;
    }
}

/// <summary>Plus grand nombre de mesures de la souris reçues en une seconde.</summary>
public sealed class MouseRate
{
    private long? _windowStart;
    private int _count;

    public int Best { get; private set; }

    public void Add(long milliseconds)
    {
        if (_windowStart is not { } start || milliseconds - start >= 1000)
        {
            _windowStart = milliseconds;
            _count = 0;
        }

        Best = Math.Max(Best, ++_count);
    }
}

/// <summary>Double clic involontaire : deux appuis trop rapprochés pour venir d'un doigt.</summary>
public sealed class ClickBounce
{
    /// <summary>Seuil choisi par MAUS (aucune norme) ; il est annoncé à l'écran.</summary>
    public const int LimitMilliseconds = 60;

    private long? _last;

    public int Count { get; private set; }

    public void Add(long milliseconds)
    {
        if (milliseconds - _last < LimitMilliseconds)
        {
            Count++;
        }

        _last = milliseconds;
    }
}

/// <summary>Messages bruts de la souris pour une fenêtre, seulement quand elle est au premier plan (jamais le clavier).</summary>
public static partial class RawMouse
{
    private const uint Remove = 0x00000001;

    public static bool Listen(nint window) => Register(0, window);

    public static bool Stop() => Register(Remove, 0);

    private static bool Register(uint flags, nint window) =>
        RegisterRawInputDevices([new RawInputDevice { UsagePage = 1, Usage = 2, Flags = flags, Target = window }], 1, (uint)Marshal.SizeOf<RawInputDevice>());

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
}

/// <summary>État de la batterie d'après Windows, et autonomie estimée d'après une mesure de décharge.</summary>
public static partial class BatteryProbe
{
    private const byte NoBattery = 128;

    public sealed record State(bool HasBattery, bool OnBattery, int? Percent);

    public static State Read()
    {
        if (!GetSystemPowerStatus(out var status) || status.BatteryFlag == NoBattery)
        {
            return new State(false, false, null);
        }

        return new State(true, status.AcLineStatus == 0, status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null);
    }

    /// <summary>Temps restant si la décharge continue au même rythme ; <c>null</c> si la batterie n'a pas assez baissé pour le dire.</summary>
    public static TimeSpan? Remaining(int startPercent, int endPercent, TimeSpan elapsed)
    {
        var drop = startPercent - endPercent;
        return drop <= 0 || endPercent < 0 ? null : elapsed * (endPercent / (double)drop);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out PowerStatus status);
}
