using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace Maus.Core.Workshop;

/// <summary>Processus de Windows lus avec les droits minimaux (<c>PROCESS_QUERY_LIMITED_INFORMATION</c>), sans rien modifier.</summary>
public sealed partial class WindowsProcessSource : IProcessSource, IDisposable
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private readonly PdhQuery _pdh = new();
    private readonly nint _gpuEngine;

    public WindowsProcessSource()
    {
        _gpuEngine = _pdh.Add(@"\GPU Engine(*)\Utilization Percentage");
        _pdh.Collect();
    }

    public IReadOnlyList<RawProcess> Read()
    {
        var result = new List<RawProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var (path, cpu, io, start) = Query(process.Id);
                    result.Add(new RawProcess(
                        process.Id,
                        path is null ? process.ProcessName : Path.GetFileName(path),
                        path,
                        cpu,
                        process.WorkingSet64,
                        process.PrivateMemorySize64,
                        io,
                        process.SessionId,
                        start));
                }
                catch (InvalidOperationException)
                {
                    // Processus terminé entre-temps.
                }
            }
        }

        return result;
    }

    public IReadOnlyDictionary<int, double> GpuPercentByPid()
    {
        _pdh.Collect();
        return GpuPercentByPid(PdhQuery.Array(_gpuEngine));
    }

    /// <summary>
    /// Par processus, le moteur le plus occupé (3D, copie, décodage vidéo…), comme le Gestionnaire des tâches : additionner des
    /// moteurs différents ne mesure rien de réel. Les instances d'un même type de moteur, elles, s'additionnent.
    /// </summary>
    internal static Dictionary<int, double> GpuPercentByPid(IEnumerable<(string Name, double Value)> instances)
    {
        var byEngine = new Dictionary<(int Pid, string Engine), double>();
        foreach (var (name, value) in instances)
        {
            var match = PidPattern().Match(name);
            if (match.Success && int.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var pid))
            {
                var key = (pid, EngineTypePattern().Match(name).Groups[1].Value);
                byEngine[key] = byEngine.GetValueOrDefault(key) + value;
            }
        }

        return byEngine.GroupBy(e => e.Key.Pid).ToDictionary(g => g.Key, g => g.Max(e => e.Value));
    }

    public void Dispose() => _pdh.Dispose();

    /// <summary>SID du compte d'un processus ; <c>null</c> si Windows ne le dit pas (processus protégé, déjà terminé).</summary>
    internal static string? OwnerOf(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            if (!OpenProcessToken(handle, TokenQuery, out var token))
            {
                return null;
            }

            try
            {
                using var identity = new WindowsIdentity(token);
                return identity.User?.Value;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static (string? Path, TimeSpan? Cpu, ulong? Io, DateTime? Start) Query(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (handle == 0)
        {
            return (null, null, null, null);
        }

        try
        {
            string? path = null;
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            if (QueryFullProcessImageNameW(handle, 0, buffer, ref size))
            {
                path = new string(buffer, 0, (int)size);
            }

            TimeSpan? cpu = null;
            DateTime? start = null;
            if (GetProcessTimes(handle, out var creation, out _, out var kernel, out var user))
            {
                cpu = TimeSpan.FromTicks(kernel + user);
                start = creation > 0 ? DateTime.FromFileTimeUtc(creation) : null;
            }

            ulong? io = GetProcessIoCounters(handle, out var counters) ? counters.ReadTransferCount + counters.WriteTransferCount : null;
            return (path, cpu, io, start);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [GeneratedRegex(@"^pid_(\d+)_")]
    private static partial Regex PidPattern();

    [GeneratedRegex(@"_engtype_(.*)$")]
    private static partial Regex EngineTypePattern();

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(nint process, out IoCounters counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

/// <summary>Arrêt d'un processus, uniquement sur demande explicite et jamais pour un processus vital (<see cref="ProcessRules.CanTerminate"/>).</summary>
public static class WindowsProcessTerminator
{
    /// <exception cref="InvalidOperationException">Arrêt refusé (processus vital, protégé ou déjà terminé).</exception>
    public static void Terminate(ProcessSample process)
    {
        ArgumentNullException.ThrowIfNull(process);

        var (allowed, reason) = ProcessRules.CanTerminate(process, Environment.ProcessId, ProcessRules.CurrentSessionId);
        if (!allowed)
        {
            throw new InvalidOperationException(reason);
        }

        // Le programme d'un autre compte ouvert dans cette session ne s'arrête pas non plus.
        using (var self = WindowsIdentity.GetCurrent())
        {
            var owner = WindowsProcessSource.OwnerOf(process.Pid);
            if (owner is null || !(owner == self.User?.Value || owner == Platform.SessionUser.Sid()))
            {
                throw new InvalidOperationException(Localization.Texts.T("Ce processus appartient à un autre compte, ou Windows ne dit pas lequel : MAUS ne l'arrête pas."));
            }
        }

        try
        {
            using var target = Process.GetProcessById(process.Pid);
            if (target.StartTime.ToUniversalTime() != process.StartTime)
            {
                throw new InvalidOperationException(Localization.Texts.T("Ce processus s'est déjà terminé."));
            }

            target.Kill(entireProcessTree: false);
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            throw new InvalidOperationException(Localization.Texts.T("Windows a refusé d'arrêter ce processus : {0}", ex.Message), ex);
        }
    }
}
