namespace Maus.Core.Workshop;

/// <summary>
/// La règle de température, en un seul endroit : alarmes en direct, diagnostic de lenteur, enregistrement d'une session, arrêt
/// des tests et arrêt de sécurité du benchmark comparent tous une mesure de la même façon.
/// </summary>
public static class ThermalRules
{
    /// <summary>En dessous de sa limite de ce nombre de degrés au plus, le processeur est dit à sa limite.</summary>
    private const int CpuAtLimitMarginC = 3;

    /// <summary>
    /// Au-delà de sa limite de ce nombre de degrés, le processeur est en danger : à sa limite il ralentit de lui-même, et certains
    /// modèles récents y montent volontairement en pleine charge ; le dépasser veut dire que sa protection n'agit pas, ou que la
    /// sonde est en défaut. Marge choisie par MAUS.
    /// </summary>
    private const int CpuDangerMarginC = 5;

    /// <summary>
    /// Limite du processeur : celle qu'il donne lui-même, sinon celle du catalogue (modèles vérifiés). Inconnue : <c>null</c>,
    /// jamais de limite inventée.
    /// </summary>
    public static int? CpuLimit(int? chipLimitC, int? catalogLimitC) => chipLimitC ?? catalogLimitC;

    public static bool CpuAtLimit(double temperatureC, int limitC) => temperatureC >= limitC - CpuAtLimitMarginC;

    public static bool CpuDanger(double temperatureC, int limitC) => temperatureC >= limitC + CpuDangerMarginC;

    /// <summary>La carte a atteint la température où elle ralentit pour se protéger (seuil donné par la carte elle-même).</summary>
    public static bool GpuDanger(double temperatureC, int slowdownC) => temperatureC >= slowdownC;
}
