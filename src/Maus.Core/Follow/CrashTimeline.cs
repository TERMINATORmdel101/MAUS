using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Maus.Core.Fixes;
using Maus.Core.Modules.M02Repair;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Follow;

/// <summary>Ce que dit une ligne de la ligne du temps.</summary>
public enum CrashTimelineKind
{
    BlueScreen,
    PowerButton,
    UnexpectedShutdown,
    Update,
    Driver,
    MausFix,

    /// <summary>Arrêt ou redémarrage demandé, avec un motif de mise à jour de Windows (code de l'événement 1074).</summary>
    UpdateRestart,

    /// <summary>Arrêt ou redémarrage demandé, pour un autre motif ou sans motif.</summary>
    Requested,
}

/// <summary>Une ligne : quoi et quand, sans lien avec les lignes voisines.</summary>
/// <param name="When">Heure locale.</param>
/// <param name="HasTime">Faux quand la source ne donne que le jour (correctifs lus par WMI).</param>
public sealed record CrashTimelineLine(DateTime When, bool HasTime, CrashTimelineKind Kind, string What)
{
    public bool IsCrash => Kind is CrashTimelineKind.BlueScreen or CrashTimelineKind.PowerButton or CrashTimelineKind.UnexpectedShutdown;

    public string WhenText => When.ToString(HasTime ? "g" : "d", Culture);

    public string KindText => Kind switch
    {
        CrashTimelineKind.BlueScreen => T("Écran bleu"),
        CrashTimelineKind.PowerButton => T("Bouton d'alimentation"),
        CrashTimelineKind.UnexpectedShutdown => T("Arrêt non expliqué"),
        CrashTimelineKind.Update => T("Mise à jour"),
        CrashTimelineKind.UpdateRestart => T("Arrêt ou redémarrage, mise à jour"),
        CrashTimelineKind.Requested => T("Arrêt ou redémarrage demandé"),
        CrashTimelineKind.Driver => T("Pilote"),
        _ => T("Correction MAUS"),
    };
}

/// <summary>Ce qu'a donné la lecture d'une source.</summary>
public enum CrashTimelineState
{
    Found,
    None,
    NoRights,
    NotRead,
}

/// <summary>Une source lue : « aucun trouvé » et « non lu » ne se confondent jamais.</summary>
public sealed record CrashTimelineSection(string Name, CrashTimelineState State, int Count = 0, bool Truncated = false, string? Note = null)
{
    public override string ToString() => Text;

    public string Text
    {
        get
        {
            var text = State switch
            {
                CrashTimelineState.Found when Truncated => T("{0} : {1}", Name, Plural(Count, T("au moins {0} trouvé"), T("au moins {0} trouvés"))),
                CrashTimelineState.Found => T("{0} : {1}", Name, Plural(Count, T("{0} trouvé"), T("{0} trouvés"))),
                CrashTimelineState.None => T("{0} : aucun trouvé", Name),
                CrashTimelineState.NoRights when Count > 0 => T("{0} : lecture incomplète sans droits administrateur, {1} dans ce qui a pu être lu", Name, Plural(Count, T("{0} trouvé"), T("{0} trouvés"))),
                CrashTimelineState.NoRights => T("{0} : non lu sans droits administrateur", Name),
                _ when Count > 0 => T("{0} : lecture incomplète, {1} dans ce qui a pu être lu", Name, Plural(Count, T("{0} trouvé"), T("{0} trouvés"))),
                _ => T("{0} : non lu", Name),
            };
            return Note is null ? text : text + ". " + Note;
        }
    }

    public bool WasRead => State is CrashTimelineState.Found or CrashTimelineState.None;
}

/// <summary>
/// Le journal des corrections, tel que l'interface a pu l'obtenir. Il est réservé aux administrateurs : sans droits, il n'est
/// jamais lu, et demander une autorisation pour un simple complément serait de trop.
/// </summary>
public sealed record CrashTimelineJournal(IReadOnlyList<JournalSession> Sessions, int Unreadable = 0, bool NoRights = false, string? Error = null)
{
    public static CrashTimelineJournal WithoutRights { get; } = new([], NoRights: true);

    public static CrashTimelineJournal Failed(string error) => new([], Error: error);
}

/// <summary>La ligne du temps lue : les lignes, de la plus récente à la plus ancienne, et ce que chaque source a donné.</summary>
/// <param name="LogStart">Date du plus ancien événement du journal Système, ou <c>null</c> si elle n'a pas pu être lue.</param>
public sealed record CrashTimeline(IReadOnlyList<CrashTimelineLine> Lines, IReadOnlyList<CrashTimelineSection> Sections, DateTime ReadAt, DateTime? LogStart = null)
{
    public const int Days = 30;

    /// <summary>La seule phrase sur le lien entre deux lignes : MAUS n'en désigne aucun.</summary>
    public static string Caution => T("Ces événements se suivent : cela ne prouve pas que l'un cause l'autre. Seule l'analyse du fichier laissé par un écran bleu (avec l'outil WinDbg) peut désigner un pilote.");

    /// <summary>Le journal Système note un plantage quand Windows redémarre, pas au moment où il arrive.</summary>
    public static string CrashTimeNote => T("L'heure d'un écran bleu, d'un appui sur le bouton ou d'un arrêt non expliqué est celle où Windows l'a noté, au redémarrage suivant.");

    /// <summary>L'événement 1074 dit qui a transmis la demande, pas qui l'a voulue : le menu Démarrer y figure quand c'est vous qui éteignez.</summary>
    public static string RequestNote => T("« Demandé par » reprend le programme noté par Windows : il a transmis la demande, ce qui ne dit ni qui l'a voulue ni si elle était justifiée. Le motif et le type sont écrits dans la langue de Windows.");

    /// <summary>Jusqu'où remonte le journal : sans cette date, « rien trouvé » ne prouve rien. MAUS ne dit pas pourquoi il est court.</summary>
    public string Coverage
    {
        get
        {
            if (LogStart is not { } start)
            {
                return T("Windows ne garde qu'un nombre limité d'événements, et la date de début de son journal Système n'a pas pu être lue.");
            }

            var date = start.ToString("d", Culture);
            return CoversAllDays(start)
                ? T("Le journal Système de Windows remonte au {0} : les {1} jours sont couverts.", date, Days)
                : T("Windows n'a de traces dans son journal Système que depuis le {0} : rien ne peut en être dit pour les jours d'avant.", date);
        }
    }

    private bool CoversAllDays(DateTime start) => start <= ReadAt.AddDays(-Days);

    /// <summary>Ce qu'il faut retenir de la lecture, surtout quand elle ne donne rien : une lecture incomplète ne dit pas que tout va bien.</summary>
    public string Summary => Headline + " " + Coverage;

    private string Headline
    {
        get
        {
            if (Lines.Count > 0)
            {
                return Sections.All(s => s.WasRead)
                    ? T("{0} sur {1} jours, la plus récente d'abord.", Plural(Lines.Count, T("{0} ligne"), T("{0} lignes")), Days)
                    : T("{0} sur {1} jours, la plus récente d'abord. Une partie n'a pas pu être lue : la liste est peut-être incomplète.", Plural(Lines.Count, T("{0} ligne"), T("{0} lignes")), Days);
            }

            if (!Sections.All(s => s.WasRead))
            {
                return T("Rien trouvé dans ce qui a pu être lu : ce n'est pas la preuve qu'il ne s'est rien passé.");
            }

            return LogStart is { } start && !CoversAllDays(start)
                ? T("Rien trouvé dans ce que Windows a gardé.")
                : T("Rien trouvé sur {0} jours.", Days);
        }
    }

    /// <summary>
    /// Les lignes à montrer d'abord, dans l'ordre : les plantages passent avant les autres pour le choix, car des arrêts demandés
    /// ordinaires (plus de cent en 30 jours sur le PC du porteur) cacheraient sinon ce que cette carte vient chercher.
    /// </summary>
    public IReadOnlyList<CrashTimelineLine> FirstLines(int count)
    {
        var crashes = Math.Min(count, Lines.Count(l => l.IsCrash));
        var others = count - crashes;
        return Lines.Where(l => l.IsCrash ? crashes-- > 0 : others-- > 0).ToList();
    }

    /// <summary>Le texte à montrer à un réparateur. Le masquage se fait à la copie (<c>ClipboardText</c>).</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine(T("MAUS : ce qui s'est passé ces {0} derniers jours (lu le {1})", Days, ReadAt.ToString("g", Culture)));
        text.AppendLine(Caution);
        text.AppendLine(CrashTimeNote);
        text.AppendLine(RequestNote);
        text.AppendLine().AppendLine(T("Ce qui a été lu :"));
        foreach (var section in Sections)
        {
            text.Append("- ").AppendLine(section.Text);
        }

        text.Append("- ").AppendLine(Coverage);

        text.AppendLine().AppendLine(T("Du plus récent au plus ancien :"));
        if (Lines.Count == 0)
        {
            text.Append("- ").AppendLine(Headline);
        }

        foreach (var line in Lines)
        {
            text.Append("- ").Append(line.WhenText).Append(" · ").Append(line.KindText).Append(" · ").AppendLine(line.What);
        }

        return text.ToString().TrimEnd();
    }
}

/// <summary>
/// Lit la ligne du temps : écrans bleus, bouton d'alimentation et arrêts non expliqués (journal Système : Kernel-Power 41, WER 1001,
/// EventLog 6008), arrêts et redémarrages demandés (User32 1074), mises à jour et pilotes (journal Système : WindowsUpdateClient 19 ;
/// correctifs de <c>Win32_QuickFixEngineering</c>), corrections de MAUS (journal des corrections). Identifiants vérifiés sur Microsoft
/// Learn (« Troubleshoot unexpected reboots using system event logs ») et, avec les champs de l'événement 1074, sur le PC du porteur le
/// 2/11/2026 : sans droits, le journal Système et WMI se lisent, le journal des corrections non. Lecture seule, à faire hors du fil de
/// la fenêtre.
/// </summary>
public static partial class CrashTimelineReader
{
    internal const string ShutdownProvider = "EventLog";
    internal const int ShutdownEventId = 6008;
    internal const string UpdateProvider = "Microsoft-Windows-WindowsUpdateClient";
    internal const int UpdateInstalledEventId = 19;

    /// <summary>Définitions de Microsoft Defender : plusieurs par jour, le numéro de cet article reste le même dans toutes les langues.</summary>
    private const string DefenderDefinitions = "2267602";

    // Codes de motif (Microsoft Learn, « System Shutdown Reason Codes ») : le texte du motif change avec la langue de Windows, pas le code.
    private const uint ReasonUserDefined = 0x40000000;
    private const uint ReasonMajorMask = 0x00FF0000;
    private const uint ReasonMinorMask = 0x0000FFFF;
    private const uint ReasonMajorOperatingSystem = 0x00020000;

    /// <summary>UPGRADE, SERVICEPACK, HOTFIX, SECURITYFIX, et le retrait des trois derniers.</summary>
    private static readonly uint[] ReasonMinorUpdates = [0x03, 0x10, 0x11, 0x12, 0x16, 0x17, 0x18];

    /// <summary>
    /// Un plantage laisse jusqu'à trois traces écrites au redémarrage : elles font une seule ligne. Ce n'est pas un seuil de santé,
    /// seulement l'écart admis entre ces traces ; deux plantages rapprochés restent deux lignes, car un même identifiant ouvre une nouvelle ligne.
    /// </summary>
    private static readonly TimeSpan SameCrash = TimeSpan.FromMinutes(10);

    public static CrashTimeline Read(IEventLogReader logs, ICimReader cim, CrashTimelineJournal journal, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentNullException.ThrowIfNull(cim);
        ArgumentNullException.ThrowIfNull(journal);

        var local = now.LocalDateTime;
        var since = local.AddDays(-CrashTimeline.Days);

        var kernelPower = SystemLogQuery.Run(logs, WindowsHealthModule.KernelPowerProvider, WindowsHealthModule.KernelPowerIds, since);
        var wer = SystemLogQuery.Run(logs, WindowsHealthModule.WerProvider, WindowsHealthModule.WerIds, since);
        var shutdowns = SystemLogQuery.Run(logs, ShutdownProvider, [ShutdownEventId], since);
        var crashes = Crashes(kernelPower.Events.Concat(wer.Events).Concat(shutdowns.Events).Where(e => e.TimeCreated >= since));

        var requests = SystemLogQuery.Run(logs, RecentEvents.RestartProvider, [RecentEvents.RestartEventId], since);
        var requestLines = requests.Events.Where(e => e.TimeCreated >= since).Select(Requested).ToList();

        var installed = SystemLogQuery.Run(logs, UpdateProvider, [UpdateInstalledEventId], since);
        var titles = installed.Events
            .Where(e => e.TimeCreated >= since)
            .Select(e => (e.TimeCreated, Title: e.Data.GetValueOrDefault("updateTitle")?.Trim() ?? string.Empty))
            .ToList();
        var routine = titles.Count(t => IsRoutine(t.Title));
        var updates = titles.Where(t => !IsRoutine(t.Title)).ToList();
        var driverLines = Installed(updates.Where(t => IsDriver(t.Title)), CrashTimelineKind.Driver);
        var updateLines = Installed(updates.Where(t => !IsDriver(t.Title)), CrashTimelineKind.Update);

        // Un correctif déjà nommé par Windows Update ne fait pas une seconde ligne, moins précise.
        var fixes = RecentEvents.ReadFixes(cim, DateOnly.FromDateTime(local));
        var fixLines = (fixes ?? [])
            .Where(fix => !titles.Exists(t => Names(t.Title, fix.Id)))
            .Select(fix => new CrashTimelineLine(fix.Date.ToDateTime(TimeOnly.MinValue), false, CrashTimelineKind.Update, T("Correctif de Windows installé : {0}", fix.Id)))
            .ToList();

        var mausLines = MausFixes(journal, since);

        var updateCount = updateLines.Count + fixLines.Count;
        var updateState = fixes is null && State(updateCount, installed) is CrashTimelineState.Found or CrashTimelineState.None
            ? CrashTimelineState.NotRead
            : State(updateCount, installed);
        CrashTimelineSection[] sections =
        [
            new(T("Écrans bleus, bouton et arrêts non expliqués"), State(crashes.Count, kernelPower, wer, shutdowns), crashes.Count, kernelPower.Truncated || wer.Truncated || shutdowns.Truncated, FirstError(kernelPower, wer, shutdowns)),
            new(T("Arrêts et redémarrages demandés"), State(requestLines.Count, requests), requestLines.Count, requests.Truncated, FirstError(requests)),
            new(T("Mises à jour installées"), updateState, updateCount, installed.Truncated, UpdateNote(installed, routine)),
            new(T("Pilotes installés par Windows Update"), State(driverLines.Count, installed), driverLines.Count, installed.Truncated, FirstError(installed)),
            MausSection(journal, mausLines.Count),
        ];

        // À heure égale, un plantage passe d'abord : il a été noté au redémarrage, donc après ce qui précède.
        var lines = crashes.Concat(requestLines).Concat(updateLines).Concat(fixLines).Concat(driverLines).Concat(mausLines)
            .OrderByDescending(l => l.When)
            .ThenByDescending(l => l.IsCrash)
            .ToList();
        return new CrashTimeline(lines, sections, local, LogStart(logs));
    }

    private static DateTime? LogStart(IEventLogReader logs)
    {
        try
        {
            return logs.OldestEvent(SystemLogQuery.LogName);
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or DataSourceUnavailableException)
        {
            return null;
        }
    }

    /// <summary>
    /// « Demandé par X, motif Y » : le nom du programme, le motif et le type notés par Windows (param1, param3, param5). Jamais le compte
    /// (param7) ni le commentaire libre (param6), qui peuvent nommer quelqu'un. Le tri vient du code du motif (param4), pas de son texte.
    /// </summary>
    private static CrashTimelineLine Requested(EventRecordInfo request)
    {
        var program = request.Data.GetValueOrDefault("param1");
        var who = string.IsNullOrWhiteSpace(program) ? T("un programme non noté") : RecentEvents.Name(program);
        var why = Noted(request.Data.GetValueOrDefault("param3")) ?? T("non noté");
        var what = T("Demandé par {0}, motif : {1}", who, why);
        if (Noted(request.Data.GetValueOrDefault("param5")) is { } type)
        {
            what = T("{0} ; type : {1}", what, type);
        }

        var kind = IsUpdateReason(request.Data.GetValueOrDefault("param4")) ? CrashTimelineKind.UpdateRestart : CrashTimelineKind.Requested;
        return new CrashTimelineLine(request.TimeCreated, true, kind, what);
    }

    private static string? Noted(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim().TrimEnd('.');

    /// <summary>« 0x80020010 » : un motif de système d'exploitation défini par Windows, qui nomme une mise à jour. Code illisible : pas de mise à jour.</summary>
    private static bool IsUpdateReason(string? code)
    {
        var digits = code?.Trim();
        if (digits is null || !digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !uint.TryParse(digits.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var reason))
        {
            return false;
        }

        return (reason & ReasonUserDefined) == 0
            && (reason & ReasonMajorMask) == ReasonMajorOperatingSystem
            && Array.IndexOf(ReasonMinorUpdates, reason & ReasonMinorMask) >= 0;
    }

    private static CrashTimelineState State(int count, params SystemLogQuery[] queries)
    {
        if (queries.Any(q => q.AccessDenied))
        {
            return CrashTimelineState.NoRights;
        }

        if (queries.Any(q => q.Error is not null))
        {
            return CrashTimelineState.NotRead;
        }

        return count > 0 ? CrashTimelineState.Found : CrashTimelineState.None;
    }

    private static string? FirstError(params SystemLogQuery[] queries) => queries.FirstOrDefault(q => q.Error is not null)?.Error;

    private static string? UpdateNote(SystemLogQuery installed, int routine)
    {
        var notes = new List<string>();
        if (installed.Error is { } error)
        {
            notes.Add(error);
        }

        if (routine > 0)
        {
            notes.Add(Plural(routine, T("{0} mise à jour d'applications du Microsoft Store ou de définitions de Microsoft Defender n'est pas détaillée."), T("{0} mises à jour d'applications du Microsoft Store et de définitions de Microsoft Defender ne sont pas détaillées.")));
        }

        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    private static List<CrashTimelineLine> Crashes(IEnumerable<EventRecordInfo> traces)
    {
        var groups = new List<List<EventRecordInfo>>();
        foreach (var trace in traces.OrderBy(e => e.TimeCreated))
        {
            if (groups.Count > 0 && trace.TimeCreated - groups[^1][0].TimeCreated <= SameCrash && groups[^1].TrueForAll(e => e.Id != trace.Id))
            {
                groups[^1].Add(trace);
            }
            else
            {
                groups.Add([trace]);
            }
        }

        return groups.ConvertAll(Describe);
    }

    private static CrashTimelineLine Describe(List<EventRecordInfo> traces)
    {
        var when = traces[0].TimeCreated;
        var kernel = traces.Find(e => e.Id == WindowsHealthModule.KernelPowerIds[0]);
        var wer = traces.Find(e => e.Id == WindowsHealthModule.WerIds[0]);
        var code = new[]
        {
            HealthParsers.ParseBugcheckCode(wer?.Data.GetValueOrDefault("param1")),
            HealthParsers.ParseBugcheckCode(kernel?.Data.GetValueOrDefault("BugcheckCode")),
        }.FirstOrDefault(c => c is > 0);
        if (code is { } known)
        {
            return new CrashTimelineLine(when, true, CrashTimelineKind.BlueScreen, T("Code d'arrêt {0}", HealthParsers.DescribeBugcheck(known)));
        }

        if (wer is not null)
        {
            return new CrashTimelineLine(when, true, CrashTimelineKind.BlueScreen, T("Code d'arrêt illisible"));
        }

        if (kernel is null)
        {
            return new CrashTimelineLine(when, true, CrashTimelineKind.UnexpectedShutdown, T("Windows note que l'arrêt précédent n'était pas prévu"));
        }

        return HealthParsers.IsNonZero(kernel.Data.GetValueOrDefault("PowerButtonTimestamp"))
            ? new CrashTimelineLine(when, true, CrashTimelineKind.PowerButton, T("Pas d'écran bleu noté : Windows a noté le bouton d'alimentation, comme après un appui long"))
            : new CrashTimelineLine(when, true, CrashTimelineKind.UnexpectedShutdown, T("Pas d'écran bleu noté : Windows ne s'est pas éteint proprement"));
    }

    /// <summary>La même mise à jour notée plusieurs fois le même jour (plusieurs périphériques, nouvel essai) ne fait qu'une ligne.</summary>
    private static List<CrashTimelineLine> Installed(IEnumerable<(DateTime TimeCreated, string Title)> updates, CrashTimelineKind kind) => updates
        .GroupBy(u => (u.Title, u.TimeCreated.Date))
        .Select(group =>
        {
            var title = group.Key.Title.Length > 0 ? group.Key.Title : T("sans nom");
            var text = group.Count() > 1 ? T("{0} ({1} fois ce jour-là)", title, group.Count()) : title;
            return new CrashTimelineLine(group.Max(u => u.TimeCreated), true, kind, text);
        })
        .ToList();

    private static List<CrashTimelineLine> MausFixes(CrashTimelineJournal journal, DateTime since)
    {
        var lines = new List<CrashTimelineLine>();
        foreach (var session in journal.Sessions)
        {
            var written = session.Entries.Where(e => e.AppliedAt is not null).ToList();
            if (written.Count > 0 && written.Min(e => e.AppliedAt!.Value).LocalDateTime is var applied && applied >= since)
            {
                lines.Add(new CrashTimelineLine(applied, true, CrashTimelineKind.MausFix, OneFix(written) ? T("Appliquée : {0}", Titles(written)) : T("Appliquées : {0}", Titles(written))));
            }

            var undone = session.Entries.Where(e => e.RevertedAt is not null).ToList();
            if (undone.Count > 0 && undone.Max(e => e.RevertedAt!.Value).LocalDateTime is var reverted && reverted >= since)
            {
                lines.Add(new CrashTimelineLine(reverted, true, CrashTimelineKind.MausFix, OneFix(undone) ? T("Annulée : {0}", Titles(undone)) : T("Annulées : {0}", Titles(undone))));
            }
        }

        return lines;
    }

    private static bool OneFix(List<JournalEntry> entries) => entries.Select(e => e.ChangeTitle).Distinct(StringComparer.Ordinal).Count() == 1;

    private static string Titles(List<JournalEntry> entries) => string.Join(", ", entries.Select(e => e.ChangeTitle).Distinct(StringComparer.Ordinal));

    private static CrashTimelineSection MausSection(CrashTimelineJournal journal, int count)
    {
        var name = T("Corrections de MAUS");
        if (journal.NoRights)
        {
            return new CrashTimelineSection(name, CrashTimelineState.NoRights, Note: T("Leur journal est réservé aux administrateurs : cliquez sur « Toutes les fonctionnalités », en haut de la fenêtre, puis relisez."));
        }

        if (journal.Error is { } error)
        {
            return new CrashTimelineSection(name, CrashTimelineState.NotRead, Note: error);
        }

        return journal.Unreadable > 0
            ? new CrashTimelineSection(name, CrashTimelineState.NotRead, count, Note: Plural(journal.Unreadable, T("{0} séance n'a pas pu être lue."), T("{0} séances n'ont pas pu être lues.")))
            : new CrashTimelineSection(name, count > 0 ? CrashTimelineState.Found : CrashTimelineState.None, count);
    }

    /// <summary>Le numéro d'un correctif (« KB5127694 ») se retrouve dans le titre, écrit « KB5127694 » ou « 5127694 Ko » selon la langue.</summary>
    private static bool Names(string title, string hotFix)
    {
        var digits = string.Concat(hotFix.Where(char.IsAsciiDigit));
        return digits.Length > 0 && title.Contains(digits, StringComparison.Ordinal);
    }

    /// <summary>Applications du Store (titre formé de leur identifiant) et définitions de Defender : leur nombre est dit, leur détail noierait le reste.</summary>
    private static bool IsRoutine(string title) => StoreApp().IsMatch(title) || IsDefenderDefinitions(title);

    /// <summary>Définitions de Microsoft Defender, reconnues par le numéro de leur article (aussi pour les mises à jour qui ont échoué, Module 3).</summary>
    internal static bool IsDefenderDefinitions(string title) => title.Contains(DefenderDefinitions, StringComparison.Ordinal);

    /// <summary>
    /// Titres relevés sur le PC du porteur : « INTEL - System - 10.1.16.7 », « NVIDIA Display Driver Update (32.0.16.1088) ». Un titre
    /// d'une autre forme reste une « mise à jour », ce qui n'est pas faux.
    /// </summary>
    private static bool IsDriver(string title) => DriverTitle().IsMatch(title);

    [GeneratedRegex(@"\A[0-9A-Z]{12}-", RegexOptions.CultureInvariant)]
    private static partial Regex StoreApp();

    [GeneratedRegex(@"\bdriver\b|\A.+ - .+ - (.+ - )?\d+(\.\d+)+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DriverTitle();
}
