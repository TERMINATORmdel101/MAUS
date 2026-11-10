using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Maus.App.Appearance;
using Maus.Core;
using Maus.Core.Diagnostics;
using Maus.Core.Preferences;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Une ligne de la carte « Votre PC en un coup d'œil » : composant, nom lu par l'audit et couleur d'état.</summary>
public sealed record GlanceItem(string Label, string Value, Tone Tone, string Icon)
{
    public override string ToString() => AccessibleName;

    public string AccessibleName => Label + " : " + Value + " · " + State;

    /// <summary>L'état en mot et en pictogramme : la couleur n'est jamais le seul signal.</summary>
    public string State => Labels.Of(Status);

    public string StateIcon => StatusGlyphs.Of(Status);

    private FindingStatus Status => Tone switch
    {
        Tone.Rose => FindingStatus.Problem,
        Tone.Sand => FindingStatus.Warning,
        Tone.Blue => FindingStatus.Improvable,
        Tone.Mint => FindingStatus.Ok,
        _ => FindingStatus.Unknown,
    };
}

/// <summary>Une correction proposée sous le score : ce qu'elle rapporte, et le bouton qui la coche.</summary>
public sealed record BestFixRow(string Title, string Gain, System.Windows.Input.ICommand Fix)
{
    public override string ToString() => Title;
}

/// <summary>
/// Accueil simple, ou complet en mode technicien (0.8.6, un seul interrupteur) : rien n'est retiré, seulement rangé dans
/// l'Atelier, les Corrections et la barre de gauche ; score qui se remplit à la fin de l'audit ; « Quoi de neuf » en carte ; carte « Votre PC en un coup d'œil »
/// faite avec ce que l'audit a déjà lu (aucune nouvelle lecture du système).
/// </summary>
public sealed partial class MainViewModel
{
    private const int ScoreAnimationMilliseconds = 900;
    private double _displayedScore = -1;
    private DispatcherTimer? _scoreTimer;
    private bool _homeComplete = AppearanceManager.Current.TechnicianMode;

    /// <summary>Accueil complet (toutes les cartes), réservé au mode technicien ; sinon l'accueil simple.</summary>
    public bool IsHomeComplete => _homeComplete;

    public bool IsHomeSimple => !IsHomeComplete;

    /// <summary>« Voir les constats » : accueil complet seulement (l'accueil simple a la ligne des familles, qui y mène).</summary>
    public bool ShowSeeFindingsButton => HasAudit && IsHomeComplete;

    /// <summary>« Relancer l'audit » : l'accueil simple n'a qu'un bouton principal, « Corriger maintenant » s'il y a des corrections.</summary>
    public bool ShowRerunButton => HasAudit && (IsHomeComplete || !HasProposedChanges);

    /// <summary>Cartes d'outils sur une ligne : trois dans l'accueil simple, quatre dans le complet.</summary>
    public int ToolColumns => IsHomeComplete ? 4 : 3;

    /// <summary>
    /// Score affiché (anneau et chiffre) : il monte jusqu'au score à la fin de l'audit, sauf si les animations sont coupées.
    /// Avant tout audit dans cette fenêtre : celui du dernier audit gardé, en gris (<see cref="IsPastScore"/>), ou rien.
    /// </summary>
    public double DisplayedScore => HasScore ? _displayedScore : _pastScore;

    /// <summary>Tant que les choix d'affichage changent ailleurs (Paramètres), l'accueil suit.</summary>
    private void HookAppearance() => AppearanceManager.Changed += OnAppearanceChanged;

    private void UnhookAppearance()
    {
        AppearanceManager.Changed -= OnAppearanceChanged;
        _scoreTimer?.Stop();
    }

    private void OnAppearanceChanged(object? sender, EventArgs e)
    {
        _homeComplete = AppearanceManager.Current.TechnicianMode;
        foreach (var name in new[] { nameof(IsHomeComplete), nameof(IsHomeSimple), nameof(ShowSeeFindingsButton), nameof(ShowRerunButton), nameof(ToolColumns), nameof(ShowDriverCard) })
        {
            OnPropertyChanged(name);
        }
    }

    private void SaveDisplayChoice(Func<UserPreferences, UserPreferences> change) =>
        Task.Run(() =>
        {
            try
            {
                PreferencesStore.Update(change);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or Maus.Core.Fixes.JournalUnsafeException or Maus.Core.Engine.Broker.EngineException)
            {
                // Non enregistré : le choix vaut pour cette ouverture de MAUS.
            }
        }).Forget("accueil : enregistrement d'un choix d'affichage");

    /// <summary>Fait monter le score affiché jusqu'au nouveau score (environ une seconde, en ralentissant à la fin).</summary>
    /// <summary>Les corrections qui rapportent le plus, calculées sur le dernier audit.</summary>
    public ObservableCollection<BestFixRow> BestFixes { get; } = [];

    public bool HasBestFixes => BestFixes.Count > 0;

    /// <summary>« 64 → 71 (+7) » après des corrections ; vide dès l'audit suivant.</summary>
    public string ScoreDeltaText { get; private set; } = string.Empty;

    public bool HasScoreDelta => ScoreDeltaText.Length > 0;

    /// <summary>Audit complet, et ni constat à traiter ni contrôle indéterminé.</summary>
    public bool IsAllGreen { get; private set; }

    /// <summary>État des pilotes graphiques en une ligne ; vide si l'audit n'en a lu aucun.</summary>
    public string DriverLine { get; private set; } = string.Empty;

    public bool ShowDriverCard => IsHomeComplete && DriverLine.Length > 0;

    /// <summary>Page officielle du fabricant, donnée par le constat d'un pilote à vérifier.</summary>
    public Uri? DriverPage { get; private set; }

    public bool HasDriverPage => DriverPage is not null;

    private ICommand? _openDriverPage;

    public ICommand OpenDriverPageCommand => _openDriverPage ??= new AsyncCommand(() =>
    {
        if (DriverPage is { } page)
        {
            ShellLauncher.OpenUrl(page);
        }
    });

    /// <summary>Corrections à proposer, état des pilotes et « tout est vert » après un audit ou un changement de constat.</summary>
    private void RefreshHomeInsights()
    {
        BestFixes.Clear();
        foreach (var gain in HealthScore.BestGains(_results, f => ChangesFor(f).Any()))
        {
            var finding = gain.Finding;
            BestFixes.Add(new BestFixRow(
                finding.Title,
                gain.Points switch
                {
                    1 => T("environ +1 point"),
                    > 0 => T("environ +{0} points", gain.Points),
                    _ => T("à corriger pour lever le plafond du score"),
                },
                new AsyncCommand(() =>
                {
                    ((IFindingActions)this).Fix(finding);
                })));
        }

        var findings = _results.SelectMany(r => r.Findings).ToList();
        DriverLine = HealthScore.DriverLine(findings) ?? string.Empty;
        DriverPage = findings
            .Where(f => f.Id.StartsWith("M09.driver.", StringComparison.Ordinal) && f.Status != FindingStatus.Ok)
            .Select(f => LinkText.FirstLink(f.Advice))
            .FirstOrDefault(link => link is not null);
        IsAllGreen = HasAudit && !IsScorePartial && NothingToHandle && findings.All(f => f.Status != FindingStatus.Unknown);
        ScoreDeltaText = string.Empty;
        foreach (var name in new[] { nameof(HasBestFixes), nameof(DriverLine), nameof(ShowDriverCard), nameof(DriverPage), nameof(HasDriverPage), nameof(IsAllGreen), nameof(ScoreDeltaText), nameof(HasScoreDelta) })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Après des corrections : montre le passage d'un score à l'autre à côté de l'anneau, qui avance déjà de lui-même.</summary>
    private void ShowScoreDelta(int before, int after)
    {
        ScoreDeltaText = T("{0} → {1} ({2:+0;-0;=})", before, after, after - before);
        OnPropertyChanged(nameof(ScoreDeltaText));
        OnPropertyChanged(nameof(HasScoreDelta));
    }

    private void AnimateScore(int target)
    {
        _scoreTimer?.Stop();
        var from = Math.Max(0, _displayedScore);
        if (target < 0 || !Motion.IsEnabled || System.Windows.Application.Current is null || Math.Abs(target - from) < 0.5)
        {
            SetDisplayedScore(target);
            return;
        }

        var started = DateTime.UtcNow;
        _scoreTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _scoreTimer.Tick += (_, _) =>
        {
            var progress = Math.Min(1, (DateTime.UtcNow - started).TotalMilliseconds / ScoreAnimationMilliseconds);
            var eased = 1 - Math.Pow(1 - progress, 3);
            SetDisplayedScore(from + ((target - from) * eased));
            if (progress >= 1)
            {
                _scoreTimer?.Stop();
                SetDisplayedScore(target);
            }
        };
        _scoreTimer.Start();
    }

    private void SetDisplayedScore(double value)
    {
        _displayedScore = value;
        OnPropertyChanged(nameof(DisplayedScore));
        OnPropertyChanged(nameof(ScoreText));
    }

    // ----- Quoi de neuf, en carte sur l'accueil -----

    private IReadOnlyList<string> _whatsNew = [];
    private string? _whatsNewVersion;

    public IReadOnlyList<string> WhatsNewLines => _whatsNew;

    public bool ShowWhatsNew => _whatsNew.Count > 0;

    public string WhatsNewTitle => T("Quoi de neuf dans MAUS {0}", _whatsNewVersion ?? string.Empty);

    /// <summary>Après une mise à jour : les nouveautés de cette version, en carte, jusqu'à « Compris ».</summary>
    public void ShowWhatsNewFor(string version)
    {
        _whatsNewVersion = version;
        _whatsNew = Views.VersionNotes.For(version);
        OnPropertyChanged(nameof(WhatsNewLines));
        OnPropertyChanged(nameof(ShowWhatsNew));
        OnPropertyChanged(nameof(WhatsNewTitle));
    }

    public ICommand DismissWhatsNewCommand => _dismissWhatsNew ??= new ParameterCommand(_ =>
    {
        _whatsNew = [];
        OnPropertyChanged(nameof(WhatsNewLines));
        OnPropertyChanged(nameof(ShowWhatsNew));
    });

    private ICommand? _dismissWhatsNew;

    public ICommand OpenWhatsNewPageCommand => _openWhatsNewPage ??= new ParameterCommand(_ =>
    {
        if (_whatsNewVersion is { } version)
        {
            ShellLauncher.OpenUrl(new Uri(ProjectLinks.Repository + "/releases/tag/" + Uri.EscapeDataString(version)));
        }
    });

    private ICommand? _openWhatsNewPage;

    // ----- Votre PC en un coup d'œil -----

    /// <summary>Processeur, carte graphique, mémoire et disques, avec la couleur des constats qui les concernent.</summary>
    public ObservableCollection<GlanceItem> Glance { get; } = [];

    public bool HasGlance => Glance.Count > 0;

    /// <summary>Recalculée après chaque audit, seulement avec ce qu'il a lu.</summary>
    private void RefreshGlance()
    {
        Glance.Clear();
        if (_lastContext is { } context)
        {
            var findings = _results.SelectMany(r => r.Findings).ToList();
            foreach (var item in BuildGlance(context.Hardware, findings))
            {
                Glance.Add(item);
            }
        }

        OnPropertyChanged(nameof(HasGlance));
    }

    internal static IEnumerable<GlanceItem> BuildGlance(Maus.Core.Hardware.HardwareProfile hardware, IReadOnlyList<Finding> findings)
    {
        if (!string.IsNullOrWhiteSpace(hardware.Cpu.Name) && hardware.Cpu != Maus.Core.Hardware.CpuInfo.Unknown)
        {
            yield return new(T("Processeur"), hardware.Cpu.Name.Trim(), ToneOf(findings, "M16.cpu", "M08.intel-microcode"), "");
        }

        var gpu = hardware.Gpus.FirstOrDefault(g => !g.IsIntegrated) ?? (hardware.Gpus.Count > 0 ? hardware.Gpus[0] : null);
        if (gpu is not null)
        {
            yield return new(T("Carte graphique"), gpu.Name.Trim(), ToneOf(findings, "M09.", "M14.", "M16.gpu"), "");
        }

        if (findings.FirstOrDefault(f => f.Id == "M10.capacity")?.Current is { Length: > 0 } memory)
        {
            yield return new(T("Mémoire"), memory, ToneOf(findings, "M10."), "");
        }

        if (findings.FirstOrDefault(f => f.Id == "M11.disks")?.Current is { Length: > 0 } disks)
        {
            yield return new(T("Disques"), disks, ToneOf(findings, "M11.disk", "M11.system-disk", "M11.free-space", "M11.trim", "M11.firmware"), "");
        }
    }

    /// <summary>Couleur du plus grave des constats concernés ; gris si rien n'a pu être lu.</summary>
    private static Tone ToneOf(IReadOnlyList<Finding> findings, params string[] prefixes)
    {
        var related = findings.Where(f => prefixes.Any(p => f.Id.StartsWith(p, StringComparison.Ordinal))).Select(f => f.Status).ToList();
        return related switch
        {
            _ when related.Contains(FindingStatus.Problem) => Tone.Rose,
            _ when related.Contains(FindingStatus.Warning) => Tone.Sand,
            _ when related.Contains(FindingStatus.Improvable) => Tone.Blue,
            _ when related.Any(s => s is FindingStatus.Ok or FindingStatus.Info) => Tone.Mint,
            _ => Tone.Grey,
        };
    }
}
