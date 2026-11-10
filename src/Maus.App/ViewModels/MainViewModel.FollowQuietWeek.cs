using Maus.Core.Engine.Broker;
using Maus.Core.Follow;
using Maus.Core.Preferences;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Suivi, carte « Les audits de la semaine » : le bilan depuis la dernière visite, sans fenêtre ni relance.</summary>
public sealed partial class MainViewModel
{
    private QuietWeekSummary? _quietWeek;

    /// <summary>Repère lu une fois par session : la carte ne change pas de discours quand on revient sur la page.</summary>
    private DateTimeOffset? _quietWeekSince;
    private bool _quietWeekSinceRead;
    private DateTimeOffset _quietWeekShownAt;
    private bool _quietWeekSeen;

    public static string QuietWeekTitle => T("Les audits de la semaine");

    public static string QuietWeekSettingLabel => T("Voir le réglage");

    public string QuietWeekHeadline => _quietWeek?.Headline ?? string.Empty;

    public IReadOnlyList<string> QuietWeekPoints => _quietWeek is { } summary ? [.. summary.Points.Select(p => p.Text)] : [];

    public IReadOnlyList<string> QuietWeekNotes => _quietWeek?.Notes ?? [];

    /// <summary>Le réglage n'apparaît qu'après un premier audit : le bouton ne mènerait nulle part avant.</summary>
    public bool CanOpenQuietWeekSetting => _quietWeek?.State == QuietWeekState.Off && HasAudit;

    public string QuietWeekSettingNote
    {
        get
        {
            if (_quietWeek?.State != QuietWeekState.Off)
            {
                return string.Empty;
            }

            if (!HasAudit)
            {
                return T("Ce réglage se trouve dans « Corrections », après un premier audit.");
            }

            return ElevationState.Current.IsFull ? string.Empty : T("Ce réglage crée une tâche de Windows : il demande « Toutes les fonctionnalités ».");
        }
    }

    public bool HasQuietWeekSettingNote => QuietWeekSettingNote.Length > 0;

    /// <summary>Le bilan est affiché : la carte peut être considérée comme vue.</summary>
    internal bool IsQuietWeekReady => _quietWeek is not null;

    /// <summary>La carte s'affiche : relit l'état de la tâche (une lecture pure, qui ne demande jamais rien) et les résumés, hors du fil de la fenêtre. Rien n'est lancé, rien n'est écrit.</summary>
    internal async Task ShowQuietWeekAsync()
    {
        ScheduleState? schedule = null;
        try
        {
            schedule = await Engine.GetScheduleAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or System.Security.SecurityException or EngineException)
        {
            Maus.Core.Diagnostics.Breadcrumbs.Add("suivi : état de l'audit de la semaine non lu");
        }

        var (known, since) = (_quietWeekSinceRead, _quietWeekSince);
        var (kept, visit, read) = await Task.Run(() =>
        {
            var snapshots = _histories.Snapshots.Load();
            if (known)
            {
                return (snapshots, since, true);
            }

            try
            {
                return (snapshots, PreferencesStore.Load().LastFollowVisit, true);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or Maus.Core.Fixes.JournalUnsafeException or EngineException)
            {
                return (snapshots, null, false);
            }
        });
        _quietWeekSince = visit;
        _quietWeekSinceRead = read;
        _quietWeekShownAt = DateTimeOffset.Now;
        _quietWeek = QuietWeek.Build(kept, visit, schedule, _quietWeekShownAt);
        foreach (var name in new[] { nameof(QuietWeekHeadline), nameof(QuietWeekPoints), nameof(QuietWeekNotes), nameof(CanOpenQuietWeekSetting), nameof(QuietWeekSettingNote), nameof(HasQuietWeekSettingNote) })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>
    /// La carte a vraiment été vue (appelé par le panneau, pas à l'ouverture de MAUS) : la visite sert de repère à la suivante.
    /// Seulement si des audits ont été montrés, et une fois par session. Un échec est sans conséquence : la carte redira la même chose.
    /// </summary>
    internal async Task MarkQuietWeekSeenAsync()
    {
        if (_quietWeekSeen || _quietWeek is not { ShowsAudits: true })
        {
            return;
        }

        _quietWeekSeen = true;
        var shownAt = _quietWeekShownAt;
        try
        {
            await Task.Run(() => PreferencesStore.Update(p => p with { LastFollowVisit = shownAt }));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or Maus.Core.Fixes.JournalUnsafeException or EngineException)
        {
            Maus.Core.Diagnostics.Breadcrumbs.Add("suivi : date de la visite non enregistrée");
        }
    }
}
