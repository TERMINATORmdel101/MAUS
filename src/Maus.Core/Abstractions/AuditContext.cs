using Maus.Core.Hardware;
using Maus.Core.Platform;
using Maus.Core.Preferences;

namespace Maus.Core;

/// <summary>Tout ce qu'un module peut lire pendant la détection. Chaque accès passe par une interface pour rester testable.</summary>
public sealed class AuditContext
{
    public required IRegistryReader Registry { get; init; }

    public required ICimReader Cim { get; init; }

    public required ICommandRunner Commands { get; init; }

    public ISystemParametersReader SystemParameters { get; init; } = new Win32SystemParametersReader();

    public IEventLogReader EventLogs { get; init; } = new WindowsEventLogReader();

    public IPackageInventory Packages { get; init; } = new WinRtPackageInventory();

    public IFileSystemReader Files { get; init; } = new LocalFileSystemReader();

    public required WindowsInfo Windows { get; init; }

    public required HardwareProfile Hardware { get; init; }

    public bool IsElevated { get; init; }

    /// <summary>Choix de l'utilisateur (profil Game Bar, alimentation du portable, constats « voulus »).</summary>
    public UserPreferences Preferences { get; init; } = UserPreferences.Default;

    /// <summary>Horloge injectée, pour tester les calculs d'âge (BIOS, pilotes).</summary>
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;

    /// <summary>Construit le contexte réel à partir du système courant.</summary>
    public static AuditContext CreateDefault()
    {
        var registry = new WindowsRegistryReader();
        var cim = new WmiCimReader();
        return new AuditContext
        {
            Registry = registry,
            Cim = cim,
            Commands = new ReadOnlyCommandRunner(),
            Windows = WindowsInfo.Read(registry),
            Hardware = HardwareProfileBuilder.Build(cim, registry),
            IsElevated = ProcessElevation.IsElevated(),
            Preferences = FilePreferencesStore.CreateDefault().Load(),
            Now = DateTimeOffset.Now,
        };
    }

    /// <summary>Même contexte, avec d'autres préférences (après un choix de l'utilisateur).</summary>
    public AuditContext WithPreferences(UserPreferences preferences) => new()
    {
        Registry = Registry,
        Cim = Cim,
        Commands = Commands,
        SystemParameters = SystemParameters,
        EventLogs = EventLogs,
        Packages = Packages,
        Files = Files,
        Windows = Windows,
        Hardware = Hardware,
        IsElevated = IsElevated,
        Preferences = preferences,
        Now = Now,
    };
}
