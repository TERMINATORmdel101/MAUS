using System.Reflection;

namespace Maus.Core;

/// <summary>Version de MAUS telle qu'elle est affichée (par exemple « 0.3.2-alpha »), sans l'empreinte du commit.</summary>
public static class AppVersion
{
    public static string Display { get; } = Clean(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3));

    /// <summary>
    /// Sept premiers caractères du commit dont MAUS a été fabriqué (« 00155f5 »), ou <c>null</c> s'il est inconnu : deux
    /// fabrications du même numéro de version restent ainsi distinguables (journaux d'erreur, « À propos », maus --version).
    /// </summary>
    public static string? Commit { get; } = CommitOf(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Version et commit : « 0.7.6 (00155f5) », ou la version seule si le commit est inconnu.</summary>
    public static string Full => Commit is { } commit ? $"{Display} ({commit})" : Display;

    public static string? CommitOf(string? informationalVersion)
    {
        var plus = informationalVersion?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        if (plus < 0)
        {
            return null;
        }

        var hash = informationalVersion![(plus + 1)..];
        return hash.Length >= 7 && hash.All(char.IsAsciiHexDigit) ? hash[..7].ToLowerInvariant() : null;
    }

    /// <summary>Retire la partie « +empreinte » que le SDK ajoute après la version.</summary>
    public static string Clean(string? informationalVersion) =>
        string.IsNullOrWhiteSpace(informationalVersion) ? "0.0.0" : informationalVersion.Split('+')[0];
}
