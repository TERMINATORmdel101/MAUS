using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop.Memory.PawnIo;

/// <summary>Échec d'un appel au pilote PawnIO (module refusé, processeur non pris en charge, lecture impossible).</summary>
public sealed class PawnIoException(string message) : Exception(message);

/// <summary>
/// Client minimal du pilote PawnIO : charge un module officiel embarqué dans MAUS (PawnIO.Modules 0.2.11, LGPL-2.1)
/// et appelle ses fonctions. Côté Intel, MAUS n'appelle que des fonctions de lecture : IntelMCHBAR ne sait que lire ;
/// IntelMSR sait aussi écrire quelques MSR de puissance, fonction que MAUS n'appelle jamais. Côté AMD, la table PM se lit
/// après une commande SMU qui la rafraîchit (une écriture dans la boîte aux lettres du SMU, documentée dans
/// <c>docs/store-security-design.md</c> §3.3), et la lecture d'une puce SPD DDR4/DDR5 passe par une sélection de page, qui
/// est une écriture SMBus encadrée (<c>SpdBusDriver</c>). Le module vérifie lui-même chaque adresse demandée. Même
/// protocole que LibreHardwareMonitor (<c>PawnIo.cs</c>, MPL-2.0).
/// </summary>
public sealed partial class PawnIoModule : IDisposable
{
    private const uint DeviceType = 41394u << 16;
    private const uint LoadBinaryCode = DeviceType | (0x821u << 2);
    private const uint ExecuteCode = DeviceType | (0x841u << 2);
    private const int FunctionNameLength = 32;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;

    private readonly SafeFileHandle _handle;

    private PawnIoModule(SafeFileHandle handle) => _handle = handle;

    /// <summary>Charge le module embarqué <paramref name="resourceName"/> (par exemple « IntelMCHBAR.bin »).</summary>
    /// <exception cref="PawnIoException">Pilote absent, droits insuffisants ou module refusé par le pilote (processeur non pris en charge).</exception>
    public static PawnIoModule Load(string resourceName)
    {
        var name = $"Maus.Core.PawnIo.{resourceName}";
        using var stream = typeof(PawnIoModule).Assembly.GetManifestResourceStream(name)
            ?? throw new PawnIoException(T("Module PawnIO absent de MAUS : {0}", resourceName));
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Load(memory.ToArray());
    }

    public static unsafe PawnIoModule Load(byte[] binary)
    {
        ArgumentNullException.ThrowIfNull(binary);

        var handle = CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", GenericRead | GenericWrite, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new PawnIoException(T("Pilote PawnIO inaccessible ({0})", new Win32Exception(error).Message));
        }

        fixed (byte* input = binary)
        {
            if (!DeviceIoControl(handle, LoadBinaryCode, input, (uint)binary.Length, null, 0, out _, 0))
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new PawnIoException(T("Module PawnIO refusé ({0})", new Win32Exception(error).Message));
            }
        }

        return new PawnIoModule(handle);
    }

    /// <summary>Appelle la fonction <paramref name="function"/> du module et renvoie ses <paramref name="outputLength"/> valeurs de sortie.</summary>
    public long[] Execute(string function, ReadOnlySpan<long> input, int outputLength)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentOutOfRangeException.ThrowIfNegative(outputLength);

        var outputBytes = checked((uint)(outputLength * sizeof(long)));
        var output = new long[outputLength];
        if (!Send(function, input, MemoryMarshal.AsBytes(output.AsSpan()), out var returned))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new PawnIoException(T("{0} a échoué ({1})", function, new Win32Exception(error).Message));
        }

        if (returned < outputBytes)
        {
            throw new PawnIoException(T("{0} : réponse incomplète ({1} octets)", function, returned));
        }

        return output;
    }

    /// <summary>
    /// Demande au pilote : nom de la fonction, puis valeurs d'entrée. Taille calculée en arithmétique vérifiée : un débordement
    /// lève une erreur au lieu d'un tampon trop petit. L'erreur de Windows se lit juste après, en cas d'échec.
    /// </summary>
    private unsafe bool Send(string function, ReadOnlySpan<long> input, Span<byte> output, out uint returned)
    {
        var request = new byte[checked(FunctionNameLength + (input.Length * sizeof(long)))];
        Encoding.ASCII.GetBytes(function, 0, Math.Min(function.Length, FunctionNameLength - 1), request, 0);
        MemoryMarshal.AsBytes(input).CopyTo(request.AsSpan(FunctionNameLength));

        fixed (byte* pIn = request)
        fixed (byte* pOut = output)
        {
            return DeviceIoControl(_handle, ExecuteCode, pIn, (uint)request.Length, pOut, (uint)output.Length, out returned, 0);
        }
    }

    /// <summary>
    /// Même appel, au format attendu par RAMSPDToolkit : code HRESULT (0 = réussi) au lieu d'une exception, et nombre de
    /// valeurs réellement renvoyées (comme <c>PawnIo.ExecuteHr</c> de LibreHardwareMonitor).
    /// </summary>
    public int ExecuteHr(string function, long[] input, uint inputCount, long[] output, uint outputCount, out uint returned)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(output);

        var buffer = new byte[checked(outputCount * sizeof(long))];
        if (!Send(function, input.AsSpan(0, checked((int)inputCount)), buffer, out var bytes))
        {
            returned = 0;
            var error = Marshal.GetLastPInvokeError();
            return error <= 0 ? error : unchecked((int)(((uint)error & 0xFFFF) | (7u << 16) | 0x80000000));
        }

        Buffer.BlockCopy(buffer, 0, output, 0, (int)Math.Min(bytes, (uint)(output.Length * sizeof(long))));
        returned = bytes / sizeof(long);
        return 0;
    }

    public void Dispose() => _handle.Dispose();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint returned, nint overlapped);
}
