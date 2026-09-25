using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Platform;

/// <summary>
/// Écriture dans le registre, réservée aux corrections (V0.2). Les modules n'y ont jamais accès pendant la détection :
/// seul le moteur de corrections l'utilise, après accord de l'utilisateur et journalisation de la valeur d'origine.
/// </summary>
public interface IRegistryWriter
{
    /// <summary>Crée la clé si besoin, puis écrit la valeur avec son type exact.</summary>
    /// <exception cref="MausAccessDeniedException">Écriture refusée.</exception>
    void SetValue(RegistryHive hive, string path, string name, object value, RegistryValueKind kind);

    /// <summary>Supprime la valeur ; sans effet si elle est déjà absente.</summary>
    /// <exception cref="MausAccessDeniedException">Écriture refusée.</exception>
    void DeleteValue(RegistryHive hive, string path, string name);

    /// <summary>Supprime la clé si elle ne contient plus ni valeur ni sous-clé (clé créée par MAUS puis restaurée).</summary>
    void DeleteKeyIfEmpty(RegistryHive hive, string path);
}

public sealed class WindowsRegistryWriter : IRegistryWriter
{
    public void SetValue(RegistryHive hive, string path, string name, object value, RegistryValueKind kind) =>
        Run(hive, path, () =>
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.CreateSubKey(path, writable: true)
                ?? throw new MausAccessDeniedException(T("Création impossible : {0}\\{1}", hive, path));
            key.SetValue(name, value, kind);
        });

    public void DeleteValue(RegistryHive hive, string path, string name) =>
        Run(hive, path, () =>
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(path, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        });

    public void DeleteKeyIfEmpty(RegistryHive hive, string path) =>
        Run(hive, path, () =>
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            bool empty;
            using (var key = root.OpenSubKey(path, writable: false))
            {
                empty = key is not null && key.ValueCount == 0 && key.SubKeyCount == 0;
            }

            if (empty)
            {
                root.DeleteSubKey(path, throwOnMissingSubKey: false);
            }
        });

    private static void Run(RegistryHive hive, string path, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            throw new MausAccessDeniedException(T("Écriture refusée : {0}\\{1}", hive, path), ex);
        }
    }
}
