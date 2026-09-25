using Maus.Core.Fixes;

namespace Maus.Core.Modules.M09Gpu;

/// <summary>Étape Plan du Module 9 : HAGS, et blocage des pilotes par Windows Update au seul choix de l'utilisateur.</summary>
public sealed partial class GpuDriverModule
{
    public IReadOnlyList<PlannedChange> Plan(AuditContext context, IReadOnlyList<Finding> findings)
    {
        var byId = findings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var changes = new List<PlannedChange>();

        if (byId.TryGetValue("M09.hags", out var hags) && hags.Fixable && hags.Status == FindingStatus.Improvable)
        {
            changes.Add(new PlannedChange
            {
                Id = hags.Id,
                ModuleId = Id,
                Title = "Activer la planification GPU à accélération matérielle (HAGS)",
                Description = "Requise pour DLSS Frame Generation sur votre carte. Valeur HwSchMode = 2, non documentée par Microsoft mais écrite par l'application Paramètres.",
                Category = SettingsCategory,
                Gain = "DLSS Frame Generation devient disponible dans les jeux compatibles.",
                Effect = ChangeEffect.Restart,
                Writes = [new SettingWrite(SettingKey.Registry("HKLM", GraphicsDriversKey, "HwSchMode"), SettingValue.Dword(2))],
            });
        }

        // Décision du projet : les pilotes de Windows Update ne sont jamais bloqués par défaut ; le blocage reste un choix.
        if (byId.TryGetValue("M09.windows-update-drivers", out var updates) && updates.Fixable
            && !(updates.Current?.StartsWith("exclus", StringComparison.Ordinal) ?? false))
        {
            changes.Add(new PlannedChange
            {
                Id = updates.Id,
                ModuleId = Id,
                Title = "Empêcher Windows Update d'installer des pilotes (ExcludeWUDriversInQualityUpdate)",
                Description = "Windows Update ne remplace plus votre pilote graphique installé à la main.",
                Category = UpdateCategory,
                Recommended = false,
                Advanced = true,
                Warning = "Ce blocage vaut pour TOUS les périphériques (Wi-Fi, audio, chipset, et probablement les firmwares des PC de marque) : " +
                          "vous devrez les mettre à jour vous-même.",
                Writes = [new SettingWrite(SettingKey.Registry("HKLM", WindowsUpdatePolicyKey, "ExcludeWUDriversInQualityUpdate"), SettingValue.Dword(1))],
            });
        }

        if (byId.TryGetValue("M09.device-installation-settings", out var device) && device.Fixable
            && !(device.Current?.StartsWith("désactivé", StringComparison.Ordinal) ?? false))
        {
            changes.Add(new PlannedChange
            {
                Id = device.Id,
                ModuleId = Id,
                Title = "Ne plus télécharger automatiquement les pilotes des fabricants (Famille)",
                Description = "Équivaut à répondre « Non » dans « Paramètres d'installation de périphérique ». Effet partiel sur les pilotes graphiques.",
                Category = UpdateCategory,
                Recommended = false,
                Advanced = true,
                Warning = "Ce réglage concerne tous les périphériques, pas seulement la carte graphique.",
                Writes = [new SettingWrite(SettingKey.Registry("HKLM", DriverSearchingKey, "SearchOrderConfig"), SettingValue.Dword(0))],
            });
        }

        return changes;
    }
}
