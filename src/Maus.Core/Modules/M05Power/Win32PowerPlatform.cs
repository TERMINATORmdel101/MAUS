using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Maus.Core.Modules.M05Power;

/// <summary>
/// Implémentation réelle par P/Invoke. Uniquement des fonctions de lecture : <c>PowerGetActiveScheme</c>,
/// <c>CallNtPowerInformation</c> (niveau SystemPowerCapabilities), abonnement temporaire au mode effectif
/// et <c>GetLogicalProcessorInformationEx</c>.
/// </summary>
internal sealed partial class Win32PowerPlatform : IPowerPlatform
{
    private const int SystemPowerCapabilitiesLevel = 4;
    private const uint EffectivePowerModeV2 = 2;
    private const int RelationProcessorCore = 0;

    /// <summary>Abonnements en cours, indexés par un identifiant passé comme contexte au rappel natif.</summary>
    private static readonly ConcurrentDictionary<nint, TaskCompletionSource<int>> PendingModes = new();
    private static long _nextRegistration;

    public Guid? GetActiveScheme()
    {
        if (PowerGetActiveScheme(0, out var pointer) != 0 || pointer == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    public unsafe PowerCapabilities? GetCapabilities()
    {
        var buffer = new byte[PowerCapabilities.NativeSize];
        fixed (byte* output = buffer)
        {
            if (CallNtPowerInformation(SystemPowerCapabilitiesLevel, null, 0, output, (uint)buffer.Length) != 0)
            {
                return null;
            }
        }

        return PowerCapabilities.Parse(buffer);
    }

    public async Task<EffectivePowerMode?> GetEffectivePowerModeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = (nint)Interlocked.Increment(ref _nextRegistration);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingModes[id] = completion;
        var handle = (nint)0;
        try
        {
            if (Register(id, out handle) != 0)
            {
                return null;
            }

            // Le rappel est invoqué une première fois dès l'abonnement, avec le mode courant.
            var mode = await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return Enum.IsDefined((EffectivePowerMode)mode) ? (EffectivePowerMode)mode : null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (handle != 0)
            {
                _ = PowerUnregisterFromEffectivePowerModeNotifications(handle);
            }

            PendingModes.TryRemove(id, out _);
        }
    }

    public unsafe int? GetEfficiencyClassCount()
    {
        uint length = 0;
        _ = GetLogicalProcessorInformationEx(RelationProcessorCore, null, ref length);
        if (length == 0)
        {
            return null;
        }

        var buffer = new byte[length];
        fixed (byte* pointer = buffer)
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, pointer, ref length))
            {
                return null;
            }
        }

        return ProcessorTopology.CountEfficiencyClasses(buffer.AsSpan(0, (int)length));
    }

    /// <summary>Séparé de la méthode asynchrone : un pointeur de fonction exige un contexte unsafe, interdit avec await.</summary>
    private static unsafe int Register(nint id, out nint handle)
    {
        nint registration = 0;
        var result = PowerRegisterForEffectivePowerModeNotifications(EffectivePowerModeV2, &OnEffectivePowerModeChanged, id, &registration);
        handle = registration;
        return result;
    }

    /// <summary>Un rappel tardif (après désabonnement) ne trouve plus son identifiant et est ignoré.</summary>
    [UnmanagedCallersOnly]
    private static void OnEffectivePowerModeChanged(int mode, nint context)
    {
        if (PendingModes.TryGetValue(context, out var completion))
        {
            completion.TrySetResult(mode);
        }
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    private static unsafe partial int CallNtPowerInformation(int informationLevel, void* inputBuffer, uint inputBufferLength, void* outputBuffer, uint outputBufferLength);

    [LibraryImport("powrprof.dll")]
    private static unsafe partial int PowerRegisterForEffectivePowerModeNotifications(uint version, delegate* unmanaged<int, nint, void> callback, nint context, nint* registrationHandle);

    [LibraryImport("powrprof.dll")]
    private static partial int PowerUnregisterFromEffectivePowerModeNotifications(nint registrationHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetLogicalProcessorInformationEx(int relationshipType, void* buffer, ref uint returnedLength);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
