using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Maus.App.Controls;
using Maus.Core;
using Maus.Core.Reporting;
using static Maus.Core.Localization.Texts;

namespace Maus.App.Views;

/// <summary>Une ligne du détail du score : points retirés, constat, gravité.</summary>
public sealed record ScoreRow(string Points, string Title, string Detail, Brush Brush);

/// <summary>« Pourquoi ce score ? » : ce qui a coûté des points, selon quelle gravité, et le barème de MAUS.</summary>
public partial class ScoreWindow : Window
{
    public ScoreWindow(ScoreBreakdown breakdown)
    {
        InitializeComponent();
        ScoreValue.Text = breakdown.Score.ToString(CultureInfo.CurrentCulture);
        ScoreWord.Text = HealthScore.Describe(breakdown.Score);
        Summary.Text = breakdown.Points <= 0
            ? T("Aucun constat ne retire de points : tout est conforme, indéterminé ou marqué « voulu ».")
            : T("{0} points retirés au total ; le score baisse de moins en moins vite, d'où {1} sur 100.", Format(breakdown.Points), breakdown.Score);

        var rows = breakdown.Lines
            .Select(l => new ScoreRow("−" + Format(l.Points), l.Title, T("gravité {0} · {1}", HealthScore.GravityName(l.Gravity), l.FindingId), BrushOf(l.Gravity)))
            .ToList();
        if (breakdown.OptimisationCount > 0)
        {
            rows.Add(new ScoreRow("−" + Format(breakdown.OptimisationPoints), T("{0} optimisations possibles", breakdown.OptimisationCount),
                T("gravité faible · 1 point chacune, {0} points au plus pour l'ensemble", Format(HealthScore.OptimisationCap)), BrushOf(Severity.Low)));
        }

        Rows.ItemsSource = rows;
        NothingLost.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (breakdown.Cap is { } cap)
        {
            CapNote.Text = T("Plafond : un constat de gravité {0} limite le score à {1}, même si peu de points ont été retirés.",
                HealthScore.GravityName(cap == HealthScore.CriticalCap ? Severity.Critical : Severity.High), cap);
            CapNote.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => Appearance.Motion.Enter(Page, offset: 12);
    }

    private static string Format(double points) => points.ToString("0.#", CultureInfo.CurrentCulture);

    private static SolidColorBrush BrushOf(Severity gravity) => gravity switch
    {
        Severity.Critical or Severity.High => Palette.Red,
        Severity.Medium => Palette.Gold,
        _ => Palette.Blue,
    };

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
