using Maus.Core;
using Maus.Core.Fixes;
using Maus.Core.Preferences;

namespace Maus.App.ViewModels;

/// <summary>Une option de liste déroulante : libellé affiché et valeur.</summary>
public sealed record ChoiceOption<T>(string Label, T Value);

/// <summary>Vos choix : profil Game Bar, alimentation du portable, constats marqués « voulu ».</summary>
public sealed partial class MainViewModel
{
    private ChoiceOption<int?> _gameBarChoice;
    private ChoiceOption<LaptopPowerChoice> _laptopChoice;
    private bool _loadingChoices;
    private bool _isLaptop;

    public IReadOnlyList<ChoiceOption<int?>> GameBarOptions { get; } =
    [
        new("Automatique : le profil détecté par MAUS", null),
        new("Profil 1 : je n'utilise ni l'app Xbox ni le Game Pass", 1),
        new("Profil 2 : j'utilise l'app Xbox ou le Game Pass", 2),
        new("Profil 3 : Game Bar complète (recommandé sur Ryzen X3D)", 3),
    ];

    public IReadOnlyList<ChoiceOption<LaptopPowerChoice>> LaptopOptions { get; } =
    [
        new("Pas encore choisi (proposition : performance sur secteur, Équilibré sur batterie)", LaptopPowerChoice.NotChosen),
        new("Performance sur secteur, Équilibré sur batterie (recommandé)", LaptopPowerChoice.Performance),
        new("Performance partout (autonomie réduite)", LaptopPowerChoice.PerformanceEverywhere),
        new("Autonomie (Équilibré sur secteur, économie sur batterie)", LaptopPowerChoice.Battery),
    ];

    public bool IsLaptop
    {
        get => _isLaptop;
        private set => SetProperty(ref _isLaptop, value);
    }

    public ChoiceOption<int?> GameBarChoice
    {
        get => _gameBarChoice;
        set
        {
            var previous = _gameBarChoice;
            if (value is null || !SetProperty(ref _gameBarChoice, value) || _loadingChoices)
            {
                return;
            }

            _ = ChangeGameBarAsync(previous, value);
        }
    }

    public ChoiceOption<LaptopPowerChoice> LaptopChoice
    {
        get => _laptopChoice;
        set
        {
            if (value is null || !SetProperty(ref _laptopChoice, value) || _loadingChoices)
            {
                return;
            }

            _ = UpdatePreferencesAsync(p => p with { LaptopPower = value.Value }, "M05");
        }
    }

    /// <summary>Affiche les choix enregistrés, sans déclencher de mise à jour.</summary>
    private void LoadChoices(AuditContext context)
    {
        _loadingChoices = true;
        try
        {
            IsLaptop = context.Hardware.IsLaptop;
            GameBarChoice = GameBarOptions.FirstOrDefault(o => o.Value == context.Preferences.GameBarProfile) ?? GameBarOptions[0];
            LaptopChoice = LaptopOptions.FirstOrDefault(o => o.Value == context.Preferences.LaptopPower) ?? LaptopOptions[0];
        }
        finally
        {
            _loadingChoices = false;
        }
    }

    private async Task ChangeGameBarAsync(ChoiceOption<int?> previous, ChoiceOption<int?> chosen)
    {
        // Laisse la liste déroulante terminer sa mise à jour avant une éventuelle remise en arrière.
        await Task.Yield();
        if (_lastContext is { Hardware.Cpu.IsAsymmetricDualCcdX3D: true } && chosen.Value is 1 or 2 &&
            !Confirm("Ryzen X3D détecté", "Sur votre processeur, la Game Bar sert à placer les jeux sur les cœurs dotés du V-Cache. " +
                "Avec ce profil, vos jeux risquent de tourner sur les cœurs sans V-Cache et d'être moins fluides." +
                Environment.NewLine + Environment.NewLine + "Choisir ce profil quand même ?"))
        {
            _loadingChoices = true;
            GameBarChoice = previous;
            _loadingChoices = false;
            return;
        }

        await UpdatePreferencesAsync(p => p with { GameBarProfile = chosen.Value }, "M07");
    }

    /// <summary>Première ouverture sur un portable : la question du choix d'alimentation (décision du projet).</summary>
    private async Task AskLaptopChoiceOnceAsync(AuditContext context)
    {
        if (_laptopQuestionAsked || !context.Hardware.IsLaptop || context.Preferences.LaptopPower != LaptopPowerChoice.NotChosen)
        {
            return;
        }

        _laptopQuestionAsked = true;
        if (AskLaptopChoice() is { } choice)
        {
            _loadingChoices = true;
            LaptopChoice = LaptopOptions.First(o => o.Value == choice);
            _loadingChoices = false;
            await UpdatePreferencesAsync(p => p with { LaptopPower = choice }, "M05");
        }
    }

    /// <summary>Marque (ou démarque) un constat « voulu », puis relance son module.</summary>
    private async Task OnAcknowledgeAsync(string moduleId, Finding finding, bool acknowledge)
    {
        if (acknowledge)
        {
            var warning = finding.Status == FindingStatus.Problem
                ? "Attention : c'est un problème de sécurité ou de fiabilité. " + Environment.NewLine + Environment.NewLine
                : string.Empty;
            if (!Confirm("Marquer « voulu » ?", $"{warning}« {finding.Title} » ({finding.Current}) ne sera plus signalé, et MAUS ne proposera plus de le corriger. " +
                    "Si la situation change, il sera de nouveau signalé. Vous pouvez retirer cette marque à tout moment." +
                    Environment.NewLine + Environment.NewLine + "Continuer ?"))
            {
                return;
            }
        }

        var now = DateTimeOffset.Now;
        await UpdatePreferencesAsync(p => acknowledge ? p.Acknowledge(finding, now) : p.Unacknowledge(finding.Id), moduleId);
    }

    private async Task UpdatePreferencesAsync(Func<UserPreferences, UserPreferences> change, params string[] moduleIds)
    {
        var updated = change(_lastContext?.Preferences ?? PreferencesStore.Load());
        try
        {
            await Task.Run(() => PreferencesStore.Save(updated));
        }
        catch (Exception ex) when (ex is JournalUnsafeException or System.IO.IOException or UnauthorizedAccessException)
        {
            StatusText = $"Votre choix n'a pas pu être enregistré : {ex.Message}";
            return;
        }

        if (_lastContext is not null)
        {
            _lastContext = _lastContext.WithPreferences(updated);
            await RefreshModulesAsync(moduleIds);
            StatusText = "Choix enregistré : les constats et les corrections sont mis à jour.";
        }
    }
}
