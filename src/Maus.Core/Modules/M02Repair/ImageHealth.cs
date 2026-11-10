using Maus.Core.Platform;

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
    /// si DISM ne répond pas (outil absent, maintenance de Windows en cours, délai dépassé).
    /// </summary>
    ImageHealth Check(TimeSpan timeout);
}
