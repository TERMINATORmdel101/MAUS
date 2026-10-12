using Maus.Core.Preferences;

namespace Maus.Core.Engine.Broker;

/// <summary>
/// Les choix de l'utilisateur vus par l'interface : lus et enregistrés par le moteur (leur fichier est dans un dossier réservé
/// aux administrateurs). Mêmes règles qu'un magasin local : un fichier illisible garde les choix en mémoire.
/// </summary>
public sealed class EnginePreferencesStore(IMausEngine engine) : IPreferencesStore
{
    private PreferencesState? _last;

    /// <summary>Résultat de la dernière lecture ou du dernier enregistrement.</summary>
    public PreferencesFileState State => _last?.FileState ?? PreferencesFileState.Absent;

    public string? Notice => _last?.Notice;

    public UserPreferences Load()
    {
        _last = Wait(engine.LoadPreferencesAsync(CancellationToken.None));
        return _last.Preferences;
    }

    public void Save(UserPreferences preferences)
    {
        var state = Wait(engine.SavePreferencesAsync(preferences, CancellationToken.None));
        _last = state;
        if (state.NotSaved is { } reason)
        {
            throw new PreferencesNotSavedException(reason, state.Preferences);
        }
    }

    // Appelé hors du fil de l'interface (Task.Run) ou pour une lecture courte : le moteur répond en quelques millisecondes.
    private static T Wait<T>(Task<T> task) => task.ConfigureAwait(false).GetAwaiter().GetResult();
}
