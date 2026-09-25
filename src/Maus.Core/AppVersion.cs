using System.Reflection;

namespace Maus.Core;

/// <summary>Version de MAUS telle qu'elle est affichée (par exemple « 0.3.2-alpha »), sans l'empreinte du commit.</summary>
public static class AppVersion
{
    public static string Display { get; } = Clean(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3));

    /// <summary>Retire la partie « +empreinte » que le SDK ajoute après la version.</summary>
    public static string Clean(string? informationalVersion) =>
        string.IsNullOrWhiteSpace(informationalVersion) ? "0.0.0" : informationalVersion.Split('+')[0];
}
