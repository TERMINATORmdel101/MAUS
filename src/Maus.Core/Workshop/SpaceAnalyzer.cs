using System.Diagnostics;
using System.Text.Json.Serialization;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Workshop;

/// <summary>Fichier repéré par l'analyse de l'espace.</summary>
public sealed record SpaceFile(string Path, string Name, long Bytes);

/// <summary>Dossier analysé : taille totale, sous-dossiers, fichiers d'au moins 1 Mo, et le reste regroupé.</summary>
public sealed class SpaceNode
{
    public required string Path { get; init; }

    public required string Name { get; init; }

    /// <summary>Dossier parent (pas transmis par le canal du moteur : rétabli par <see cref="SpaceAnalyzer.RestoreParents"/>).</summary>
    [JsonIgnore]
    public SpaceNode? Parent { get; internal set; }

    [JsonInclude]
    public long Bytes { get; internal set; }

    /// <summary>Sert au regroupement dans le moteur ; aucun écran ne le lit, il ne passe pas par le canal.</summary>
    [JsonIgnore]
    public long Files { get; internal set; }

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<SpaceNode> Folders { get; } = [];

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public List<SpaceFile> BigFiles { get; } = [];

    /// <summary>Petits fichiers (moins de 1 Mo) placés directement dans ce dossier, regroupés.</summary>
    [JsonInclude]
    public long OtherBytes { get; internal set; }

    [JsonInclude]
    public long OtherFiles { get; internal set; }

    /// <summary>Dossier illisible (droits) : sa taille réelle est inconnue.</summary>
    [JsonInclude]
    public bool Inaccessible { get; internal set; }
}

public sealed record SpaceReport(SpaceNode Root, IReadOnlyList<SpaceFile> Largest, long Files, long Folders, int Inaccessible, TimeSpan Duration);

/// <summary>
/// « Qu'est-ce qui prend de la place ? » : parcours en lecture seule d'un lecteur ou d'un dossier. Les liens (jonctions) ne sont
/// pas suivis, les fichiers OneDrive seulement en ligne comptent pour zéro, les dossiers illisibles sont signalés.
/// </summary>
public static class SpaceAnalyzer
{
    /// <summary>
    /// Chemin complet d'un dossier local : une lettre de lecteur après normalisation, jamais un chemin réseau ni de
    /// périphérique, sous aucune écriture (Windows lit aussi « //serveur/partage », « //?/C:/ » et « \??\C:\ »).
    /// </summary>
    public static bool IsLocalRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root))
        {
            return false;
        }

        var full = Path.GetFullPath(root);
        return full.Length >= 3 && char.IsAsciiLetter(full[0]) && full[1] == ':' && full[2] == Path.DirectorySeparatorChar;
    }

    public const long BigFileBytes = 1L << 20;
    private const int LargestCount = 30;

    /// <summary>Dossiers et gros fichiers transmis au plus par le canal du moteur (<see cref="Compact"/>).</summary>
    public const int MaxChannelEntries = 40_000;

    /// <summary>Niveaux de dossiers transmis au plus : le JSON du canal s'arrête à 64 niveaux, et un dossier en prend deux.</summary>
    internal const int MaxChannelDepth = 25;

    /// <summary>Attributs d'un fichier « à la demande » (OneDrive) : sa taille est en ligne, pas sur le disque.</summary>
    private const FileAttributes CloudOnly = FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;

    public static Task<SpaceReport> ScanAsync(string root, IProgress<long>? filesScanned = null, IReadOnlyCollection<string>? skip = null, CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(() => Scan(root, filesScanned, skip, cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Dossiers d'autres comptes que la liste des profils ne cite pas : profils de Windows.old, corbeilles des autres comptes.</summary>
    public static IReadOnlyList<string> OtherAccountFolders(string drive, string? ownSid)
    {
        var found = new List<string> { Path.Combine(drive, "Windows.old", "Users") };
        try
        {
            found.AddRange(Directory.EnumerateDirectories(Path.Combine(drive, "$Recycle.Bin"), "S-*")
                .Where(folder => ownSid is null || !Path.GetFileName(folder).Equals(ownSid, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Pas de corbeille lisible sur ce lecteur : rien à écarter.
        }

        return found;
    }

    /// <summary>Vrai si <paramref name="path"/> est l'un de ces dossiers, ou se trouve dedans.</summary>
    public static bool IsUnder(string path, IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(folders);
        var full = Path.TrimEndingDirectorySeparator(path);
        return folders.Select(Path.TrimEndingDirectorySeparator).Any(folder =>
            full.Equals(folder, StringComparison.OrdinalIgnoreCase) || full.StartsWith(folder + @"\", StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="skip">Dossiers à ne jamais lire (profils des autres comptes) : comptés comme illisibles.</param>
    public static SpaceReport Scan(string root, IProgress<long>? filesScanned = null, IReadOnlyCollection<string>? skip = null, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var largest = new PriorityQueue<SpaceFile, long>();
        var state = new ScanState(largest, filesScanned, cancellationToken) { Skip = skip ?? [] };
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

    /// <summary>
    /// Profondeur de dossiers au plus (règle 1 : récursion bornée). Au-delà, le dossier est compté comme inaccessible : un
    /// dossier imbriqué des milliers de fois (même créé exprès) épuiserait la pile et fermerait MAUS sans message.
    /// </summary>
    internal const int MaxDepth = 256;

    /// <summary>Rétablit les liens vers les dossiers parents (arbre reçu du moteur, où ils ne sont pas transmis).</summary>
    public static void RestoreParents(SpaceNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var pending = new Stack<SpaceNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            foreach (var child in node.Folders)
            {
                child.Parent = node;
                pending.Push(child);
            }
        }
    }

    /// <summary>
    /// Allège l'arbre avant de le passer par le canal du moteur : un lecteur ordinaire compte des centaines de milliers de
    /// dossiers, trop pour un seul message. Seuls les plus gros dossiers et fichiers restent ; les autres rejoignent « le
    /// reste » de leur dossier parent, donc les tailles restent justes. Les dossiers illisibles restent signalés.
    /// </summary>
    public static void Compact(SpaceNode root, int maxEntries)
    {
        ArgumentNullException.ThrowIfNull(root);
        var threshold = BigFileBytes;
        while (Visit(root, threshold, merge: false) > maxEntries && threshold < long.MaxValue / 2)
        {
            threshold *= 2;
        }

        Visit(root, threshold, merge: true);
    }

    /// <summary>Compte ce qui reste au seuil donné ; avec <paramref name="merge"/>, regroupe le reste dans le dossier parent.</summary>
    private static long Visit(SpaceNode root, long threshold, bool merge)
    {
        var kept = 0L;
        var pending = new Stack<(SpaceNode Node, int Depth)>();
        pending.Push((root, 1));
        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            bool Keeps(SpaceNode folder) => depth < MaxChannelDepth && (folder.Bytes >= threshold || folder.Inaccessible);
            foreach (var folder in node.Folders)
            {
                if (Keeps(folder))
                {
                    kept++;
                    pending.Push((folder, depth + 1));
                }
                else if (merge)
                {
                    node.OtherBytes += folder.Bytes;
                    node.OtherFiles += folder.Files;
                }
            }

            kept += node.BigFiles.Count(file => file.Bytes >= threshold);
            if (merge)
            {
                foreach (var file in node.BigFiles.Where(file => file.Bytes < threshold))
                {
                    node.OtherBytes += file.Bytes;
                    node.OtherFiles++;
                }

                node.Folders.RemoveAll(folder => !Keeps(folder));
                node.BigFiles.RemoveAll(file => file.Bytes < threshold);
            }
        }

        return kept;
    }

    private static void Walk(SpaceNode node, ScanState state, int depth = 0)
    {
        state.CancellationToken.ThrowIfCancellationRequested();
        if (depth >= MaxDepth || IsUnder(node.Path, state.Skip))
        {
            node.Inaccessible = true;
            state.Inaccessible++;
            return;
        }

        state.Folders++;
        var options = new EnumerationOptions
        {
            // Une liste vide passerait pour un dossier vide : le refus, qui ne concerne que ce dossier, est traité plus bas.
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
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
            // Liens et jonctions : jamais suivis ni comptés. Les fichiers en ligne de OneDrive, eux, ne sont pas des liens.
            if (entry.LinkTarget is not null || (entry is DirectoryInfo && entry.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                continue;
            }

            if (entry is DirectoryInfo directory)
            {
                var child = new SpaceNode { Path = directory.FullName, Name = directory.Name, Parent = node };
                Walk(child, state, depth + 1);
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
                    var found = new SpaceFile(file.FullName, file.Name, bytes);
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

        public IReadOnlyCollection<string> Skip { get; init; } = [];

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
        ArgumentNullException.ThrowIfNull(path);

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
