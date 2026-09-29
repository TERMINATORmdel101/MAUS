using System.Globalization;
using System.Text;
using Maus.Core.Fixes;
using static Maus.Core.Localization.Texts;

namespace Maus.Core.Reporting;

/// <summary>Contenu du rapport : un audit, et facultativement l'audit d'avant et la séance de corrections.</summary>
public sealed record HtmlReportInput
{
    public required AuditReport After { get; init; }

    /// <summary>Audit d'avant les corrections ; <c>null</c> pour un simple rapport d'audit.</summary>
    public AuditReport? Before { get; init; }

    public JournalSession? Session { get; init; }

    public IReadOnlyList<VerifiedOutcome> Outcomes { get; init; } = [];
}

/// <summary>
/// Rapport avant/après en une page HTML autonome (sans script ni ressource externe), à enregistrer ou à imprimer.
/// Aucun nom d'utilisateur, nom de PC ni numéro de série.
/// </summary>
public static class HtmlReport
{
    private static readonly FindingStatus[] Order =
        [FindingStatus.Problem, FindingStatus.Warning, FindingStatus.Improvable, FindingStatus.Unknown, FindingStatus.Info, FindingStatus.Ok];

    /// <summary>Bannière du logo en base64, ou <c>null</c> si la ressource manque (le rapport s'en passe).</summary>
    private static readonly Lazy<string?> Logo = new(() =>
    {
        using var stream = typeof(HtmlReport).Assembly.GetManifestResourceStream("Maus.Core.Reporting.maus-report-logo.jpg");
        if (stream is null)
        {
            return null;
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Convert.ToBase64String(memory.ToArray());
    });

    public static string Build(HtmlReportInput input)
    {
        var after = input.After;
        var html = new StringBuilder();
        var title = input.Before is null ? T("Rapport d'audit MAUS") : T("Rapport avant/après MAUS");
        html.Append(CultureInfo.InvariantCulture, $"<!DOCTYPE html><html lang=\"{Language}\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append(CultureInfo.InvariantCulture, $"<title>{E(title)}</title><style>{StyleSheet}</style></head><body><main>");

        html.Append("<header>");
        if (Logo.Value is { } logo)
        {
            // Logo embarqué dans la page (base64) : le rapport reste un seul fichier, lisible hors connexion.
            html.Append(CultureInfo.InvariantCulture, $"<img class=\"logo\" alt=\"MAUS\" src=\"data:image/jpeg;base64,{logo}\">");
        }

        html.Append(CultureInfo.InvariantCulture, $"<h1>{E(title)}</h1><p class=\"sub\">{E(after.CreatedAt.ToLocalTime().ToString("f", Culture))}</p>")
            .Append(CultureInfo.InvariantCulture, $"<p>{E(T("{0} {1} (build {2}), édition {3}", after.Windows.ProductName, after.Windows.DisplayVersion, after.Windows.FullBuild, after.Windows.EditionId))}</p>")
            .Append(CultureInfo.InvariantCulture, $"<p>{E($"{Labels.Of(after.Hardware.FormFactor)} · {after.Hardware.Cpu.Name}")}")
            .Append(after.Hardware.Gpus.Count > 0 ? E(" · " + string.Join(", ", after.Hardware.Gpus.Select(g => g.Name))) : string.Empty)
            .Append("</p></header>");

        var breakdown = HealthScore.Explain(after.Modules);
        var score = breakdown.Score;
        html.Append(CultureInfo.InvariantCulture, $"<section><h2>{E(T("En résumé"))}</h2>")
            .Append(CultureInfo.InvariantCulture, $"<p class=\"score\"><b>{score}</b>/100 · {E(T("santé du PC : {0}", HealthScore.Describe(score)))}</p>");
        AppendScoreDetail(html, breakdown);
        html.Append("<div class=\"cards\">");
        foreach (var status in Order.Take(4))
        {
            var now = Count(after, status);
            var before = input.Before is null ? (int?)null : Count(input.Before, status);
            html.Append(CultureInfo.InvariantCulture, $"<div class=\"card s-{Css(status)}\"><div class=\"n\">{now}</div><div>{E(Labels.Of(status))}</div>");
            if (before is not null)
            {
                html.Append(CultureInfo.InvariantCulture, $"<div class=\"was\">{E(T("avant : {0}", before))}</div>");
            }

            html.Append("</div>");
        }

        html.Append("</div></section>");

        if (input.Session is { } session)
        {
            AppendSession(html, session, input.Outcomes);
        }

        if (input.Before is not null)
        {
            AppendChanges(html, input.Before, after);
        }

        html.Append(CultureInfo.InvariantCulture, $"<section><h2>{E(T("Constats détaillés"))}</h2>");
        foreach (var module in after.Modules)
        {
            html.Append(CultureInfo.InvariantCulture, $"<details{(module.WorstStatus.Rank() >= FindingStatus.Improvable.Rank() ? " open" : string.Empty)}><summary><span class=\"dot s-{Css(module.WorstStatus)}\"></span>{E($"{module.ModuleId} · {module.Title}")}</summary>");
            if (module.Error is not null)
            {
                html.Append(CultureInfo.InvariantCulture, $"<p class=\"err\">{E(module.Error)}</p>");
            }

            foreach (var finding in module.Findings.OrderBy(f => Array.IndexOf(Order, f.Status)))
            {
                html.Append(CultureInfo.InvariantCulture, $"<div class=\"finding s-{Css(finding.Status)}\"><div class=\"ft\"><b>{E(finding.Title)}</b><span class=\"tag\">{E(Labels.Of(finding.Status))}</span></div>");
                if (finding.Current is not null)
                {
                    html.Append(CultureInfo.InvariantCulture, $"<div class=\"val\">{E(finding.Expected is null ? T("Constaté : {0}", finding.Current) : T("Constaté : {0}   ·   Attendu : {1}", finding.Current, finding.Expected))}</div>");
                }

                html.Append(CultureInfo.InvariantCulture, $"<p>{E(finding.Explanation)}</p>");
                if (finding.Advice is not null)
                {
                    html.Append(CultureInfo.InvariantCulture, $"<p class=\"adv\">→ {E(finding.Advice)}</p>");
                }

                html.Append("</div>");
            }

            html.Append("</details>");
        }

        html.Append("</section>")
            .Append(CultureInfo.InvariantCulture, $"<footer>MAUS {E(after.MausVersion)} · {E(T("Logiciel libre (GPL-3.0) conçu et codé avec Claude, une IA d'Anthropic, sous la direction de son auteur."))} ")
            .Append(E(T("Ce rapport ne contient ni nom d'utilisateur, ni nom de PC, ni numéro de série.")))
            .Append("</footer></main></body></html>");
        return html.ToString();
    }

    private static void AppendSession(StringBuilder html, JournalSession session, IReadOnlyList<VerifiedOutcome> outcomes)
    {
        html.Append(CultureInfo.InvariantCulture, $"<section><h2>{E(T("Corrections faites"))}</h2>");
        html.Append(session.RestorePoint is { } point
            ? $"<p class=\"ok\">{E(T("Point de restauration n° {0} créé et vérifié avant toute modification.", point.SequenceNumber))}</p>"
            : $"<p class=\"warn\">{E(session.RestorePointNote ?? T("Aucun point de restauration."))}</p>");

        html.Append(CultureInfo.InvariantCulture, $"<table><thead><tr><th>{E(T("Correction"))}</th><th>{E(T("Résultat"))}</th><th>{E(T("Valeur avant → après"))}</th></tr></thead><tbody>");
        var verified = outcomes.ToDictionary(o => o.Outcome.ChangeId, StringComparer.Ordinal);
        foreach (var change in session.Entries.GroupBy(e => e.ChangeId))
        {
            var first = change.First();
            var result = verified.TryGetValue(change.Key, out var v)
                ? v.Message
                : first.State switch
                {
                    EntryState.Applied => T("Appliqué"),
                    EntryState.Reverted => T("Annulé depuis"),
                    EntryState.Failed => T("Échec : valeur d'origine remise"),
                    _ => first.State.ToString(),
                };
            var values = string.Join("<br>", change.Select(e => $"<code>{E(e.Key.Describe())}</code> : {E(SettingValue.Display(e.Before))} → {E(SettingValue.Display(e.After))}"));
            html.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(first.ChangeTitle)}</td><td>{E(result)}</td><td>{values}</td></tr>");
        }

        html.Append("</tbody></table>")
            .Append(CultureInfo.InvariantCulture, $"<h3>{E(T("Comment tout annuler"))}</h3><ul>")
            .Append(CultureInfo.InvariantCulture, $"<li>{E(T("Dans MAUS : onglet « Historique », bouton « Annuler cette séance » (ou une seule correction)."))}</li>")
            .Append(CultureInfo.InvariantCulture, $"<li>{E(T("En ligne de commande (administrateur) :"))} <code>maus --revert {E(session.Id)}</code></li>");
        if (session.RestorePoint is { } rp)
        {
            html.Append(CultureInfo.InvariantCulture, $"<li>{E(T("En dernier recours : Restauration du système (rstrui.exe), point n° {0}.", rp.SequenceNumber))}</li>");
        }

        html.Append("</ul></section>");
    }

    private static void AppendChanges(StringBuilder html, AuditReport before, AuditReport after)
    {
        var old = before.Modules.SelectMany(m => m.Findings).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
        var changed = after.Modules.SelectMany(m => m.Findings)
            .Where(f => old.TryGetValue(f.Id, out var o) && o.Status != f.Status)
            .ToList();
        html.Append(CultureInfo.InvariantCulture, $"<section><h2>{E(T("Ce qui a changé"))}</h2>");
        if (changed.Count == 0)
        {
            html.Append(CultureInfo.InvariantCulture, $"<p>{E(T("Aucun verdict n'a changé entre les deux audits."))}</p></section>");
            return;
        }

        html.Append(CultureInfo.InvariantCulture, $"<table><thead><tr><th>{E(T("Constat"))}</th><th>{E(T("Avant"))}</th><th>{E(T("Après"))}</th></tr></thead><tbody>");
        foreach (var finding in changed)
        {
            var was = old[finding.Id];
            html.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(finding.Title)}</td><td><span class=\"tag s-{Css(was.Status)}\">{E(Labels.Of(was.Status))}</span> {E(was.Current ?? string.Empty)}</td>")
                .Append(CultureInfo.InvariantCulture, $"<td><span class=\"tag s-{Css(finding.Status)}\">{E(Labels.Of(finding.Status))}</span> {E(finding.Current ?? string.Empty)}</td></tr>");
        }

        html.Append("</tbody></table></section>");
    }

    private static int Count(AuditReport report, FindingStatus status) => report.Modules.Sum(m => m.Count(status));

    /// <summary>« Pourquoi ce score ? » repliable : points retirés par constat, optimisations, plafond, barème.</summary>
    private static void AppendScoreDetail(StringBuilder html, ScoreBreakdown breakdown)
    {
        if (breakdown.Points <= 0)
        {
            return;
        }

        html.Append(CultureInfo.InvariantCulture, $"<details class=\"why\"><summary>{E(T("Pourquoi ce score ?"))}</summary><ul>");
        foreach (var line in breakdown.Lines)
        {
            html.Append(CultureInfo.InvariantCulture,
                $"<li><b>−{line.Points.ToString("0.#", Culture)}</b> {E(line.Title)} <span class=\"val\">({E(T("gravité {0}", HealthScore.GravityName(line.Gravity)))})</span></li>");
        }

        if (breakdown.OptimisationCount > 0)
        {
            html.Append(CultureInfo.InvariantCulture,
                $"<li><b>−{breakdown.OptimisationPoints.ToString("0.#", Culture)}</b> {E(T("{0} optimisations possibles", breakdown.OptimisationCount))} <span class=\"val\">({E(T("gravité faible, {0} points au plus pour l'ensemble", HealthScore.OptimisationCap.ToString("0.#", Culture)))})</span></li>");
        }

        html.Append("</ul>");
        if (breakdown.Cap is { } cap)
        {
            html.Append(CultureInfo.InvariantCulture, $"<p>{E(T("Plafond : un constat de gravité {0} limite le score à {1}, même si peu de points ont été retirés.", HealthScore.GravityName(cap == HealthScore.CriticalCap ? Severity.Critical : Severity.High), cap))}</p>");
        }

        html.Append(CultureInfo.InvariantCulture, $"<p class=\"val\">{E(T("Barème de MAUS : critique 20 points, importante 10, moyenne 4, faible 1 ; le score baisse de moins en moins vite (100 × e^(−points/100)). C'est un repère, pas une mesure officielle."))}</p></details>");
    }

    /// <summary>Échappe seulement ce qui compte en HTML ; les accents restent lisibles (page en UTF-8).</summary>
    private static string E(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);

    private static string Css(FindingStatus status) => status.ToString().ToLowerInvariant();

    private const string StyleSheet =
        ":root{--bg:#f6f7fb;--fg:#1b1d24;--muted:#5d6475;--card:#fff;--line:#dfe3ec;--ok:#1f8a4c;--info:#56607a;--improvable:#2563c9;--warning:#c46a00;--problem:#c42b1c;--unknown:#8a8f9c}" +
        "@media(prefers-color-scheme:dark){:root{--bg:#14161c;--fg:#e8eaf0;--muted:#a3a9b8;--card:#1d2029;--line:#2c313d;--ok:#3fbf73;--info:#9aa3ba;--improvable:#5b9bff;--warning:#f0a030;--problem:#ff6b5b;--unknown:#8a8f9c}}" +
        "*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 'Segoe UI Variable','Segoe UI',system-ui,sans-serif}" +
        "main{max-width:980px;margin:0 auto;padding:24px 16px 48px}h1{font-size:28px;margin:0}h2{font-size:20px;margin:32px 0 12px}h3{font-size:16px}" +
        ".sub,footer,.val,.was{color:var(--muted)}header p{margin:4px 0}header .logo{display:block;width:240px;max-width:60%;height:auto;border-radius:12px;box-shadow:0 4px 18px rgba(0,0,0,.25);margin:0 0 14px}details.why{margin:4px 0 14px}details.why summary{cursor:pointer;color:var(--improvable)}details.why li{margin:2px 0}section{margin-top:8px}" +
        ".cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px}" +
        ".card{background:var(--card);border:1px solid var(--line);border-top:4px solid var(--c);border-radius:10px;padding:12px}.card .n{font-size:30px;font-weight:600;color:var(--c)}" +
        ".s-ok{--c:var(--ok)}.s-info{--c:var(--info)}.s-improvable{--c:var(--improvable)}.s-warning{--c:var(--warning)}.s-problem{--c:var(--problem)}.s-unknown{--c:var(--unknown)}" +
        "details{background:var(--card);border:1px solid var(--line);border-radius:10px;margin:8px 0;padding:8px 14px}summary{cursor:pointer;font-weight:600;padding:4px 0}" +
        ".dot{display:inline-block;width:10px;height:10px;border-radius:50%;background:var(--c);margin-right:8px}" +
        ".finding{border-left:4px solid var(--c);padding:6px 12px;margin:10px 0}.ft{display:flex;justify-content:space-between;gap:12px}.finding p{margin:4px 0}.adv{font-style:italic}" +
        ".tag{color:var(--c);font-weight:600;font-size:13px;white-space:nowrap}" +
        "table{width:100%;border-collapse:collapse;background:var(--card);border-radius:10px;overflow:hidden}th,td{text-align:left;vertical-align:top;padding:8px 10px;border-bottom:1px solid var(--line)}" +
        "code{font-family:Consolas,monospace;font-size:13px;overflow-wrap:anywhere}.ok{color:var(--ok)}.warn,.err{color:var(--warning)}footer{margin-top:40px;font-size:13px}" +
        ".score{font-size:17px;margin:0 0 12px}.score b{font-size:30px}" +
        "@media print{details{break-inside:avoid}details:not([open])>*:not(summary){display:block}}";
}
