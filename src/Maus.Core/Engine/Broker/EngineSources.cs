using Maus.Core.Workshop;

namespace Maus.Core.Engine.Broker;

/// <summary>
/// Capteurs lus par le moteur (PawnIO, compteurs de Windows) : l'interface s'en sert comme d'une source ordinaire. Appelé hors
/// du fil de l'interface, comme les autres sources.
/// </summary>
public sealed class EngineSensorSource(IMausEngine engine) : ISensorSource
{
    /// <summary>État du pilote PawnIO à la dernière mesure, ou <c>null</c> avant la première.</summary>
    public SensorState? State { get; private set; }

    public SensorSnapshot Sample()
    {
        var reading = engine.SampleSensorsAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
        State = reading.State;
        return reading.Snapshot;
    }

    public void Dispose()
    {
        // Les capteurs appartiennent au moteur, qui les rend à Windows en s'arrêtant.
    }
}

/// <summary>Processus lus par le moteur (tous les comptes, chemins complets), pour la liste de l'Atelier.</summary>
public sealed class EngineProcessSource(IMausEngine engine) : IProcessSource
{
    private IReadOnlyDictionary<int, double> _gpu = new Dictionary<int, double>();

    public IReadOnlyList<RawProcess> Read()
    {
        var reading = engine.ReadProcessesAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
        _gpu = reading.GpuPercent;
        return reading.Processes;
    }

    /// <summary>Charge de la carte graphique par processus, lue avec la dernière liste.</summary>
    public IReadOnlyDictionary<int, double> GpuPercentByPid() => _gpu;
}
