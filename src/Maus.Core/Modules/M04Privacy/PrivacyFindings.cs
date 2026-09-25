using Maus.Core.Platform;

namespace Maus.Core.Modules.M04Privacy;

/// <summary>Fabrique de constats du Module 4 : tout écart reste une optimisation (bleu), jamais un danger.</summary>
internal static class PrivacyFindings
{
    /// <summary>Constat conforme ou « optimisation possible » ; une correction réversible est prévue pour chaque écart.</summary>
    public static Finding Choice(
        string id,
        string title,
        string category,
        bool compliant,
        string current,
        string expected,
        string explanation,
        string advice) => new()
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
        bool fixable = false) => new()
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
            return Finding.Unknown(id, title, "Cette information n'est pas disponible sur ce PC.", category);
        }
    }
}
