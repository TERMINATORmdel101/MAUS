using Maus.Core.Fixes;

namespace Maus.Core.Preferences;

/// <summary>
/// Choix qui guident l'audit et les corrections, gardés dans le dossier réservé aux administrateurs : un programme sans
/// droits ne doit pas pouvoir marquer « voulu » un antivirus coupé, ni changer le profil Game Bar appliqué par MAUS.
/// </summary>
public interface IMachinePreferences
{
    /// <summary>Message d'état du fichier protégé (illisible, ancien…) ; rien si tout va bien.</summary>
    string? Notice => null;

    /// <summary>Lisibles sans rien demander (moteur administrateur en marche, ou MAUS lancé en administrateur).</summary>
    bool IsAvailable { get; }

    /// <summary>Lit les choix protégés ; peut demander l'autorisation administrateur si <see cref="IsAvailable"/> est faux.</summary>
    UserPreferences Load();

    /// <summary>Enregistre les choix protégés ; peut demander l'autorisation administrateur.</summary>
    void Save(UserPreferences preferences);

    /// <summary>Recopie des choix protégés inchangés : jamais d'autorisation, rien n'est recopié si le moteur vient de s'arrêter.</summary>
    void CopyIfRunning(UserPreferences preferences) => Save(preferences);
}

/// <summary>
/// Les choix vus d'une interface sans droits administrateur (0.8.1) : ceux de l'affichage (langue, thème, compteur d'images,
/// moteur de recherche, avertissements acceptés…) dans un fichier de l'utilisateur, lu et écrit sans droits ; ceux qui
/// guident l'audit et les corrections (constats « voulus », profil Game Bar, alimentation du portable) seulement par le
/// moteur administrateur. L'ouverture de MAUS ne demande donc jamais d'autorisation : les choix protégés sont lus dès que le
/// moteur tourne, et un changement de l'un d'eux demande l'autorisation.
/// </summary>
/// <param name="user">Fichier de l'utilisateur (<c>%LOCALAPPDATA%\MAUS\settings</c>).</param>
/// <param name="machine">Choix protégés, par le moteur administrateur.</param>
public sealed class SplitPreferencesStore(IPreferencesStore user, IMachinePreferences machine) : IPreferencesStore
{
    /// <summary>
    /// Protège <see cref="_machine"/>, et rien d'autre : jamais tenu pendant un appel au moteur, qui peut attendre un « Oui »
    /// dans sa fenêtre. Le fil de l'interface lit ainsi les choix sans attendre.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>Un enregistrement à la fois : les choix protégés et ceux de l'utilisateur restent dans le même ordre.</summary>
    private readonly Lock _saves = new();

    /// <summary>Choix protégés lus par le moteur ; <c>null</c> tant qu'il n'a pas tourné.</summary>
    private UserPreferences? _machine;

    /// <summary>Nombre d'enregistrements : une lecture commencée avant l'un d'eux ne l'écrase pas.</summary>
    private int _saved;

    public string? Notice => user.Notice ?? machine.Notice;

    /// <summary>État du fichier de l'utilisateur (celui qui décide de l'affichage).</summary>
    public PreferencesFileState State => user is FilePreferencesStore files ? files.State : PreferencesFileState.Read;

    /// <summary>
    /// Le moteur n'est appelé que si ses choix ne sont pas encore connus : il est seul à les écrire, par <see cref="Save"/>, et
    /// <see cref="Refresh"/> les relit à chaque démarrage du moteur.
    /// </summary>
    public UserPreferences Load()
    {
        var own = user.Load();
        if (Known() is null && machine.IsAvailable)
        {
            try
            {
                Refresh();
            }
            catch (Exception ex) when (ex is Engine.Broker.EngineException or IOException or UnauthorizedAccessException)
            {
                // Moteur injoignable : les derniers choix protégés connus restent valables pour cette lecture.
                Diagnostics.Breadcrumbs.Add("choix protégés illisibles (" + ex.GetType().Name + ")");
            }
        }

        return Known() is { } protectedChoices ? WithMachineChoices(own, protectedChoices) : own;
    }

    public void Save(UserPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_saves)
        {
            // Les choix protégés ne s'écrivent que s'ils changent (autorisation demandée au besoin) ; moteur déjà en marche :
            // tout est recopié, pour que l'audit de la semaine parle la même langue que l'interface.
            var baseline = Known() ?? user.Load();
            var unchanged = SameMachineChoices(preferences, baseline);
            if (machine.IsAvailable || !unchanged)
            {
                try
                {
                    if (unchanged)
                    {
                        machine.CopyIfRunning(preferences);
                    }
                    else
                    {
                        machine.Save(preferences);
                    }
                }
                catch
                {
                    // Choix protégé refusé (confirmation déclinée, autorisation refusée) : il ne doit pas rester dans le fichier
                    // de l'utilisateur ; les choix d'affichage, eux, sont gardés.
                    TrySaveUser(WithMachineChoices(preferences, baseline));
                    throw;
                }

                lock (_gate)
                {
                    _machine = preferences;
                    _saved++;
                }
            }

            user.Save(preferences);
        }
    }

    /// <summary>Relit les choix protégés (le moteur vient de démarrer).</summary>
    public void Refresh()
    {
        int saved;
        lock (_gate)
        {
            saved = _saved;
        }

        var loaded = machine.Load();
        lock (_gate)
        {
            if (saved == _saved)
            {
                _machine = loaded;
            }
        }
    }

    private UserPreferences? Known()
    {
        lock (_gate)
        {
            return _machine;
        }
    }

    private void TrySaveUser(UserPreferences preferences)
    {
        try
        {
            user.Save(preferences);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JournalUnsafeException)
        {
            // Recopie manquée : elle sera refaite au prochain lancement.
        }
    }

    private static UserPreferences WithMachineChoices(UserPreferences display, UserPreferences protectedChoices) => display with
    {
        GameBarProfile = protectedChoices.GameBarProfile,
        LaptopPower = protectedChoices.LaptopPower,
        Acknowledged = protectedChoices.Acknowledged,
        WeeklyNotification = protectedChoices.WeeklyNotification,
        SkippedModules = protectedChoices.SkippedModules,
    };

    private static bool SameMachineChoices(UserPreferences a, UserPreferences b) =>
        a.GameBarProfile == b.GameBarProfile && a.LaptopPower == b.LaptopPower && a.Acknowledged.SequenceEqual(b.Acknowledged) && a.WeeklyNotification == b.WeeklyNotification && a.SkippedModules.SequenceEqual(b.SkippedModules, StringComparer.Ordinal);
}

/// <summary>
/// Dossier de l'utilisateur (<c>%LOCALAPPDATA%\MAUS\settings</c>) : il lui appartient déjà, rien à protéger ; un lien
/// symbolique y est refusé, comme ailleurs.
/// </summary>
public sealed class UserDirectoryProtector : IDirectoryProtector
{
    public void EnsureProtected(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new JournalUnsafeException(Localization.Texts.T("Le dossier {0} est un lien : MAUS refuse de s'en servir. Supprimez-le puis relancez MAUS.", directory));
        }

        info.Create();
    }

    public bool IsTrusted(string file) => !new FileInfo(file).Attributes.HasFlag(FileAttributes.ReparsePoint);

    public void ProtectFile(string file)
    {
        // Fichier de l'utilisateur, dans son propre dossier : il en est déjà le propriétaire.
    }
}
