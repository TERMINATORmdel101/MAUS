using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M04Privacy;

/// <summary>Fabrique de constats du Module 4 : tout écart reste une optimisation (bleu), jamais un danger.</summary>
internal static class PrivacyFindings
{
    // Pages des Paramètres citées par les conseils du Module 4 (adresses documentées par Microsoft Learn, « Launch Windows Settings »).

    /// <summary>Confidentialité et sécurité > Diagnostics et commentaires.</summary>
    internal const string FeedbackPage = "ms-settings:privacy-feedback";

    /// <summary>Confidentialité et sécurité > Général, renommée « Recommandations et offres » sur les versions récentes.</summary>
    internal const string GeneralPrivacyPage = "ms-settings:privacy";

    /// <summary>Système > Notifications (rubrique « Paramètres supplémentaires » en bas de page).</summary>
    internal const string NotificationsPage = "ms-settings:notifications";

    /// <summary>Personnalisation > Écran de verrouillage.</summary>
    internal const string LockScreenPage = "ms-settings:lockscreen";

    /// <summary>Personnalisation > Démarrer.</summary>
    internal const string StartPage = "ms-settings:personalization-start";

    /// <summary>Confidentialité et sécurité > Autorisations de recherche.</summary>
    internal const string SearchPermissionsPage = "ms-settings:search-permissions";

    /// <summary>Confidentialité et sécurité > Historique des activités.</summary>
    internal const string ActivityHistoryPage = "ms-settings:privacy-activityhistory";

    /// <summary>Windows Update > Options avancées > Optimisation de la distribution.</summary>
    internal const string DeliveryOptimizationPage = "ms-settings:delivery-optimization";

    /// <summary>
    /// Constat conforme ou « optimisation possible » ; une correction réversible est prévue pour chaque écart.
    /// <paramref name="settingsPage"/> : page des Paramètres où le conseil envoie l'utilisateur, proposée seulement en cas d'écart (comme le conseil).
    /// </summary>
    public static Finding Choice(
        string id,
        string title,
        string category,
        bool compliant,
        string current,
        string expected,
        string explanation,
        string advice,
        string? settingsPage = null) => new()
    {
        Id = id,
        Title = title,
        Category = category,
        Status = compliant ? FindingStatus.Ok : FindingStatusExtensions.ForDeviation(Severity.Low),
        Severity = Severity.Low,
        Current = current,
        Expected = expected,
        Explanation = explanation,
        Advice = compliant ? null : advice,
        SettingsPage = compliant ? null : settingsPage,
        Fixable = !compliant,
    };

    /// <summary>Simple information, sans jugement : réglage au choix ou sans effet sur cette édition.</summary>
    public static Finding Info(
        string id,
        string title,
        string category,
        string current,
        string expected,
        string explanation,
        string? advice = null,
        bool fixable = false,
        string? settingsPage = null) => new()
    {
        Id = id,
        Title = title,
        Category = category,
        Status = FindingStatus.Info,
        Severity = Severity.Info,
        Current = current,
        Expected = expected,
        Explanation = explanation,
        Advice = advice,
        SettingsPage = settingsPage,
        Fixable = fixable,
    };

    /// <summary>Traduit une lecture refusée ou une source absente en constat « indéterminé », jamais en problème.</summary>
    public static Finding Guard(string id, string title, string category, Func<Finding> check)
    {
        try
        {
            return check();
        }
        catch (MausAccessDeniedException)
        {
            return Finding.AdminRequired(id, title, category);
        }
        catch (DataSourceUnavailableException)
        {
            return Finding.Unknown(id, title, T("Cette information n'est pas disponible sur ce PC."), category);
        }
    }
}
