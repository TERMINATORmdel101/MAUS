using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Fixes;

/// <summary>État de la protection du système (restauration) sur le lecteur Windows.</summary>
public enum ProtectionState
{
    Unknown,
    Enabled,
    Disabled,

    /// <summary>Coupée par une stratégie (<c>DisableSR</c>) : MAUS ne la réactive pas.</summary>
    DisabledByPolicy,
}

/// <summary>Accès bas niveau à la Restauration du système (WMI <c>root\default:SystemRestore</c>).</summary>
public interface ISystemRestore
{
    ProtectionState GetProtectionState(string drive);

    /// <summary>Active la protection du système sur le lecteur (par exemple « C:\ »).</summary>
    void EnableProtection(string drive);

    IReadOnlyList<RestorePointInfo> ListRestorePoints();

    /// <summary>Demande la création d'un point (<c>MODIFY_SETTINGS</c>, <c>BEGIN_SYSTEM_CHANGE</c>). Le succès se vérifie en relisant la liste.</summary>
    void CreateRestorePoint(string description);
}

public enum RestorePointStatus
{
    Created,
    ProtectionDisabled,
    Failed,
}

public sealed record RestorePointOutcome(RestorePointStatus Status, RestorePointInfo? Point, string Message)
{
    public bool Succeeded => Status == RestorePointStatus.Created;
}

/// <summary>
/// Crée un point de restauration et vérifie qu'il existe vraiment. <c>CreateRestorePoint</c> renvoie un succès sans rien créer
/// si un point a moins de 24 heures : MAUS pose <c>SystemRestorePointCreationFrequency</c> = 0 le temps de l'appel,
/// remet la valeur d'origine, puis relit la liste des points.
/// </summary>
public sealed class RestorePointCreator(ISystemRestore restore, ISettingsAccessor settings, string systemDrive = @"C:\")
{
    public static readonly SettingKey FrequencyKey = SettingKey.Registry(
        "HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "SystemRestorePointCreationFrequency");

    public RestorePointOutcome Create(string description, bool enableProtectionIfNeeded)
    {
        switch (restore.GetProtectionState(systemDrive))
        {
            case ProtectionState.DisabledByPolicy:
                return new(RestorePointStatus.ProtectionDisabled, null,
                    T("La protection du système est coupée par une stratégie (PC géré ou réglage d'entreprise) : MAUS ne peut pas créer de point de restauration."));
            case ProtectionState.Disabled when !enableProtectionIfNeeded:
                return new(RestorePointStatus.ProtectionDisabled, null,
                    T("La protection du système est désactivée sur {0} : aucun point de restauration possible sans l'activer.", systemDrive));
            case ProtectionState.Disabled:
                try
                {
                    restore.EnableProtection(systemDrive);
                }
                catch (Exception ex) when (ex is MausAccessDeniedException or InvalidOperationException or DataSourceUnavailableException)
                {
                    return new(RestorePointStatus.Failed, null, T("Activation de la protection du système impossible : {0}", ex.Message));
                }

                break;
        }

        try
        {
            return CreateAndVerify(description);
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or InvalidOperationException or DataSourceUnavailableException)
        {
            return new(RestorePointStatus.Failed, null, T("Création du point de restauration impossible : {0}", ex.Message));
        }
    }

    /// <summary>
    /// Une création à la fois dans MAUS (corrections et suppression d'un pilote) : chacune lit la fréquence d'origine, pose 0
    /// puis remet la valeur lue. Deux créations mêlées pourraient laisser 0 pour de bon.
    /// </summary>
    private static readonly Lock Gate = new();

    private RestorePointOutcome CreateAndVerify(string description)
    {
        lock (Gate)
        {
            return CreateAndVerifyAlone(description);
        }
    }

    private RestorePointOutcome CreateAndVerifyAlone(string description)
    {
        var lastBefore = restore.ListRestorePoints().Select(p => p.SequenceNumber).DefaultIfEmpty(0).Max();
        var originalFrequency = settings.Read(FrequencyKey);
        settings.Write(FrequencyKey, SettingValue.Dword(0));
        try
        {
            restore.CreateRestorePoint(description);
        }
        finally
        {
            settings.Write(FrequencyKey, originalFrequency);
        }

        var created = restore.ListRestorePoints()
            .Where(p => p.SequenceNumber > lastBefore)
            .OrderByDescending(p => p.SequenceNumber)
            .FirstOrDefault();

        return created is null
            ? new(RestorePointStatus.Failed, null, T("Windows a répondu sans créer de point de restauration (vérifié en relisant la liste des points)."))
            : new(RestorePointStatus.Created, created, T("Point de restauration n° {0} créé et vérifié.", created.SequenceNumber));
    }
}
