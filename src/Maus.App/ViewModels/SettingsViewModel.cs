using System.Windows.Input;
using System.Windows.Media;
using Maus.App.Appearance;
using Maus.Core.Diagnostics;
using Maus.Core.Fixes;
using Maus.Core.Hardware;
using Maus.Core.Preferences;
using static Maus.Core.Localization.Texts;

namespace Maus.App.ViewModels;

/// <summary>Une couleur proposée : libellé, précision et pastilles d'aperçu (teinte principale et secondaire).</summary>
public sealed class AccentOptionViewModel(AccentChoice value, string label, string? detail)
{
    public override string ToString() => Label;

    public AccentChoice Value { get; } = value;

    public string Label { get; } = label;

    public string? Detail { get; } = detail;

    public bool HasDetail => Detail is not null;

    public Brush Primary { get; private set; } = Brushes.Transparent;

    public Brush Secondary { get; private set; } = Brushes.Transparent;

    public bool HasSecondary { get; private set; }

    internal AccentOptionViewModel WithSwatches()
    {
        var (primary, secondary) = AppearanceManager.Colors(Value);
        Primary = Frozen(new SolidColorBrush(primary));
        HasSecondary = secondary is not null;
        Secondary = Frozen(new SolidColorBrush(secondary ?? primary));
        return this;
    }

    private static SolidColorBrush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>Paramètres de MAUS : apparence, animations, fréquence des mesures, fenêtre de surveillance. Tout s'applique à chaud.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly IPreferencesStore _store;
    private ChoiceOption<ThemeChoice> _theme;
    private AccentOptionViewModel _accent;
    private ChoiceOption<AnimationChoice> _animations;
    private ChoiceOption<int> _refresh;
    private bool _monitorOnTop;
    private string _status = string.Empty;

    public SettingsViewModel(IPreferencesStore store, Action openMonitor)
    {
        _store = store;
        var current = AppearanceManager.Current;
        ThemeOptions =
        [
            new(T("Comme Windows (recommandé)"), ThemeChoice.System),
            new(T("Clair"), ThemeChoice.Light),
            new(T("Sombre"), ThemeChoice.Dark),
        ];
        AccentOptions =
        [
            new AccentOptionViewModel(AccentChoice.Components, T("Selon vos composants (recommandé)"), ComponentsDetail()).WithSwatches(),
            new AccentOptionViewModel(AccentChoice.Windows, T("Couleur de Windows"), T("Celle choisie dans Paramètres Windows > Personnalisation > Couleurs.")).WithSwatches(),
            new AccentOptionViewModel(AccentChoice.Maus, T("Bleu MAUS"), T("Le bleu du logo.")).WithSwatches(),
            new AccentOptionViewModel(AccentChoice.Blue, T("Bleu"), null).WithSwatches(),
            new AccentOptionViewModel(AccentChoice.Teal, T("Bleu-vert"), null).WithSwatches(),
            new AccentOptionViewModel(AccentChoice.Green, T("Vert"), null).WithSwatches(),
            new AccentOptionViewModel(AccentChoice.Orange, T("Orange"), null).WithSwatches(),
            new AccentOptionViewModel(AccentChoice.Red, T("Rouge"), null).WithSwatches(),
            new AccentOptionViewModel(AccentChoice.Violet, T("Violet"), null).WithSwatches(),
        ];
        AnimationOptions =
        [
            new(T("Comme Windows (recommandé)"), AnimationChoice.System),
            new(T("Activées"), AnimationChoice.On),
            new(T("Désactivées"), AnimationChoice.Off),
        ];
        RefreshOptions =
        [
            new(T("0,5 seconde (très réactif, un peu plus de charge)"), 500),
            new(T("1 seconde (recommandé)"), 1000),
            new(T("2 secondes"), 2000),
            new(T("5 secondes (le plus léger)"), 5000),
        ];

        ZoomOptions = ZoomChoices(current.UiZoomUsed);
        _zoom = ZoomOptions.First(o => Math.Abs(o.Value - current.UiZoomUsed) < 0.001);
        _theme = ThemeOptions.FirstOrDefault(o => o.Value == current.Theme) ?? ThemeOptions[0];
        _accent = AccentOptions.FirstOrDefault(o => o.Value == current.Accent) ?? AccentOptions[0];
        _animations = AnimationOptions.FirstOrDefault(o => o.Value == current.Animations) ?? AnimationOptions[0];
        _refresh = RefreshOptions.FirstOrDefault(o => o.Value == (int)current.RefreshInterval.TotalMilliseconds) ?? RefreshOptions[1];
        _monitorOnTop = current.MonitorOnTop;
        _offerAllFeatures = current.OfferAllFeatures;
        _auditWhenElevated = current.AuditWhenElevated;
        _technicianMode = current.TechnicianMode;
        _helpTips = !current.HelpTipsOff;
        OpenMonitorCommand = new AsyncCommand(() =>
        {
            openMonitor();
        });
    }

    public IReadOnlyList<ChoiceOption<ThemeChoice>> ThemeOptions { get; }

    public IReadOnlyList<AccentOptionViewModel> AccentOptions { get; }

    public IReadOnlyList<ChoiceOption<AnimationChoice>> AnimationOptions { get; }

    public IReadOnlyList<ChoiceOption<int>> RefreshOptions { get; }

    public ICommand OpenMonitorCommand { get; }

    public ChoiceOption<ThemeChoice> Theme
    {
        get => _theme;
        set => SetChoice(ref _theme, value, (p, v) => p with { Theme = v.Value });
    }

    public AccentOptionViewModel Accent
    {
        get => _accent;
        set => SetChoice(ref _accent, value, (p, v) => p with { Accent = v.Value });
    }

    public ChoiceOption<AnimationChoice> Animations
    {
        get => _animations;
        set => SetChoice(ref _animations, value, (p, v) => p with { Animations = v.Value });
    }

    public ChoiceOption<int> Refresh
    {
        get => _refresh;
        set => SetChoice(ref _refresh, value, (p, v) => p with { RefreshMilliseconds = v.Value });
    }

    private bool _offerAllFeatures;

    /// <summary>Proposer d'activer toutes les fonctionnalités (droits administrateur) à l'ouverture de MAUS.</summary>
    public bool OfferAllFeatures
    {
        get => _offerAllFeatures;
        set => Set(ref _offerAllFeatures, value, (p, v) => p with { OfferAllFeatures = v });
    }

    private bool _auditWhenElevated;

    /// <summary>Lancer l'audit dès que les droits administrateur sont accordés (désactivé par défaut).</summary>
    public bool AuditWhenElevated
    {
        get => _auditWhenElevated;
        set => Set(ref _auditWhenElevated, value, (p, v) => p with { AuditWhenElevated = v });
    }

    private ChoiceOption<double> _zoom;

    /// <summary>Tailles proposées ; une taille réglée avec Ctrl + molette s'ajoute à la liste, pour rester visible ici.</summary>
    public IReadOnlyList<ChoiceOption<double>> ZoomOptions { get; }

    private static List<ChoiceOption<double>> ZoomChoices(double current)
    {
        List<ChoiceOption<double>> choices =
        [
            new(T("Compact (85 %)"), 0.85),
            new(T("Normal (100 %)"), 1),
            new(T("Grand (115 %)"), 1.15),
            new(T("Très grand (130 %)"), 1.3),
        ];
        if (!choices.Exists(c => Math.Abs(c.Value - current) < 0.001))
        {
            choices.Add(new(T("Personnalisée ({0:0} %)", current * 100), current));
        }

        return choices;
    }

    public ChoiceOption<double> Zoom
    {
        get => _zoom;
        set => SetChoice(ref _zoom, value, (p, v) => p with { UiZoom = v.Value });
    }

    private bool _helpTips;

    private bool _technicianMode;

    /// <summary>Mode technicien : détails techniques dépliés partout, erreurs avec leur message d'origine.</summary>
    public bool HelpTips
    {
        get => _helpTips;
        set
        {
            if (SetProperty(ref _helpTips, value))
            {
                Change(p => p with { HelpTipsOff = !value });
            }
        }
    }

    public bool TechnicianMode
    {
        get => _technicianMode;
        set => Set(ref _technicianMode, value, (p, v) => p with { TechnicianMode = v });
    }

    public bool MonitorOnTop
    {
        get => _monitorOnTop;
        set => Set(ref _monitorOnTop, value, (p, v) => p with { MonitorOnTop = v });
    }

    /// <summary>Message si l'enregistrement échoue (le choix vaut alors pour cette ouverture de MAUS).</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => Status.Length > 0;

    private static string ComponentsDetail()
    {
        static string? Name(HardwareVendor brand) => brand switch
        {
            HardwareVendor.Intel => "Intel",
            HardwareVendor.Amd => "AMD",
            HardwareVendor.Nvidia => "Nvidia",
            _ => null,
        };

        var brands = AppearanceManager.Brands;
        return (Name(brands.Cpu), Name(brands.Graphics)) switch
        {
            ({ } cpu, { } gpu) when cpu != gpu => T("Teinte du processeur ({0}), avec une touche de celle de la carte graphique ({1}) dans la barre de navigation.", cpu, gpu),
            ({ } cpu, _) => T("Teinte du processeur ({0}).", cpu),
            (null, { } gpu) => T("Teinte de la carte graphique ({0}).", gpu),
            _ => T("Composants non reconnus : bleu MAUS."),
        };
    }

    /// <summary>Applique tout de suite, puis enregistre (le choix reste valable pour cette ouverture même si l'enregistrement échoue).</summary>
    /// <summary>Change un choix et l'enregistre, s'il a vraiment changé.</summary>
    private void Set<T>(ref T field, T value, Func<UserPreferences, T, UserPreferences> change, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            Change(p => change(p, value));
        }
    }

    /// <summary>Comme <see cref="Set{T}"/> pour une liste de choix : WPF envoie <c>null</c> quand la liste se vide, et il est ignoré.</summary>
    private void SetChoice<T>(ref T field, T? value, Func<UserPreferences, T, UserPreferences> change, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        where T : class
    {
        if (value is not null)
        {
            Set(ref field, value, change, propertyName);
        }
    }

    private void Change(Func<UserPreferences, UserPreferences> change)
    {
        AppearanceManager.Apply(change(AppearanceManager.Current));
        SaveAsync(change).Forget("paramètres : enregistrement", ex => Status = T("Votre choix n'a pas pu être enregistré : {0}", Maus.Core.Diagnostics.ErrorText.For(ex)));
    }

    private async Task SaveAsync(Func<UserPreferences, UserPreferences> change) =>
        Status = await Task.Run(() =>
        {
            try
            {
                _store.Update(change);
                return string.Empty;
            }
            catch (Exception ex) when (ex is JournalUnsafeException or System.IO.IOException or UnauthorizedAccessException)
            {
                return T("Votre choix n'a pas pu être enregistré : {0}", Maus.Core.Diagnostics.ErrorText.For(ex));
            }
        });
}
