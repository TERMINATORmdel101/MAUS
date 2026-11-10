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

    /// <summary>Protection du système coupée : l'utilisateur peut l'activer.</summary>
    ProtectionDisabled,

    /// <summary>Coupée par une stratégie (PC géré) : MAUS ne propose pas de l'activer.</summary>
    ProtectionDisabledByPolicy,
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
/// <param name="memo">Garde la fréquence d'origine sur disque pendant qu'elle vaut 0 ; <c>null</c> : rien n'est gardé.</param>
/// <param name="callTimeout">Attente maximale d'un appel à la Restauration du système (remplaçable pour les tests).</param>
/// <param name="listWait">Attente maximale de l'apparition du point dans la liste ; par défaut, seulement avec le vrai Windows.</param>
public sealed class RestorePointCreator(ISystemRestore restore, ISettingsAccessor settings, string? systemDrive = null, IRestoreFrequencyMemo? memo = null, TimeSpan? callTimeout = null, TimeSpan? listWait = null)
{
    /// <summary>Lecteur où Windows est installé.</summary>
    public static string SystemDrive => Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    /// <summary>
    /// WMI n'a pas d'annulation et le moteur attend ici ses autres demandes : l'attente est donc bornée. Choisi par MAUS, large
    /// devant la durée habituelle d'une création, pour ne pas déclarer en échec un disque simplement lent.
    /// </summary>
    private static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromMinutes(10);

    private readonly string _systemDrive = systemDrive ?? SystemDrive;
    private readonly TimeSpan _callTimeout = callTimeout ?? DefaultCallTimeout;

    /// <summary>Observé le 2/11/2026 : Windows répond à la création, et le point n'entre dans la liste que quelques secondes plus tard.</summary>
    private readonly TimeSpan _listWait = listWait ?? (restore is WmiSystemRestore ? TimeSpan.FromSeconds(90) : TimeSpan.Zero);

    public static readonly SettingKey FrequencyKey = SettingKey.Registry(
        "HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "SystemRestorePointCreationFrequency");

    public RestorePointOutcome Create(string description, bool enableProtectionIfNeeded)
    {
        try
        {
            return CreateChecked(description, enableProtectionIfNeeded);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // WMI peut lever bien des erreurs : l'utilisateur garde le choix de continuer sans point de restauration.
            return new(RestorePointStatus.Failed, null, T("Création du point de restauration impossible : {0}", ex.Message));
        }
    }

    private RestorePointOutcome CreateChecked(string description, bool enableProtectionIfNeeded)
    {
        switch (restore.GetProtectionState(_systemDrive))
        {
            case ProtectionState.DisabledByPolicy:
                return new(RestorePointStatus.ProtectionDisabledByPolicy, null,
                    T("La protection du système est coupée par une stratégie (PC géré ou réglage d'entreprise) : MAUS ne peut pas créer de point de restauration."));
            case ProtectionState.Disabled when !enableProtectionIfNeeded:
                return new(RestorePointStatus.ProtectionDisabled, null,
                    T("La protection du système est désactivée sur {0} : aucun point de restauration possible sans l'activer.", _systemDrive));
            case ProtectionState.Disabled:
                try
                {
                    Bounded(() =>
                    {
                        restore.EnableProtection(_systemDrive);
                        return true;
                    });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return new(RestorePointStatus.Failed, null, T("Activation de la protection du système impossible : {0}", ex.Message));
                }

                break;
        }

        return CreateAndVerify(description);
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
            var lastBefore = Bounded(restore.ListRestorePoints).Select(p => p.SequenceNumber).DefaultIfEmpty(0).Max();
            var originalFrequency = OriginalFrequency();
            Remember(originalFrequency);
            settings.Write(FrequencyKey, SettingValue.Dword(0));
            string? notPutBack = null;
            RestorePointOutcome outcome;
            try
            {
                try
                {
                    Bounded(() =>
                    {
                        restore.CreateRestorePoint(description);
                        return true;
                    });
                }
                finally
                {
                    notPutBack = PutBackFrequency(originalFrequency);
                }

                var created = FindCreated(lastBefore);

                outcome = created is null
                    ? new(RestorePointStatus.Failed, null, T("Windows a répondu sans créer de point de restauration (vérifié en relisant la liste des points)."))
                    : new(RestorePointStatus.Created, created, T("Point de restauration n° {0} créé et vérifié.", created.SequenceNumber));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                outcome = new(RestorePointStatus.Failed, null, T("Création du point de restauration impossible : {0}", ex.Message));
            }

            return notPutBack is null
                ? outcome
                : outcome with { Message = outcome.Message + " " + T("Le réglage de fréquence des points de restauration n'a pas pu être remis à sa valeur d'origine ({0}) : il reste à 0.", notPutBack) };
        }
    }

    /// <summary>Le point créé, relu jusqu'à ce qu'il apparaisse ; <c>null</c> si Windows ne l'a pas écrit après l'attente.</summary>
    private RestorePointInfo? FindCreated(long lastBefore)
    {
        var deadline = DateTime.UtcNow + _listWait;
        while (true)
        {
            var created = Bounded(restore.ListRestorePoints)
                .Where(p => p.SequenceNumber > lastBefore)
                .OrderByDescending(p => p.SequenceNumber)
                .FirstOrDefault();
            if (created is not null || DateTime.UtcNow >= deadline)
            {
                return created;
            }

            Thread.Sleep(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Remet la fréquence d'origine. Un échec ne doit pas faire croire que le point n'existe pas : il rend le motif.</summary>
    private string? PutBackFrequency(SettingValue? original)
    {
        try
        {
            settings.Write(FrequencyKey, original);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Le fichier reste : la prochaine création retrouvera la vraie valeur d'origine.
            return ex.Message;
        }

        Forget();
        return null;
    }

    /// <summary>
    /// La fréquence à remettre après la création. Si MAUS a été coupé pendant une création précédente, le 0 encore en place n'est
    /// pas la valeur d'origine : c'est celle gardée sur disque qui compte. Une valeur changée depuis par quelqu'un d'autre reste.
    /// </summary>
    private SettingValue? OriginalFrequency()
    {
        var current = settings.Read(FrequencyKey);
        try
        {
            return memo?.Recall() is { } interrupted && SettingValue.AreEquivalent(current, SettingValue.Dword(0)) ? interrupted.Value : current;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Diagnostics.Breadcrumbs.Add("point de restauration : fréquence gardée illisible (" + ex.GetType().Name + ")");
            return current;
        }
    }

    /// <summary>Un fichier impossible à écrire n'empêche pas de créer le point : il protège seulement d'un arrêt brutal.</summary>
    private void Remember(SettingValue? original)
    {
        try
        {
            memo?.Remember(original);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Diagnostics.Breadcrumbs.Add("point de restauration : fréquence non gardée (" + ex.GetType().Name + ")");
        }
    }

    private void Forget()
    {
        try
        {
            memo?.Forget();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Diagnostics.Breadcrumbs.Add("point de restauration : fichier de la fréquence non retiré (" + ex.GetType().Name + ")");
        }
    }

    /// <summary>
    /// Appel à la Restauration du système, à attente bornée. Au-delà, l'appel continue seul sur son fil : Windows peut encore finir
    /// le point, ce qui n'est pas grave, et la main revient à l'utilisateur, qui garde le choix de continuer sans point.
    /// </summary>
    /// <exception cref="TimeoutException">Pas de réponse dans le délai.</exception>
    private TResult Bounded<TResult>(Func<TResult> call)
    {
        var task = Task.Run(call);
        try
        {
            if (task.Wait(_callTimeout))
            {
                return task.Result;
            }
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }

        // Une erreur tardive de l'appel abandonné ne doit pas remonter en « tâche jamais attendue ».
        _ = task.ContinueWith(t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        throw new TimeoutException(T("La Restauration du système n'a pas répondu en {0:0} minutes.", _callTimeout.TotalMinutes));
    }
}

/// <summary>Valeur gardée de la fréquence ; <see cref="Value"/> est <c>null</c> si la valeur était absente du registre.</summary>
public sealed record RememberedFrequency(SettingValue? Value);

/// <summary>
/// Mémoire de la fréquence d'origine, tenue le temps où le registre vaut 0 : MAUS coupé à ce moment-là (plantage, arrêt du PC) ne
/// laisse pas ce 0 passer pour la valeur d'origine lors de la création suivante.
/// </summary>
public interface IRestoreFrequencyMemo
{
    void Remember(SettingValue? original);

    /// <returns>La valeur laissée par une création interrompue, ou <c>null</c> s'il n'y en a pas.</returns>
    RememberedFrequency? Recall();

    void Forget();
}

/// <summary>Un petit fichier dans le dossier protégé du journal (son extension le tient à l'écart des séances, en <c>.json</c>).</summary>
public sealed class FileRestoreFrequencyMemo(string directory, IDirectoryProtector protector) : IRestoreFrequencyMemo
{
    private string FilePath => Path.Combine(directory, "restore-frequency.memo");

    public void Remember(SettingValue? original)
    {
        protector.EnsureProtected(directory);
        AtomicFile.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(new RememberedFrequency(original), FileJournalStore.JsonOptions), protector.ProtectFile);
    }

    public RememberedFrequency? Recall()
    {
        StoredFileRead read;
        try
        {
            read = protector.EnsureReadable(directory) ? AtomicFile.ReadText(FilePath, protector.IsTrusted) : new StoredFileRead(StoredFileState.Absent);
        }
        catch (JournalUnsafeException)
        {
            return null;
        }

        if (read.State != StoredFileState.Read)
        {
            return null;
        }

        try
        {
            var kept = System.Text.Json.JsonSerializer.Deserialize<RememberedFrequency>(read.Text!, FileJournalStore.JsonOptions);

            // Ce réglage est un DWORD (ou absent) : autre chose ne vient pas de MAUS.
            return kept is { Value: null or { Kind: Microsoft.Win32.RegistryValueKind.DWord } } ? kept : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public void Forget() => File.Delete(FilePath);
}
