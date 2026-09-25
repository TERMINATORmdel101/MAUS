using Maus.Core.Platform;
using Microsoft.Win32;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Modules.M01Audit;

/// <summary>Catégorie « Persistance » : IFEO, Winlogon, AppInit_DLLs, programmes non signés au démarrage.</summary>
public sealed partial class RiskyChangesAuditModule
{
    private const string PersistenceCategory = "Persistance";
    private const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    private static readonly string[] IfeoKeys =
    [
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
    ];

    private static readonly string[] AppInitKeys =
    [
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Windows",
    ];

    private static readonly (RegistryHive Hive, string Path)[] RunKeys =
    [
        (Hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (Hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
        (Hklm, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
        (Hklm, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce"),
        (Hkcu, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        (Hkcu, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
    ];

    private static Check IfeoCheck => new("M01.ifeo-debugger", T("Programmes détournés par un « débogueur » (IFEO)"), PersistenceCategory, Severity.High, Fixable: true);

    private static Check WinlogonCheck => new("M01.winlogon", T("Programmes lancés à l'ouverture de session (Winlogon)"), PersistenceCategory, Severity.Critical, Fixable: true);

    private static Check AppInitCheck => new("M01.appinit", T("Bibliothèques injectées dans tous les programmes (AppInit_DLLs)"), PersistenceCategory, Severity.Critical, Fixable: true);

    private static Check StartupCheck => new("M01.unsigned-startup", T("Programmes non signés au démarrage"), PersistenceCategory, Severity.Medium);

    private static Finding DetectIfeoDebuggers(IRegistryReader registry)
    {
        var explanation = T("La clé « Image File Execution Options » permet de lancer un autre programme à la place de celui demandé. Des logiciels malveillants s'en servent " +
            "pour se relancer ou pour bloquer l'antivirus ; quelques outils légitimes aussi (Process Explorer qui remplace le Gestionnaire des tâches).");
        var expected = T("aucun débogueur");
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var protectedKeys = 0;
        foreach (var root in IfeoKeys)
        {
            foreach (var program in registry.GetSubKeyNames(Hklm, root))
            {
                try
                {
                    var debugger = registry.GetString(Hklm, $@"{root}\{program}", "Debugger");
                    if (IsSet(debugger))
                    {
                        found.Add($"{program} → {debugger}");
                    }
                }
                catch (MausAccessDeniedException)
                {
                    // Quelques clés (celles de Defender notamment) sont protégées : on les ignore sans conclure.
                    protectedKeys++;
                }
            }
        }

        if (found.Count > 0)
        {
            return IfeoCheck.Deviation(
                T("à examiner : {0}", Join(found)),
                expected,
                explanation,
                T("Examiner chaque entrée : si vous ne reconnaissez pas le programme indiqué après la flèche, supprimer la valeur Debugger correspondante et lancer une analyse antivirus."));
        }

        var current = protectedKeys > 0 ? T("aucun ({0} clé(s) protégée(s) non lue(s))", protectedKeys) : T("aucun");
        return IfeoCheck.Compliant(current, expected, explanation);
    }

    private Finding DetectWinlogon(IRegistryReader registry)
    {
        var explanation = T("À l'ouverture de session, Windows lance les programmes indiqués dans les valeurs Shell (le Bureau, explorer.exe) et Userinit. " +
            "Un programme ajouté à ces valeurs démarre avant tout le reste : c'est une technique classique des logiciels malveillants.");
        var expected = T("Shell = explorer.exe ; Userinit = userinit.exe seul");
        var shell = registry.GetString(Hklm, WinlogonKey, "Shell");
        var userinit = registry.GetString(Hklm, WinlogonKey, "Userinit");
        var userShell = registry.GetString(Hkcu, WinlogonKey, "Shell");

        var issues = new List<string>();
        if (IsSet(shell) && !IsDefaultWinlogonEntry(shell, "explorer.exe", _windowsDirectory, _windowsDirectory))
        {
            issues.Add($"Shell = {shell}");
        }

        if (IsSet(userShell) && !IsDefaultWinlogonEntry(userShell, "explorer.exe", _windowsDirectory, _windowsDirectory))
        {
            issues.Add(T("Shell de l'utilisateur = {0}", userShell));
        }

        if (IsSet(userinit) && !IsDefaultWinlogonEntry(userinit, "userinit.exe", _windowsDirectory + @"\System32", _windowsDirectory))
        {
            issues.Add($"Userinit = {userinit}");
        }

        return issues.Count == 0
            ? WinlogonCheck.Compliant($"Shell = {shell ?? "explorer.exe"} ; Userinit = {userinit ?? "userinit.exe"}", expected, explanation)
            : WinlogonCheck.Deviation(
                Join(issues),
                expected,
                explanation,
                T("Identifier le programme ajouté (analyse antivirus recommandée), puis remettre Shell à « explorer.exe » et Userinit à « C:\\Windows\\system32\\userinit.exe, »."));
    }

    /// <summary>
    /// Vrai si chaque élément de la liste (séparée par des virgules) est l'exécutable attendu, sans chemin
    /// ou dans son dossier d'origine (écrit en clair ou avec %SystemRoot% / %windir%).
    /// </summary>
    internal static bool IsDefaultWinlogonEntry(string value, string fileName, string expectedFolder, string windowsDirectory)
    {
        var items = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return items.Length > 0 && items.All(item =>
        {
            // Découpage explicite sur « \ » : chemins Windows, quel que soit le système qui exécute les tests.
            var path = item.Trim('"');
            var separator = path.LastIndexOf('\\');
            if (!string.Equals(path[(separator + 1)..], fileName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var folder = separator < 0 ? null : path[..separator];
            if (string.IsNullOrEmpty(folder))
            {
                return true;
            }

            var expanded = folder
                .Replace("%SystemRoot%", windowsDirectory, StringComparison.OrdinalIgnoreCase)
                .Replace("%windir%", windowsDirectory, StringComparison.OrdinalIgnoreCase);
            return string.Equals(expanded.TrimEnd('\\'), expectedFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        });
    }

    private static Finding DetectAppInit(IRegistryReader registry)
    {
        var explanation = T("Les bibliothèques listées dans AppInit_DLLs sont chargées dans presque tous les programmes qui s'ouvrent. " +
            "Ce mécanisme ancien sert surtout aux logiciels malveillants et publicitaires.");
        var expected = T("liste vide ; LoadAppInit_DLLs = 0");
        var advice = T("Vider AppInit_DLLs et remettre LoadAppInit_DLLs à 0, après une analyse antivirus complète.");
        var active = new List<string>();
        var inactive = new List<string>();
        foreach (var key in AppInitKeys)
        {
            var dlls = registry.GetString(Hklm, key, "AppInit_DLLs");
            if (!IsSet(dlls))
            {
                continue;
            }

            (registry.GetDword(Hklm, key, "LoadAppInit_DLLs") == 1 ? active : inactive).Add(dlls.Trim());
        }

        if (active.Count > 0)
        {
            return AppInitCheck.Deviation(T("chargées : {0}", Join(active)), expected, explanation, advice);
        }

        return inactive.Count > 0
            ? AppInitCheck.Deviation(
                T("liste présente mais inactive : {0}", Join(inactive)),
                expected,
                T("Le chargement est coupé (LoadAppInit_DLLs = 0), mais la liste reste une trace à examiner. ") + explanation,
                advice,
                Severity.Low)
            : AppInitCheck.Compliant(T("liste vide"), expected, explanation);
    }

    private Finding DetectUnsignedStartup(AuditContext context)
    {
        var explanation = T("Une signature numérique garantit l'éditeur d'un programme et qu'il n'a pas été modifié. Un programme non signé lancé à chaque démarrage " +
            "n'est pas forcément dangereux (petits logiciels gratuits), mais c'est aussi l'emplacement préféré des logiciels malveillants.");
        var expected = T("programmes signés par leur éditeur");
        var unsigned = new List<string>();
        var orphans = new List<string>();
        var checkedCount = 0;
        var readable = false;
        foreach (var (hive, path) in RunKeys)
        {
            IReadOnlyList<string> names;
            try
            {
                names = context.Registry.GetValueNames(hive, path);
                readable = true;
            }
            catch (MausAccessDeniedException)
            {
                continue;
            }

            foreach (var name in names.Where(n => n.Length > 0))
            {
                var executable = ResolveStartupExecutable(context.Registry.GetString(hive, path, name));
                if (executable is null)
                {
                    continue;
                }

                if (!context.Files.FileExists(executable))
                {
                    orphans.Add(name);
                    continue;
                }

                checkedCount++;
                if (_signatures.Verify(executable) is SignatureStatus.Unsigned or SignatureStatus.Invalid)
                {
                    unsigned.Add($"{name} ({executable})");
                }
            }
        }

        if (!readable)
        {
            return StartupCheck.AdminRequired();
        }

        var orphanNote = orphans.Count > 0 ? T(" ; {0} entrée(s) sans programme : {1}", orphans.Count, Join(orphans, 3)) : string.Empty;
        if (unsigned.Count > 0)
        {
            return StartupCheck.Deviation(
                T("{0} programme(s) non signé(s) : {1}{2}", unsigned.Count, Join(unsigned, 4), orphanNote),
                expected,
                explanation,
                T("Vérifier que vous reconnaissez chaque programme listé ; sinon, le désactiver (voir Module 12) et lancer une analyse antivirus."));
        }

        if (orphans.Count > 0)
        {
            return StartupCheck.Deviation(
                T("{0} entrée(s) sans programme : {1}", orphans.Count, Join(orphans)),
                expected,
                T("Ces entrées lancent un programme qui n'existe plus : elles sont inutiles mais sans danger."),
                T("Retirer ces entrées orphelines (voir Module 12)."),
                Severity.Low);
        }

        return StartupCheck.Compliant(
            checkedCount == 0 ? T("aucun programme") : T("{0} programme(s), tous signés", checkedCount),
            expected,
            explanation);
    }

    /// <summary>Chemin complet de l'exécutable d'une commande Run : variables développées, nom seul cherché dans System32.</summary>
    private string? ResolveStartupExecutable(string? command)
    {
        var executable = StartupCommandParser.ExtractExecutable(command);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        executable = Environment.ExpandEnvironmentVariables(executable);
        return Path.IsPathRooted(executable)
            ? executable
            : Path.Combine(_windowsDirectory, "System32", executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? executable : executable + ".exe");
    }
}
