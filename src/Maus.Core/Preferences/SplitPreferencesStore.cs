using Maus.Core.Fixes;

namespace Maus.Core.Preferences;

/// <summary>
/// Choix qui guident l'audit et les corrections, gardés dans le dossier réservé aux administrateurs : un programme sans
/// droits ne doit pas pouvoir marquer « voulu » un antivirus coupé, ni changer le profil Game Bar appliqué par MAUS.
/// </summary>
public interface IMachinePreferences
{
    /// <summary>Lisibles sans rien demander (moteur administrateur en marche, ou MAUS lancé en administrateur).</summary>
    bool IsAvailable { get; }

    /// <summary>Lit les choix protégés ; peut demander l'autorisation administrateur si <see cref="IsAvailable"/> est faux.</summary>
    UserPreferences Load();

    /// <summary>Enregistre les choix protégés ; peut demander l'autorisation administrateur.</summary>
    void Save(UserPreferences preferences);
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
    private readonly Lock _gate = new();

    /// <summary>Choix protégés lus par le moteur ; <c>null</c> tant qu'il n'a pas tourné.</summary>
    private UserPreferences? _machine;

    /// <summary>Fichier de l'utilisateur absent à la première lecture : il est rempli des choix d'affichage du fichier protégé.</summary>
    private bool _importUserChoices;

    public string? Notice => user.Notice;

    /// <summary>État du fichier de l'utilisateur (celui qui décide de l'affichage).</summary>
    public PreferencesFileState State => user is FilePreferencesStore files ? files.State : PreferencesFileState.Read;

    public UserPreferences Load()
    {
        lock (_gate)
        {
            var own = user.Load();
            if (State == PreferencesFileState.Absent && _machine is null)
            {
                _importUserChoices = true;
            }

            if (machine.IsAvailable)
            {
                Refresh();
            }

            if (_machine is not { } protectedChoices)
            {
                return own;
            }

            if (_importUserChoices)
            {
                // Première ouverture depuis la 0.8.1 : les choix d'affichage étaient tous dans le fichier protégé. Un accord
                // donné aux avertissements entre-temps est gardé.
                _importUserChoices = false;
                own = protectedChoices with { DisclaimerAccepted = Math.Max(own.DisclaimerAccepted, protectedChoices.DisclaimerAccepted) };
                TrySaveUser(own);
            }

            return WithMachineChoices(own, protectedChoices);
        }
    }

    public void Save(UserPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_gate)
        {
            // Les choix protégés ne s'écrivent que s'ils changent (autorisation demandée au besoin) ; moteur déjà en marche :
            // tout est recopié, pour que l'audit de la semaine parle la même langue que l'interface.
            var baseline = _machine ?? user.Load();
            user.Save(preferences);
            if (machine.IsAvailable || !SameMachineChoices(preferences, baseline))
            {
                machine.Save(preferences);
                _machine = preferences;
            }
        }
    }

    /// <summary>Relit les choix protégés (le moteur vient de démarrer).</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            _machine = machine.Load();
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
    };

    private static bool SameMachineChoices(UserPreferences a, UserPreferences b) =>
        a.GameBarProfile == b.GameBarProfile && a.LaptopPower == b.LaptopPower && a.Acknowledged.SequenceEqual(b.Acknowledged);
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
