namespace Maus.Core.Preferences;

/// <summary>
/// Choix gardés en mémoire seulement : MAUS sans droits administrateur (mode limité) ne peut ni lire ni écrire le fichier
/// des choix, réservé aux administrateurs ; les choix valent jusqu'à la fermeture.
/// </summary>
public sealed class MemoryPreferencesStore(UserPreferences? initial = null) : IPreferencesStore
{
    private UserPreferences _value = initial ?? UserPreferences.Default;

    public UserPreferences Load() => _value;

    public void Save(UserPreferences preferences) => _value = preferences;
}
