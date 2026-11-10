namespace Maus.Core.Preferences;

/// <summary>
/// Choix gardés en mémoire seulement : MAUS sans droits administrateur (mode limité) ne peut ni lire ni écrire le fichier
/// des choix, réservé aux administrateurs ; les choix valent jusqu'à la fermeture.
/// </summary>
public sealed class MemoryPreferencesStore : IPreferencesStore
{
    private UserPreferences _value = UserPreferences.Default;

    public UserPreferences Load() => _value;

    public void Save(UserPreferences preferences) => _value = preferences;
}
