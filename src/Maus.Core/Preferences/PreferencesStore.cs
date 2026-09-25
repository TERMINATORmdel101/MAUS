using System.Text.Json;
using Maus.Core.Fixes;

namespace Maus.Core.Preferences;

public interface IPreferencesStore
{
    /// <summary>Préférences enregistrées, ou valeurs par défaut si le fichier est absent, illisible ou non fiable.</summary>
    UserPreferences Load();

    /// <exception cref="JournalUnsafeException">Le dossier ne peut pas être protégé.</exception>
    void Save(UserPreferences preferences);
}

/// <summary>
/// Préférences sous <c>%ProgramData%\MAUS\settings</c>, protégées comme le journal : un programme lancé sans droits
/// administrateur ne doit pas pouvoir marquer « voulu » un antivirus coupé pour le cacher à MAUS.
/// </summary>
public sealed class FilePreferencesStore(string directory, IDirectoryProtector protector) : IPreferencesStore
{
    private const string FileName = "preferences.json";

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MAUS", "settings");

    public static FilePreferencesStore CreateDefault() => new(DefaultDirectory, new WindowsDirectoryProtector());

    public UserPreferences Load()
    {
        var path = Path.Combine(directory, FileName);
        try
        {
            if (!File.Exists(path) || !protector.IsTrusted(path))
            {
                return UserPreferences.Default;
            }

            return JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(path), FileJournalStore.JsonOptions) ?? UserPreferences.Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return UserPreferences.Default;
        }
    }

    public void Save(UserPreferences preferences)
    {
        protector.EnsureProtected(directory);
        var path = Path.Combine(directory, FileName);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, FileJournalStore.JsonOptions));
        protector.ProtectFile(temporary);
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>Applique les marques « voulu » aux résultats d'un module.</summary>
public static class Acknowledgements
{
    public static ModuleResult Apply(ModuleResult result, UserPreferences preferences)
    {
        if (preferences.Acknowledged.Count == 0)
        {
            return result;
        }

        var findings = result.Findings.Select(finding =>
            finding.Status is FindingStatus.Improvable or FindingStatus.Warning or FindingStatus.Problem
            && preferences.AcknowledgementFor(finding) is { } mark
                ? finding with
                {
                    Status = FindingStatus.Info,
                    AcknowledgedFrom = finding.Status,
                    Fixable = false,
                    Advice = $"Marqué « voulu » par vous le {mark.At.ToLocalTime():dd/MM/yyyy}. MAUS le signalera de nouveau si la situation change.",
                }
                : finding).ToList();
        return result with { Findings = findings };
    }
}
