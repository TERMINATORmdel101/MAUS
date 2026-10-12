using Maus.Core.Engine.Broker;

namespace Maus.App;

/// <summary>
/// Le moteur de MAUS pour toute l'interface : en 0.8.0, le processus « moteur » administrateur, joint par un canal privé ;
/// à défaut (pas encore lancé), le moteur dans ce processus.
/// </summary>
public static class EngineHost
{
    private static IMausEngine? s_current;

    public static IMausEngine Current
    {
        get => s_current ??= new LocalMausEngine();
        set => s_current = value;
    }
}
