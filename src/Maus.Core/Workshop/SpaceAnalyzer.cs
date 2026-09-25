using System.Diagnostics;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Fichier repéré par l'analyse de l'espace.</summary>
public sealed record SpaceFile(string Path, string Name, long Bytes, DateTime LastWrite);

/// <summary>Dossier analysé : taille totale, sous-dossiers, fichiers d'au moins 1 Mo, et le reste regroupé.</summary>
public sealed class SpaceNode
{
    public required string Path { get; init; }

    public required string Name { get; init; }

    public SpaceNode? Parent { get; init; }

    public long Bytes { get; internal set; }

    public long Files { get; internal set; }

    public List<SpaceNode> Folders { get; } = [];

    public List<SpaceFile> BigFiles { get; } = [];

    /// <summary>Petits fichiers (moins de 1 Mo) placés directement dans ce dossier, regroupés.</summary>
    public long OtherBytes { get; internal set; }

    public long OtherFiles { get; internal set; }

    /// <summary>Dossier illisible (droits) : sa taille réelle est inconnue.</summary>
    public bool Inaccessible { get; internal set; }
}

public sealed record SpaceReport(SpaceNode Root, IReadOnlyList<SpaceFile> Largest, long Files, long Folders, int Inaccessible, TimeSpan Duration);

/// <summary>
/// « Qu'est-ce qui prend de la place ? » : parcours en lecture seule d'un lecteur ou d'un dossier. Les liens (jonctions) ne sont
/// pas suivis, les fichiers OneDrive seulement en ligne comptent pour zéro, les dossiers illisibles sont signalés.
/// </summary>
public static class SpaceAnalyzer
{
    public const long BigFileBytes = 1L << 20;
    private const int LargestCount = 30;

    /// <summary>Attributs d'un fichier « à la demande » (OneDrive) : sa taille est en ligne, pas sur le disque.</summary>
    private const FileAttributes CloudOnly = FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;

    public static Task<SpaceReport> ScanAsync(string root, IProgress<long>? filesScanned = null, CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(() => Scan(root, filesScanned, cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static SpaceReport Scan(string root, IProgress<long>? filesScanned = null, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var largest = new PriorityQueue<SpaceFile, long>();
        var state = new ScanState(largest, filesScanned, cancellationToken);
        var full = Path.GetFullPath(root);
        var node = new SpaceNode { Path = full, Name = NameOf(full) };
        Walk(node, state);
        var top = new List<SpaceFile>();
        while (largest.TryDequeue(out var file, out _))
        {
            top.Add(file);
        }

        top.Reverse();
        return new SpaceReport(node, top, state.Files, state.Folders, state.Inaccessible, clock.Elapsed);
    }

    private static void Walk(SpaceNode node, ScanState state)
    {
        state.CancellationToken.ThrowIfCancellationRequested();
        state.Folders++;
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(node.Path).EnumerateFileSystemInfos("*", options).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            node.Inaccessible = true;
            state.Inaccessible++;
            return;
        }

        foreach (var entry in entries)
        {
            if (entry is DirectoryInfo directory)
            {
                var child = new SpaceNode { Path = directory.FullName, Name = directory.Name, Parent = node };
                Walk(child, state);
                node.Folders.Add(child);
                node.Bytes += child.Bytes;
                node.Files += child.Files;
            }
            else if (entry is FileInfo file)
            {
                long bytes;
                try
                {
                    bytes = (file.Attributes & CloudOnly) != 0 ? 0 : file.Length;
                }
                catch (IOException)
                {
                    continue;
                }

                node.Bytes += bytes;
                node.Files++;
                state.Files++;
                if (state.Files % 2000 == 0)
                {
                    state.Progress?.Report(state.Files);
                }

                if (bytes >= BigFileBytes)
                {
                    var found = new SpaceFile(file.FullName, file.Name, bytes, file.LastWriteTime);
                    node.BigFiles.Add(found);
                    state.Largest.Enqueue(found, bytes);
                    if (state.Largest.Count > LargestCount)
                    {
                        state.Largest.Dequeue();
                    }
                }
                else
                {
                    node.OtherBytes += bytes;
                    node.OtherFiles++;
                }
            }
        }

        node.Folders.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        node.BigFiles.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
    }

    private static string NameOf(string path) =>
        System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : path;

    private sealed class ScanState(PriorityQueue<SpaceFile, long> largest, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        public PriorityQueue<SpaceFile, long> Largest { get; } = largest;

        public IProgress<long>? Progress { get; } = progress;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public long Files { get; set; }

        public long Folders { get; set; }

        public int Inaccessible { get; set; }
    }
}

/// <summary>Explications des dossiers et fichiers volumineux bien connus : ce que c'est, et comment en libérer sans risque.</summary>
public static class SpaceHints
{
    public static string? Describe(string path)
    {
        // Découpage explicite sur « \ » : le même code tourne dans les tests sous Linux.
        var lower = path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        var name = lower[(lower.LastIndexOf('\\') + 1)..];
        return name switch
        {
            "hiberfil.sys" => T("Veille prolongée et démarrage rapide de Windows. Il disparaît si la veille prolongée est désactivée (commande « powercfg /h off », au prix du démarrage rapide)."),
            "pagefile.sys" => T("Fichier d'échange : la réserve de mémoire de Windows. Ne pas le supprimer (voir Module 1)."),
            "swapfile.sys" => T("Fichier d'échange des applications du Store. Géré par Windows."),
            "winsxs" when lower.EndsWith(@"\windows\winsxs", StringComparison.Ordinal) => T("Magasin des composants de Windows. Sa taille affichée est exagérée (fichiers partagés). Ne rien supprimer à la main : l'Assistant de stockage le nettoie sans risque."),
            "softwaredistribution" => T("Cache de Windows Update. Nettoyé par l'Assistant de stockage (fichiers de mise à jour)."),
            "windows.old" => T("Ancienne version de Windows, gardée 10 jours après une mise à jour pour revenir en arrière. Supprimable par l'Assistant de stockage (« Installations précédentes de Windows »)."),
            "$recycle.bin" => T("Corbeille. La vider libère cet espace."),
            "system volume information" => T("Points de restauration et index. Taille réglable dans la Protection du système ; ne pas supprimer à la main."),
            "temp" when lower.Contains(@"\appdata\local\temp", StringComparison.Ordinal) || lower.EndsWith(@"\windows\temp", StringComparison.Ordinal)
                => T("Fichiers temporaires. L'Assistant de stockage les supprime sans risque."),
            "downloads" or "téléchargements" => T("Téléchargements : souvent des installateurs devenus inutiles. À trier vous-même."),
            "steamapps" or "steamlibrary" => T("Jeux Steam. Désinstallez-les depuis Steam, jamais en supprimant le dossier."),
            "epic games" or "xboxgames" or "riot games" or "battle.net" or "ubisoft game launcher" or "ea games" => T("Jeux d'un lanceur. Désinstallez-les depuis le lanceur."),
            "installer" when lower.EndsWith(@"\windows\installer", StringComparison.Ordinal) => T("Copies des programmes installés, nécessaires pour les réparer ou les désinstaller. Ne rien supprimer à la main."),
            "driverstore" => T("Réserve des pilotes de Windows. Ne rien supprimer à la main."),
            "onedrive" => T("Dossier OneDrive : les fichiers « disponibles en ligne seulement » ne prennent pas de place ici."),
            _ => null,
        };
    }
}
