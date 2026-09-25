using System.Security;
using Microsoft.Win32;

namespace Maus.Core.Platform;

/// <summary>Lecture du registre Windows. Aucune clé n'est jamais ouverte en écriture.</summary>
public sealed class WindowsRegistryReader : IRegistryReader
{
    public object? GetValue(RegistryHive hive, string path, string name, RegistryView view = RegistryView.Registry64) =>
        WithKey(hive, path, view, key => key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));

    public bool KeyExists(RegistryHive hive, string path, RegistryView view = RegistryView.Registry64) =>
        WithKey(hive, path, view, key => key is not null);

    public IReadOnlyList<string> GetValueNames(RegistryHive hive, string path, RegistryView view = RegistryView.Registry64) =>
        WithKey(hive, path, view, key => (IReadOnlyList<string>?)key?.GetValueNames() ?? []);

    public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string path, RegistryView view = RegistryView.Registry64) =>
        WithKey(hive, path, view, key => (IReadOnlyList<string>?)key?.GetSubKeyNames() ?? []);

    public RegistryValueKind? GetValueKind(RegistryHive hive, string path, string name, RegistryView view = RegistryView.Registry64) =>
        WithKey(hive, path, view, key => key is not null && key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)
            ? key.GetValueKind(name)
            : (RegistryValueKind?)null);

    private static T WithKey<T>(RegistryHive hive, string path, RegistryView view, Func<RegistryKey?, T> read)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(path, writable: false);
            return read(key);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            throw new MausAccessDeniedException($"Lecture refusée : {hive}\\{path}", ex);
        }
    }
}
