using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Maus.Core.Workshop.Memory.PawnIo;

/// <summary>Échec d'un appel au pilote PawnIO (module refusé, processeur non pris en charge, lecture impossible).</summary>
public sealed class PawnIoException(string message, int win32Error) : Exception(message)
{
    public int Win32Error { get; } = win32Error;
}

/// <summary>
/// Client minimal du pilote PawnIO : charge un module officiel embarqué dans MAUS (PawnIO.Modules 0.2.11, LGPL-2.1)
/// et appelle ses fonctions. MAUS n'appelle que des fonctions de lecture : IntelMCHBAR ne sait que lire ; IntelMSR sait
/// aussi écrire quelques MSR de puissance, fonction que MAUS n'appelle jamais. Le module vérifie lui-même chaque adresse
/// demandée. Même protocole que LibreHardwareMonitor (<c>PawnIo.cs</c>, MPL-2.0).
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
            ?? throw new PawnIoException($"Module PawnIO absent de MAUS : {resourceName}", 0);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Load(memory.ToArray());
    }

    public static unsafe PawnIoModule Load(byte[] binary)
    {
        var handle = CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", GenericRead | GenericWrite, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new PawnIoException($"Pilote PawnIO inaccessible ({new Win32Exception(error).Message})", error);
        }

        fixed (byte* input = binary)
        {
            if (!DeviceIoControl(handle, LoadBinaryCode, input, (uint)binary.Length, null, 0, out _, 0))
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new PawnIoException($"Module PawnIO refusé ({new Win32Exception(error).Message})", error);
            }
        }

        return new PawnIoModule(handle);
    }

    /// <summary>Appelle la fonction <paramref name="function"/> du module et renvoie ses <paramref name="outputLength"/> valeurs de sortie.</summary>
    public unsafe long[] Execute(string function, ReadOnlySpan<long> input, int outputLength)
    {
        var request = new byte[FunctionNameLength + (input.Length * sizeof(long))];
        Encoding.ASCII.GetBytes(function, 0, Math.Min(function.Length, FunctionNameLength - 1), request, 0);
        MemoryMarshal.AsBytes(input).CopyTo(request.AsSpan(FunctionNameLength));
        var output = new long[outputLength];

        fixed (byte* pIn = request)
        fixed (long* pOut = output)
        {
            if (!DeviceIoControl(_handle, ExecuteCode, pIn, (uint)request.Length, pOut, (uint)(outputLength * sizeof(long)), out var returned, 0))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new PawnIoException($"{function} a échoué ({new Win32Exception(error).Message})", error);
            }

            if (returned < outputLength * sizeof(long))
            {
                throw new PawnIoException($"{function} : réponse incomplète ({returned} octets)", 0);
            }
        }

        return output;
    }

    /// <summary>
    /// Même appel, au format attendu par RAMSPDToolkit : code HRESULT (0 = réussi) au lieu d'une exception, et nombre de
    /// valeurs réellement renvoyées (comme <c>PawnIo.ExecuteHr</c> de LibreHardwareMonitor).
    /// </summary>
    public unsafe int ExecuteHr(string function, long[] input, uint inputCount, long[] output, uint outputCount, out uint returned)
    {
        var request = new byte[FunctionNameLength + (inputCount * sizeof(long))];
        Encoding.ASCII.GetBytes(function, 0, Math.Min(function.Length, FunctionNameLength - 1), request, 0);
        MemoryMarshal.AsBytes(input.AsSpan(0, (int)inputCount)).CopyTo(request.AsSpan(FunctionNameLength));
        var buffer = new byte[outputCount * sizeof(long)];

        fixed (byte* pIn = request)
        fixed (byte* pOut = buffer)
        {
            if (!DeviceIoControl(_handle, ExecuteCode, pIn, (uint)request.Length, buffer.Length == 0 ? null : pOut, (uint)buffer.Length, out var bytes, 0))
            {
                returned = 0;
                var error = Marshal.GetLastPInvokeError();
                return error <= 0 ? error : unchecked((int)(((uint)error & 0xFFFF) | (7u << 16) | 0x80000000));
            }

            Buffer.BlockCopy(buffer, 0, output, 0, (int)Math.Min(bytes, (uint)(output.Length * sizeof(long))));
            returned = bytes / sizeof(long);
            return 0;
        }
    }

    public void Dispose() => _handle.Dispose();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint returned, nint overlapped);
}
