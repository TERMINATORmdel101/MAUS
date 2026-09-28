using System.Runtime.InteropServices;

namespace Maus.Core.Platform;

/// <summary>
/// Précision de l'horloge système de Windows (timeBeginPeriod / timeEndPeriod, winmm.dll), relevée le temps d'une opération
/// qui enchaîne de très courtes pauses, puis rendue.
/// </summary>
public sealed partial class TimerResolution : IDisposable
{
    private readonly bool _active;

    private TimerResolution(bool active) => _active = active;

    /// <summary>Horloge à 1 ms jusqu'à <see cref="Dispose"/>.</summary>
    public static TimerResolution OneMillisecond() => new(TimeBeginPeriod(1) == 0);

    public void Dispose()
    {
        if (_active)
        {
            _ = TimeEndPeriod(1);
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint milliseconds);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint milliseconds);
}
