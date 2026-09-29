using Maus.Core.Platform;

namespace Maus.Core.Fixes;

/// <summary>
/// Tout ce dont le moteur de corrections a besoin pour écrire. Séparé d'<see cref="AuditContext"/> :
/// un module en détection n'a structurellement aucun moyen d'écrire.
/// </summary>
public sealed class FixContext
{
    public required AuditContext Audit { get; init; }

    public required ISettingsAccessor Settings { get; init; }

    public required IJournalStore Journal { get; init; }

    public required ISystemRestore SystemRestore { get; init; }

    public ISettingChangeNotifier Notifier { get; init; } = new Win32SettingChangeNotifier();

    /// <summary>MAUS a été élevé avec un autre compte que celui de la session : les réglages du profil sont bloqués.</summary>
    public bool ElevatedAsAnotherUser { get; init; }

    /// <summary>
    /// Horloge des dates du journal (séance, écriture, annulation). Sans elle, l'heure de l'audit : l'interface réutilise le
    /// dernier audit, dont l'heure peut dater de plusieurs heures.
    /// </summary>
    public Func<DateTimeOffset>? Clock { get; init; }

    public static FixContext CreateDefault(AuditContext audit)
    {
        var settings = new SettingsAccessor(audit.Registry, new WindowsRegistryWriter(), audit.SystemParameters, new Win32SystemParametersWriter());
        return new FixContext
        {
            Audit = audit,
            Settings = settings,
            Journal = FileJournalStore.CreateDefault(),
            SystemRestore = new WmiSystemRestore(audit.Registry),
            ElevatedAsAnotherUser = SessionUser.IsElevatedAsAnotherUser(),
            Clock = () => DateTimeOffset.Now,
        };
    }
}
