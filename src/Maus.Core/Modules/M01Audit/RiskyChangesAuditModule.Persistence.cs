using Maus.Core.Platform;
using Microsoft.Win32;

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

    private static readonly Check IfeoCheck = new("M01.ifeo-debugger", "Programmes détournés par un « débogueur » (IFEO)", PersistenceCategory, Severity.High, Fixable: true);

    private static readonly Check WinlogonCheck = new("M01.winlogon", "Programmes lancés à l'ouverture de session (Winlogon)", PersistenceCategory, Severity.Critical, Fixable: true);

    private static readonly Check AppInitCheck = new("M01.appinit", "Bibliothèques injectées dans tous les programmes (AppInit_DLLs)", PersistenceCategory, Severity.Critical, Fixable: true);

    private static readonly Check StartupCheck = new("M01.unsigned-startup", "Programmes non signés au démarrage", PersistenceCategory, Severity.Medium);

    private static Finding DetectIfeoDebuggers(IRegistryReader registry)
    {
        const string explanation =
            "La clé « Image File Execution Options » permet de lancer un autre programme à la place de celui demandé. Des logiciels malveillants s'en servent " +
            "pour se relancer ou pour bloquer l'antivirus ; quelques outils légitimes aussi (Process Explorer qui remplace le Gestionnaire des tâches).";
        const string expected = "aucun débogueur";
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
                $"à examiner : {Join(found)}",
                expected,
                explanation,
                "Examiner chaque entrée : si vous ne reconnaissez pas le programme indiqué après la flèche, supprimer la valeur Debugger correspondante et lancer une analyse antivirus.");
        }

        var current = protectedKeys > 0 ? $"aucun ({protectedKeys} clé(s) protégée(s) non lue(s))" : "aucun";
        return IfeoCheck.Compliant(current, expected, explanation);
    }

    private Finding DetectWinlogon(IRegistryReader registry)
    {
        const string explanation =
            "À l'ouverture de session, Windows lance les programmes indiqués dans les valeurs Shell (le Bureau, explorer.exe) et Userinit. " +
            "Un programme ajouté à ces valeurs démarre avant tout le reste : c'est une technique classique des logiciels malveillants.";
        const string expected = "Shell = explorer.exe ; Userinit = userinit.exe seul";
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
            issues.Add($"Shell de l'utilisateur = {userShell}");
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
                "Identifier le programme ajouté (analyse antivirus recommandée), puis remettre Shell à « explorer.exe » et Userinit à « C:\\Windows\\system32\\userinit.exe, ».");
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
        const string explanation =
            "Les bibliothèques listées dans AppInit_DLLs sont chargées dans presque tous les programmes qui s'ouvrent. " +
            "Ce mécanisme ancien sert surtout aux logiciels malveillants et publicitaires.";
        const string expected = "liste vide ; LoadAppInit_DLLs = 0";
        const string advice = "Vider AppInit_DLLs et remettre LoadAppInit_DLLs à 0, après une analyse antivirus complète.";
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
            return AppInitCheck.Deviation($"chargées : {Join(active)}", expected, explanation, advice);
        }

        return inactive.Count > 0
            ? AppInitCheck.Deviation(
                $"liste présente mais inactive : {Join(inactive)}",
                expected,
                "Le chargement est coupé (LoadAppInit_DLLs = 0), mais la liste reste une trace à examiner. " + explanation,
                advice,
                Severity.Low)
            : AppInitCheck.Compliant("liste vide", expected, explanation);
    }

    private Finding DetectUnsignedStartup(AuditContext context)
    {
        const string explanation =
            "Une signature numérique garantit l'éditeur d'un programme et qu'il n'a pas été modifié. Un programme non signé lancé à chaque démarrage " +
            "n'est pas forcément dangereux (petits logiciels gratuits), mais c'est aussi l'emplacement préféré des logiciels malveillants.";
        const string expected = "programmes signés par leur éditeur";
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

        var orphanNote = orphans.Count > 0 ? $" ; {orphans.Count} entrée(s) sans programme : {Join(orphans, 3)}" : string.Empty;
        if (unsigned.Count > 0)
        {
            return StartupCheck.Deviation(
                $"{unsigned.Count} programme(s) non signé(s) : {Join(unsigned, 4)}{orphanNote}",
                expected,
                explanation,
                "Vérifier que vous reconnaissez chaque programme listé ; sinon, le désactiver (voir Module 12) et lancer une analyse antivirus.");
        }

        if (orphans.Count > 0)
        {
            return StartupCheck.Deviation(
                $"{orphans.Count} entrée(s) sans programme : {Join(orphans)}",
                expected,
                "Ces entrées lancent un programme qui n'existe plus : elles sont inutiles mais sans danger.",
                "Retirer ces entrées orphelines (voir Module 12).",
                Severity.Low);
        }

        return StartupCheck.Compliant(
            checkedCount == 0 ? "aucun programme" : $"{checkedCount} programme(s), tous signés",
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
