using System.Diagnostics;
using System.Text;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Platform;

/// <summary>
/// Exécute uniquement des commandes connues pour ne rien modifier. Toute autre commande est refusée :
/// en mode audit, MAUS ne peut pas écrire sur le système, même par erreur.
/// </summary>
public sealed class ReadOnlyCommandRunner : ICommandRunner
{
    /// <summary>Lignes de commande admises, chacune entière et exacte (casse ignorée) : celles que MAUS lance, rien d'autre.</summary>
    private static readonly Dictionary<string, IReadOnlyList<string>[]> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["powercfg.exe"] = [["/list"]],
        ["bcdedit.exe"] = [["/enum", "{current}"]],
        ["fsutil.exe"] = [Modules.M11Health.HardwareHealthModule.TrimArguments],
        ["reagentc.exe"] = [["/info"]],
        ["nvidia-smi.exe"] = [Modules.M09Gpu.GpuDriverModule.NvidiaSmiQueryArguments, Modules.M09Gpu.GpuDriverModule.NvidiaSmiMemoryArguments, Modules.M09Gpu.GpuDriverModule.NvidiaSmiPcieArguments],
        ["winget.exe"] = [Modules.M20Software.Winget.ListArguments],

        // DISM : seulement /CheckHealth, qui lit l'indicateur d'altération (/RestoreHealth réparerait).
        ["dism.exe"] = [Modules.M02Repair.DismCommandImageHealthChecker.Arguments],
    };

    /// <summary>Processus avec les droits administrateur (moteur, fenêtre lancée en administrateur) : winget y est refusé.</summary>
    private readonly bool _elevated;

    public ReadOnlyCommandRunner()
        : this(Environment.IsPrivilegedProcess)
    {
    }

    /// <param name="elevated">Droits administrateur du processus, injectés pour les tests.</param>
    internal ReadOnlyCommandRunner(bool elevated)
    {
        _elevated = elevated;
    }

    /// <summary>
    /// Encodage de la sortie des outils classiques : la page OEM du système (<see cref="OemEncoding"/>), et non celle de la
    /// culture du fil, que l'application remplace par la langue de MAUS (un Windows russe lu en page 437 serait illisible).
    /// </summary>
    internal static Encoding ToolOutputEncoding => OemEncoding.Current;

    /// <summary>Seul emplacement admis pour le rapport de batterie de Windows : <c>%LOCALAPPDATA%\MAUS\rapports</c>.</summary>
    public static string BatteryReportPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "rapports", "rapport-batterie.html");

    /// <summary>Seul emplacement admis pour le rapport XML de batterie, lu puis supprimé par MAUS : <c>%LOCALAPPDATA%\MAUS\rapports</c>.</summary>
    public static string BatteryHistoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MAUS", "rapports", "rapport-batterie.xml");

    public static bool IsAllowed(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(executable);

        var name = FileName(executable);
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name += ".exe";
        }

        if (name.Equals("powercfg.exe", StringComparison.OrdinalIgnoreCase) && (IsBatteryReport(arguments) || IsBatteryHistory(arguments)))
        {
            return true;
        }

        return AllowedCommands.TryGetValue(name, out var allowed)
            && allowed.Any(line => line.SequenceEqual(arguments, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Rapport de batterie : écrit seulement le fichier du rapport, à l'emplacement prévu par MAUS (rien d'autre n'est admis).</summary>
    private static bool IsBatteryReport(IReadOnlyList<string> arguments) =>
        arguments.Count == 3 && arguments[0].Equals("/batteryreport", StringComparison.OrdinalIgnoreCase)
        && arguments[1].Equals("/output", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path.GetFullPath(arguments[2]), BatteryReportPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Le même rapport en XML : écrit seulement ce fichier, à l'emplacement prévu par MAUS, dans l'ordre documenté par Microsoft.</summary>
    private static bool IsBatteryHistory(IReadOnlyList<string> arguments) =>
        arguments.Count == 4 && arguments[0].Equals("/batteryreport", StringComparison.OrdinalIgnoreCase)
        && arguments[1].Equals("/output", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path.GetFullPath(arguments[2]), BatteryHistoryPath, StringComparison.OrdinalIgnoreCase)
        && arguments[3].Equals("/xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>Nom du fichier d'un chemin Windows (découpé sur « \ » ou « / », quel que soit le système qui compile).</summary>
    private static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    /// <summary>Vrai si le chemin est dans <c>Program Files\WindowsApps</c>, dossier des paquets que seul Windows peut modifier.</summary>
    public static bool IsInProtectedAppsFolder(string path, string programFiles)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (string.IsNullOrEmpty(programFiles) || path.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return path.StartsWith(programFiles.TrimEnd('\\') + @"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(executable);

        if (!IsAllowed(executable, arguments))
        {
            throw new InvalidOperationException(T("Commande refusée en mode lecture seule : {0} {1}", executable, string.Join(' ', arguments)));
        }

        var path = ResolveSystemExecutable(executable);
        var isWinget = FileName(path).Equals("winget.exe", StringComparison.OrdinalIgnoreCase);
        if (isWinget && _elevated)
        {
            // winget est un outil propre au compte de l'utilisateur : il lit et écrit dans son profil. Il n'est jamais lancé
            // avec les droits administrateur (0.8.6), ni par le moteur ni par une fenêtre lancée en administrateur.
            throw new InvalidOperationException(T("winget n'est jamais lancé avec les droits administrateur : ouvrez MAUS normalement (sans « Exécuter en tant qu'administrateur ») pour lister les mises à jour des logiciels."));
        }

        if (isWinget && !IsInProtectedAppsFolder(path, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)))
        {
            // Un winget.exe hors du dossier protégé des paquets pourrait avoir été remplacé : jamais lancé avec les droits de MAUS.
            throw new InvalidOperationException(T("Commande refusée : winget doit être celui du dossier protégé des applications ({0}).", path));
        }

        // Les outils classiques écrivent dans la page de code OEM du système. L'encodage de winget redirigé n'est pas documenté :
        // sa sortie est lue en octets puis décodée par DecodeOutput (UTF-16 si marqueur, UTF-8 si valide, sinon OEM).
        var oem = ToolOutputEncoding;
        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = oem,
            StandardErrorEncoding = oem,
            WorkingDirectory = SystemTool.WorkingFolder,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new DataSourceUnavailableException(T("Commande introuvable : {0}", executable), ex);
        }

        var stdout = isWinget ? ReadDecodedAsync(process.StandardOutput.BaseStream, oem, cancellationToken) : process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = isWinget ? ReadDecodedAsync(process.StandardError.BaseStream, oem, cancellationToken) : process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Délai dépassé ou annulation par l'utilisateur : le programme lancé est arrêté dans les deux cas (avant, une
            // annulation le laissait tourner après le retour de MAUS).
            StopTree(process);
            cancellationToken.ThrowIfCancellationRequested();
            return new CommandResult(-1, string.Empty, string.Empty, TimedOut: true);
        }

        string output;
        string errors;
        try
        {
            // Un programme fils peut garder la sortie ouverte après la fin : l'attente reste bornée.
            output = await stdout.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            errors = await stderr.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            StopTree(process);
            return new CommandResult(-1, string.Empty, string.Empty, TimedOut: true);
        }

        return new CommandResult(process.ExitCode, output, errors, TimedOut: false);
    }

    /// <summary>Lit les octets bruts, marqueur compris (un lecteur de texte le retirerait et changerait d'encodage), puis décode.</summary>
    private static async Task<string> ReadDecodedAsync(Stream stream, Encoding fallback, CancellationToken cancellationToken)
    {
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes, cancellationToken).ConfigureAwait(false);
        return DecodeOutput(bytes.ToArray(), fallback);
    }

    /// <summary>Arrête le programme et ses enfants ; déjà terminé ou refusé : rien de plus à faire.</summary>
    private static void StopTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Déjà terminé, ou Windows refuse de l'arrêter : il n'y a rien d'autre à tenter.
        }
    }

    /// <summary>
    /// Décode une sortie dont l'encodage n'est pas connu d'avance : UTF-16 si elle commence par son marqueur,
    /// UTF-8 si les octets forment de l'UTF-8 valide, sinon la page de code donnée (OEM).
    /// </summary>
    public static string DecodeOutput(byte[] bytes, Encoding fallback)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(fallback);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            return fallback.GetString(bytes);
        }
    }

    /// <summary>
    /// Les outils désignés par leur seul nom sont pris dans System32, et nulle part ailleurs : absents de System32, ils sont
    /// « introuvables ». Jamais de recherche dans le PATH, dont certains dossiers (WindowsApps du profil…) sont modifiables
    /// sans droits administrateur : un nvidia-smi.exe déposé là serait lancé avec les droits du moteur.
    /// </summary>
    internal static string ResolveSystemExecutable(string executable)
    {
        if (Path.IsPathRooted(executable))
        {
            return executable;
        }

        var name = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? executable : executable + ".exe";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);
    }
}
