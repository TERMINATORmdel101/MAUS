using System.Runtime.InteropServices;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M02Repair;

/// <summary>État du magasin des composants de Windows (<c>DismImageHealthState</c>).</summary>
public enum ImageHealth
{
    Healthy = 0,

    /// <summary>Altération signalée, réparable par <c>DISM /RestoreHealth</c>.</summary>
    Repairable = 1,

    /// <summary>Altération que DISM ne sait pas réparer : réinstallation sur place.</summary>
    NotRepairable = 2,
}

/// <summary>Équivalent de <c>DISM /Online /Cleanup-Image /CheckHealth</c> : lit le signalement d'altération, sans analyser ni réparer.</summary>
public interface IImageHealthChecker
{
    /// <summary>
    /// Lève <see cref="MausAccessDeniedException"/> sans droits administrateur et <see cref="DataSourceUnavailableException"/>
    /// si DISM ne répond pas (API absente, maintenance de Windows en cours, délai dépassé).
    /// </summary>
    ImageHealth Check(TimeSpan timeout);
}

/// <summary>
/// Lecture par l'API DISM (<c>dismapi.dll</c>) : <c>DismCheckImageHealth</c> avec <c>ScanImage = FALSE</c> ne fait que lire
/// l'indicateur d'altération enregistré par Windows (quelques secondes). L'analyse complète (<c>/ScanHealth</c>) et la
/// réparation (<c>/RestoreHealth</c>) ne sont jamais lancées ici.
/// </summary>
public sealed partial class DismImageHealthChecker : IImageHealthChecker
{
    private const string OnlineImage = "DISM_{53BFAE52-B167-4E2F-A258-0A37B57FF845}";
    private const int LogErrors = 0;
    private const int ElevationRequired = unchecked((int)0x800702E4);
    private const int Cancelled = unchecked((int)0x800704C7);

    /// <summary>L'API DISM ne s'initialise qu'une fois par processus à la fois.</summary>
    private static readonly Lock Gate = new();

    public ImageHealth Check(TimeSpan timeout)
    {
        using var cancel = new EventWaitHandle(false, EventResetMode.ManualReset);
        using var timer = new Timer(_ => cancel.Set(), null, timeout, Timeout.InfiniteTimeSpan);
        lock (Gate)
        {
            try
            {
                return CheckLocked(cancel);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                throw new DataSourceUnavailableException(T("API DISM introuvable."), ex);
            }
        }
    }

    private static ImageHealth CheckLocked(EventWaitHandle cancel)
    {
        Throw(DismInitialize(LogErrors, null, null), "DismInitialize");
        try
        {
            Throw(DismOpenSession(OnlineImage, null, null, out var session), "DismOpenSession");
            try
            {
                var result = DismCheckImageHealth(session, 0, cancel.SafeWaitHandle.DangerousGetHandle(), 0, 0, out var health);
                if (result == Cancelled)
                {
                    throw new DataSourceUnavailableException(T("DISM n'a pas répondu à temps : une maintenance de Windows est peut-être en cours."));
                }

                Throw(result, "DismCheckImageHealth");
                return Enum.IsDefined((ImageHealth)health)
                    ? (ImageHealth)health
                    : throw new DataSourceUnavailableException(T("Réponse de DISM inconnue ({0}).", health));
            }
            finally
            {
                _ = DismCloseSession(session);
            }
        }
        finally
        {
            _ = DismShutdown();
        }
    }

    private static void Throw(int hresult, string function)
    {
        if (hresult == ElevationRequired)
        {
            throw new MausAccessDeniedException(T("DISM demande les droits administrateur."));
        }

        if (hresult < 0)
        {
            throw new DataSourceUnavailableException(T("{0} a renvoyé le code 0x{1:X8}.", function, hresult));
        }
    }

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DismInitialize(int logLevel, string? logFilePath, string? scratchDirectory);

    [LibraryImport("dismapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DismOpenSession(string imagePath, string? windowsDirectory, string? systemDrive, out uint session);

    [LibraryImport("dismapi.dll")]
    private static partial int DismCheckImageHealth(uint session, int scanImage, nint cancelEvent, nint progress, nint userData, out int imageHealth);

    [LibraryImport("dismapi.dll")]
    private static partial int DismCloseSession(uint session);

    [LibraryImport("dismapi.dll")]
    private static partial int DismShutdown();
}
