namespace Maus.Core.Workshop.Memory.PawnIo;

/// <summary>Lecture des registres du contrôleur mémoire Intel (fenêtre MCHBAR), en lecture seule.</summary>
public interface IMchbarReader
{
    uint ReadDword(int offset);

    ulong ReadQword(int offset);
}

/// <summary>Lecture de registres MSR du processeur, en lecture seule.</summary>
public interface IMsrReader
{
    /// <summary>Valeur du MSR, ou <c>null</c> si le module la refuse ou si le processeur ne l'a pas.</summary>
    ulong? Read(uint msr);
}

/// <summary>
/// Module officiel IntelMCHBAR : il localise lui-même la fenêtre MCHBAR du processeur (Sandy Bridge et suivants),
/// refuse toute adresse hors de cette fenêtre et ne sait que lire (<c>ioctl_read_dword</c>, <c>ioctl_read_qword</c>).
/// </summary>
public sealed class PawnIoMchbarReader : IMchbarReader, IDisposable
{
    private readonly PawnIoModule _module = PawnIoModule.Load("IntelMCHBAR.bin");

    public uint ReadDword(int offset) => (uint)_module.Execute("ioctl_read_dword", [offset], 1)[0];

    public ulong ReadQword(int offset) => (ulong)_module.Execute("ioctl_read_qword", [offset], 1)[0];

    public void Dispose() => _module.Dispose();
}

/// <summary>
/// Module officiel IntelMSR : lecture d'une liste fermée de MSR, dont ceux du ring (0x620, 0x621). Ce module sait aussi
/// écrire quelques MSR de puissance (<c>ioctl_write_msr</c>) : cette classe ne l'expose pas et MAUS ne l'appelle jamais.
/// </summary>
public sealed class PawnIoMsrReader : IMsrReader, IDisposable
{
    private readonly PawnIoModule _module = PawnIoModule.Load("IntelMSR.bin");

    public ulong? Read(uint msr)
    {
        try
        {
            return (ulong)_module.Execute("ioctl_read_msr", [msr], 1)[0];
        }
        catch (PawnIoException)
        {
            return null;
        }
    }

    public void Dispose() => _module.Dispose();
}
