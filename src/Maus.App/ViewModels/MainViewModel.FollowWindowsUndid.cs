using System.Collections.ObjectModel;
using System.Windows.Input;
using Maus.App.Appearance;
using Maus.Core;
using Maus.Core.Diagnostics;
using Maus.Core.Follow;
using Maus.Core.Platform;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Une correction encore appliquée, dans la carte « Windows a-t-il défait mes réglages ? ».</summary>
public sealed record WindowsUndidRow(Tone Tone, string State, string Title, string Detail)
{
    public override string ToString() => Title;
}

/// <summary>
/// Suivi, « Windows a-t-il défait mes réglages ? » : le journal comparé au dernier audit. Ouvrir la carte ne lance aucun audit
/// et ne demande aucune autorisation : sans droits, le journal n'est pas lu, et la carte le dit. Rien n'est réappliqué d'ici.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _windowsUndidShown;

    public ObservableCollection<WindowsUndidRow> WindowsUndidRows { get; } = [];

    public string WindowsUndidSummary { get; private set; } = string.Empty;

    public string WindowsUndidNotice { get; private set; } = string.Empty;

    public bool HasWindowsUndidNotice => WindowsUndidNotice.Length > 0;

    public string WindowsUndidBuildNote { get; private set; } = string.Empty;

    public bool HasWindowsUndidBuildChange { get; private set; }

    public bool HasWindowsUndidPlainBuildNote => !HasWindowsUndidBuildChange && WindowsUndidBuildNote.Length > 0;

    public string WindowsUndidNextStep { get; private set; } = string.Empty;

    public bool HasWindowsUndidNextStep => WindowsUndidNextStep.Length > 0;

    /// <summary>Sans droits : le journal ne se lit pas, et un audit sans droits ne le permettrait pas davantage. Seul le bouton des droits est proposé.</summary>
    public bool WindowsUndidNeedsRights { get; private set; }

    public bool WindowsUndidCanAudit => !WindowsUndidNeedsRights;

    /// <summary>Lire le journal ne demandera aucune autorisation : moteur en marche, ou MAUS lancé en administrateur.</summary>
    internal Func<bool> CanReadJournalSilently { get; set; } = () => EngineHost.OnDemand is not { IsElevatedRunning: false };

    /// <summary>Version actuelle de Windows, ou <c>null</c> si elle n'a pas pu être lue.</summary>
    internal Func<string?> ReadWindowsBuild { get; set; } = ReadCurrentWindowsBuild;

    private ICommand? _windowsUndidRecheck;

    /// <summary>Recoche ce qui a été remis et ouvre les corrections : rien n'est écrit, la décision reste à l'utilisateur.</summary>
    public ICommand WindowsUndidRecheckCommand => _windowsUndidRecheck ??= new AsyncCommand(() =>
    {
        RecheckComeBackCommand.Execute(null);
        SelectedTab = TabFixes;
    });

    /// <summary>La carte s'affiche : lit le journal seulement si cela ne demande aucune autorisation, et ne lance jamais d'audit.</summary>
    internal async Task ShowWindowsUndidAsync()
    {
        _windowsUndidShown = true;
        string? failure = null;
        if (!_journalRead && !IsWriting && CanReadJournalSilently())
        {
            try
            {
                // Met aussi la carte à jour. Moteur arrêté entre-temps : rien n'est demandé, la carte le dit ci-dessous.
                await RefreshJournalAsync(withoutAsking: true);
                if (_journalRead)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                CrashLog.Write(ex, "lecture du journal pour le suivi");
                failure = T("Le journal des corrections n'a pas pu être lu : {0}", ErrorText.For(ex));
            }
        }

        await UpdateWindowsUndidAsync(failure);
    }

    private async Task UpdateWindowsUndidAsync(string? failure = null)
    {
        var canRead = CanReadJournalSilently();
        var build = await Task.Run(ReadWindowsBuild);
        var sessions = Sessions.Select(s => s.Session).ToList();
        if (_journalRead)
        {
            MarkComeBack(sessions);
        }

        // Le plan ne vaut que pour un audit complet de cette ouverture : un PC bloqué n'en prépare aucun, cela ne dirait rien.
        var planKnown = _currentSnapshot is not null && _lastContext is { IsElevated: true, BlockingReason: null, ElevatedAsAnotherUser: false };
        var report = WindowsUndid.Compare(
            sessions,
            WindowsUndid.LastAudit(_snapshots),
            planKnown ? Changes.Select(c => c.Change.Id).ToHashSet(StringComparer.Ordinal) : null,
            _accountSid,
            build);

        WindowsUndidRows.Clear();
        foreach (var fix in report.Fixes)
        {
            WindowsUndidRows.Add(RowOf(fix, report.CheckedAt));
        }

        WindowsUndidNeedsRights = !canRead;
        WindowsUndidNotice = failure ?? _journalNotice ?? string.Empty;
        WindowsUndidBuildNote = BuildNoteOf(report);
        HasWindowsUndidBuildChange = report.BuildChanged;
        WindowsUndidSummary = SummaryOf(report, canRead);
        WindowsUndidNextStep = NextStepOf(report, canRead, planKnown);
        foreach (var name in new[]
        {
            nameof(WindowsUndidNeedsRights), nameof(WindowsUndidCanAudit), nameof(WindowsUndidNotice), nameof(HasWindowsUndidNotice),
            nameof(WindowsUndidBuildNote), nameof(HasWindowsUndidBuildChange), nameof(HasWindowsUndidPlainBuildNote),
            nameof(WindowsUndidSummary), nameof(WindowsUndidNextStep), nameof(HasWindowsUndidNextStep),
        })
        {
            OnPropertyChanged(name);
        }
    }

    private string SummaryOf(FixHoldReport report, bool canRead)
    {
        if (!_journalRead)
        {
            // « Pas lu » n'est jamais « tout va bien ».
            return canRead
                ? T("MAUS ne peut pas répondre pour l'instant : le journal des corrections n'a pas été lu.")
                : T("Non lu sans droits administrateur : le journal des corrections n'est pas lisible, MAUS ne peut donc pas dire si elles tiennent. Cela ne veut pas dire que tout va bien.");
        }

        if (report.Fixes.Count == 0)
        {
            return _journalNotice is null
                ? T("Le journal ne contient aucune correction encore appliquée : il n'y a rien à contrôler. Après vos premières corrections (page Corrections), cette carte dira si elles tiennent, par exemple après une mise à jour de Windows.")
                : T("MAUS ne peut pas répondre pour l'instant : le journal des corrections n'a pas été lu en entier.");
        }

        return T("{0} : {1} en place, {2} plus en place, {3}.", Plural(report.Fixes.Count, T("Sur {0} correction encore appliquée"), T("Sur {0} corrections encore appliquées")), report.Count(FixHoldState.InPlace), report.Count(FixHoldState.NotInPlace), Plural(report.Count(FixHoldState.NotChecked), T("{0} non contrôlée"), T("{0} non contrôlées")));
    }

    private static string BuildNoteOf(FixHoldReport report)
    {
        if (report.CheckedAt is not { } at)
        {
            return report.CurrentBuild is { } current ? T("Windows : build {0}. Aucun audit gardé pour comparer.", current) : T("Aucun audit gardé pour comparer.");
        }

        var when = at.LocalDateTime;
        if (!report.KnowsBothBuilds)
        {
            return T("Dernier audit : le {0:g}. La version de Windows n'a pas pu être comparée.", when);
        }

        return report.BuildChanged
            ? T("Windows a changé de version depuis le dernier audit du {0:g} (build {1} → {2}). Les états ci-dessous datent d'avant ce changement : relancez l'audit pour savoir si vos corrections tiennent encore.", when, report.AuditBuild!, report.CurrentBuild!)
            : T("Dernier audit : le {0:g}, avec la même version de Windows (build {1}).", when, report.CurrentBuild!);
    }

    private string NextStepOf(FixHoldReport report, bool canRead, bool planKnown)
    {
        if (!canRead)
        {
            return _journalRead
                ? T("Les droits administrateur sont refermés : cette liste vient de la dernière lecture du journal. Il faut les rouvrir pour un nouveau contrôle : Windows demandera votre autorisation, seulement après votre clic.")
                : T("« Toutes les fonctionnalités » permet de lire le journal : Windows demandera votre autorisation, seulement après votre clic.");
        }

        if (HasComeBackChanges)
        {
            return T("Rien n'est réappliqué tout seul : ce bouton coche de nouveau ces corrections et ouvre la page Corrections, à vous de décider. Une valeur remise l'a peut-être été exprès.");
        }

        return report.Count(FixHoldState.NotInPlace) > 0 && !planKnown
            ? T("Relancez l'audit : les corrections qui ne sont plus en place seront de nouveau proposées dans la page Corrections, si vous voulez les réappliquer.")
            : string.Empty;
    }

    private static WindowsUndidRow RowOf(FixHold fix, DateTimeOffset? checkedAt)
    {
        var applied = fix.AppliedOn.ToLocalTime().ToString("d", Culture);
        var when = checkedAt?.LocalDateTime ?? default;
        return fix.State switch
        {
            FixHoldState.InPlace => new(Tone.Mint, T("En place"), fix.Title, T("Appliquée le {0}. En place au dernier contrôle, le {1:g}.", applied, when)),
            FixHoldState.NotInPlace => new(Tone.Sand, T("Plus en place"), fix.Title, WithNote(T("Appliquée le {0}. Le dernier contrôle, le {1:g}, ne la voit plus en place : elle a pu être annulée par Windows, une mise à jour, un autre programme ou vous-même, ou ne pas avoir pris effet. MAUS ne peut pas savoir lequel.", applied, when), fix.EffectNote)),
            _ => new(Tone.Grey, T("Non contrôlée"), fix.Title, NotCheckedText(fix.Reason, applied, when)),
        };
    }

    private static string WithNote(string text, string? note) => note is null ? text : text + " " + T("Noté au moment de la correction : {0}", note);

    private static string NotCheckedText(NotCheckedReason reason, string applied, DateTime when) => reason switch
    {
        NotCheckedReason.NoAudit => T("Appliquée le {0}. Aucun audit gardé pour la contrôler : lancez l'audit.", applied),
        NotCheckedReason.AppliedSinceAudit => T("Appliquée le {0}, après le dernier audit du {1:g} : pas encore contrôlée.", applied, when),
        NotCheckedReason.ModuleNotRead => T("Appliquée le {0}. Le dernier audit, le {1:g}, n'a pas lu cette partie du PC.", applied, when),
        NotCheckedReason.ValueNotRead => T("Appliquée le {0}. Le dernier audit, le {1:g}, n'a pas pu lire ce réglage.", applied, when),
        NotCheckedReason.OtherAccount => T("Appliquée le {0} par un autre compte de ce PC, dans son profil : ouvrez MAUS depuis ce compte pour la contrôler.", applied),
        NotCheckedReason.AuditOfOtherAccount => T("Appliquée le {0}. Le dernier audit, le {1:g}, a été fait avec un autre compte administrateur : il n'a pas lu le profil de ce compte-ci.", applied, when),
        _ => T("Appliquée le {0}. Le résumé du dernier audit, le {1:g}, ne permet pas de le dire : relancez l'audit.", applied, when),
    };

    private static string? ReadCurrentWindowsBuild()
    {
        try
        {
            return WindowsInfo.Read(new WindowsRegistryReader()).FullBuild;
        }
        catch (Exception ex) when (ex is MausAccessDeniedException or System.IO.IOException or UnauthorizedAccessException or DataSourceUnavailableException)
        {
            Breadcrumbs.Add("version de Windows illisible pour le suivi");
            return null;
        }
    }
}
