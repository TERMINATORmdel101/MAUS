using Maus.Core.Modules.M12Startup;

namespace Maus.Core.Tests.Modules.M12Startup;

/// <summary>Dossiers Démarrage, variables et raccourcis simulés pour le module 12.</summary>
internal sealed class FakeStartupEnvironment : IStartupEnvironment
{
    public const string UserFolder = @"C:\Users\Bob\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";
    public const string CommonFolder = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp";

    public Dictionary<string, string> Shortcuts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? UserStartupFolder { get; init; } = UserFolder;

    public string? CommonStartupFolder { get; init; } = CommonFolder;

    public string Expand(string text) => text
        .Replace("%SystemRoot%", @"C:\Windows", StringComparison.OrdinalIgnoreCase)
        .Replace("%windir%", @"C:\Windows", StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase)
        .Replace("%LOCALAPPDATA%", @"C:\Users\Bob\AppData\Local", StringComparison.OrdinalIgnoreCase);

    public string? ResolveShortcut(string shortcutPath) => Shortcuts.GetValueOrDefault(shortcutPath);
}
