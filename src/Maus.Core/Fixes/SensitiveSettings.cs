using static Maus.Core.Localization.Texts;

namespace Maus.Core.Fixes;

/// <summary>
/// Réglages qui lancent un programme ou portent une stratégie de sécurité : Winlogon, AppInit, IFEO, UAC, Defender, pare-feu,
/// serveur et stratégies de Windows Update, services de mise à jour, protections du noyau suivies par M13.
/// Remettre leur valeur d'origine n'est jamais anodin. La valeur est donc montrée à l'utilisateur avant d'être remise, le
/// moteur la fait confirmer dans sa propre fenêtre, et une annulation groupée la laisse de côté. Une correction ordinaire ne
/// passe pas par là : une question de plus ferait cliquer sans lire.
/// </summary>
public static class SensitiveSettings
{
    private const int LongestValueShown = 300;

    private static readonly string[] WinlogonNames = ["Shell", "Userinit"];
    private static readonly string[] AppInitNames = ["AppInit_DLLs", "LoadAppInit_DLLs"];
    private static readonly string[] DefenceServices =
    [
        @"SYSTEM\CurrentControlSet\Services\WinDefend", @"SYSTEM\CurrentControlSet\Services\mpssvc",
        @"SYSTEM\CurrentControlSet\Services\wuauserv", @"SYSTEM\CurrentControlSet\Services\UsoSvc", @"SYSTEM\CurrentControlSet\Services\WaaSMedicSvc",
        @"SYSTEM\CurrentControlSet\Services\BITS", @"SYSTEM\CurrentControlSet\Services\CryptSvc", @"SYSTEM\CurrentControlSet\Services\TrustedInstaller",
    ];

    private static readonly string[] SmartScreenNames = ["EnableSmartScreen", "ShellSmartScreenLevel"];
    private static readonly string[] UpdateServerNames = ["WUServer", "WUStatusServer", "DoNotConnectToWindowsUpdateInternetLocations"];
    private static readonly string[] AutomaticUpdateNames = ["UseWUServer", "NoAutoUpdate"];
    private static readonly string[] MitigationNames = ["FeatureSettingsOverride", "FeatureSettingsOverrideMask"];

    /// <summary>Ce réglage lance un programme ou porte une stratégie de sécurité.</summary>
    public static bool IsSensitive(SettingKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Kind != SettingKind.Registry || key.Path is not { } path || key.Name is not { } name)
        {
            return false;
        }

        return (path.EndsWith(@"\Windows NT\CurrentVersion\Winlogon", StringComparison.OrdinalIgnoreCase) && IsOneOf(name, WinlogonNames))
            || (path.EndsWith(@"\Windows NT\CurrentVersion\Windows", StringComparison.OrdinalIgnoreCase) && IsOneOf(name, AppInitNames))
            || (path.Contains(@"\Image File Execution Options\", StringComparison.OrdinalIgnoreCase) && name.Equals("Debugger", StringComparison.OrdinalIgnoreCase))
            || path.Equals(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", StringComparison.OrdinalIgnoreCase)
            || IsAtOrUnder(path, @"SOFTWARE\Policies\Microsoft\Windows Defender")
            || IsAtOrUnder(path, @"SOFTWARE\Policies\Microsoft\WindowsFirewall")
            || (path.Equals(@"SOFTWARE\Policies\Microsoft\Windows\System", StringComparison.OrdinalIgnoreCase) && IsOneOf(name, SmartScreenNames))
            || (IsOneOf(path, DefenceServices) && name.Equals("Start", StringComparison.OrdinalIgnoreCase))
            || (path.Equals(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", StringComparison.OrdinalIgnoreCase) && IsOneOf(name, UpdateServerNames))
            || (path.Equals(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", StringComparison.OrdinalIgnoreCase) && IsOneOf(name, AutomaticUpdateNames))
            || (path.Equals(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", StringComparison.OrdinalIgnoreCase) && IsOneOf(name, MitigationNames))
            || (path.Equals(@"SYSTEM\CurrentControlSet\Control\CI\Config", StringComparison.OrdinalIgnoreCase) && name.Equals("VulnerableDriverBlocklistEnable", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>L'annuler remettrait une valeur dans un réglage sensible (retirer une valeur que MAUS a ajoutée n'en remet aucune).</summary>
    public static bool IsSensitive(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Before is not null && IsSensitive(entry.Key);
    }

    /// <summary>Valeurs sensibles encore à annuler dans une séance, ou dans l'une de ses corrections.</summary>
    public static IReadOnlyList<JournalEntry> Pending(JournalSession session, string? changeId = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Entries.Where(e => e.State.IsOutstanding() && (changeId is null || e.ChangeId == changeId) && IsSensitive(e)).ToList();
    }

    /// <summary>Corrections qu'une annulation groupée peut défaire : aucune valeur sensible à remettre. La plus récente d'abord.</summary>
    public static IReadOnlyList<string> OrdinaryChanges(JournalSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var sensitive = Pending(session).Select(e => e.ChangeId).ToHashSet(StringComparer.Ordinal);
        return Enumerable.Reverse(session.Entries)
            .Where(e => e.State.IsOutstanding() && !sensitive.Contains(e.ChangeId))
            .Select(e => e.ChangeId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Texte de la question : chaque réglage avec la valeur qui y serait remise.</summary>
    public static string Question(IEnumerable<JournalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var lines = entries.Select(e => "• " + Shown(e.Key.Describe()) + " : " + Shown(SettingValue.Display(e.Before)));
        return T("Ces valeurs d'origine seraient remises dans des réglages qui lancent un programme ou protègent Windows. L'une d'elles peut être justement ce que la correction avait retiré :")
            + Environment.NewLine + string.Join(Environment.NewLine, lines) + Environment.NewLine
            + T("Si une valeur ne vous dit rien, répondez Non.");
    }

    /// <summary>
    /// Texte lu sur le PC, tel qu'il peut s'afficher dans une question : sur une ligne, sans caractère invisible qui changerait
    /// sa lecture, et de longueur bornée.
    /// </summary>
    public static string Shown(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clean = new string(text.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? ' ' : c).ToArray());
        return clean.Length <= LongestValueShown ? clean : clean[..LongestValueShown] + "…";
    }

    private static bool IsOneOf(string text, string[] list) => list.Contains(text, StringComparer.OrdinalIgnoreCase);

    private static bool IsAtOrUnder(string path, string key) =>
        path.Equals(key, StringComparison.OrdinalIgnoreCase) || path.StartsWith(key + @"\", StringComparison.OrdinalIgnoreCase);
}
