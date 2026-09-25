using System.Globalization;
using System.Text;
using Maus.Core.Fixes;

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
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private static readonly FindingStatus[] Order =
        [FindingStatus.Problem, FindingStatus.Warning, FindingStatus.Improvable, FindingStatus.Unknown, FindingStatus.Info, FindingStatus.Ok];

    public static string Build(HtmlReportInput input)
    {
        var after = input.After;
        var html = new StringBuilder();
        var title = input.Before is null ? "Rapport d'audit MAUS" : "Rapport avant/après MAUS";
        html.Append("<!DOCTYPE html><html lang=\"fr\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append(CultureInfo.InvariantCulture, $"<title>{E(title)}</title><style>{StyleSheet}</style></head><body><main>");

        html.Append(CultureInfo.InvariantCulture, $"<header><h1>{E(title)}</h1><p class=\"sub\">{E(after.CreatedAt.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm", French))}</p>")
            .Append(CultureInfo.InvariantCulture, $"<p>{E($"{after.Windows.ProductName} {after.Windows.DisplayVersion} (build {after.Windows.FullBuild}), édition {after.Windows.EditionId}")}</p>")
            .Append(CultureInfo.InvariantCulture, $"<p>{E($"{Labels.Of(after.Hardware.FormFactor)} · {after.Hardware.Cpu.Name}")}")
            .Append(after.Hardware.Gpus.Count > 0 ? E(" · " + string.Join(", ", after.Hardware.Gpus.Select(g => g.Name))) : string.Empty)
            .Append("</p></header>");

        html.Append("<section><h2>En résumé</h2><div class=\"cards\">");
        foreach (var status in Order.Take(4))
        {
            var now = Count(after, status);
            var before = input.Before is null ? (int?)null : Count(input.Before, status);
            html.Append(CultureInfo.InvariantCulture, $"<div class=\"card s-{Css(status)}\"><div class=\"n\">{now}</div><div>{E(Labels.Of(status))}</div>");
            if (before is not null)
            {
                html.Append(CultureInfo.InvariantCulture, $"<div class=\"was\">avant : {before}</div>");
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

        html.Append("<section><h2>Constats détaillés</h2>");
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
                    html.Append(CultureInfo.InvariantCulture, $"<div class=\"val\">Constaté : {E(finding.Current)}{(finding.Expected is null ? string.Empty : " · Attendu : " + E(finding.Expected))}</div>");
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
            .Append(CultureInfo.InvariantCulture, $"<footer>MAUS {E(after.MausVersion)} · Logiciel libre (GPL-3.0) conçu et codé avec Claude, une IA d'Anthropic, sous la direction de son auteur. ")
            .Append("Ce rapport ne contient ni nom d'utilisateur, ni nom de PC, ni numéro de série.</footer></main></body></html>");
        return html.ToString();
    }

    private static void AppendSession(StringBuilder html, JournalSession session, IReadOnlyList<VerifiedOutcome> outcomes)
    {
        html.Append("<section><h2>Corrections faites</h2>");
        html.Append(session.RestorePoint is { } point
            ? string.Create(CultureInfo.InvariantCulture, $"<p class=\"ok\">Point de restauration n° {point.SequenceNumber} créé et vérifié avant toute modification.</p>")
            : $"<p class=\"warn\">{E(session.RestorePointNote ?? "Aucun point de restauration.")}</p>");

        html.Append("<table><thead><tr><th>Correction</th><th>Résultat</th><th>Valeur avant → après</th></tr></thead><tbody>");
        var verified = outcomes.ToDictionary(o => o.Outcome.ChangeId, StringComparer.Ordinal);
        foreach (var change in session.Entries.GroupBy(e => e.ChangeId))
        {
            var first = change.First();
            var result = verified.TryGetValue(change.Key, out var v)
                ? v.Message
                : first.State switch
                {
                    EntryState.Applied => "Appliqué",
                    EntryState.Reverted => "Annulé depuis",
                    EntryState.Failed => "Échec : valeur d'origine remise",
                    _ => first.State.ToString(),
                };
            var values = string.Join("<br>", change.Select(e => $"<code>{E(e.Key.Describe())}</code> : {E(SettingValue.Display(e.Before))} → {E(SettingValue.Display(e.After))}"));
            html.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(first.ChangeTitle)}</td><td>{E(result)}</td><td>{values}</td></tr>");
        }

        html.Append("</tbody></table>")
            .Append("<h3>Comment tout annuler</h3><ul>")
            .Append("<li>Dans MAUS : onglet <b>Historique</b>, bouton « Annuler cette séance » (ou une seule correction).</li>")
            .Append(CultureInfo.InvariantCulture, $"<li>En ligne de commande (administrateur) : <code>maus --revert {E(session.Id)}</code></li>");
        if (session.RestorePoint is { } rp)
        {
            html.Append(CultureInfo.InvariantCulture, $"<li>En dernier recours : Restauration du système (<code>rstrui.exe</code>), point n° {rp.SequenceNumber}.</li>");
        }

        html.Append("</ul></section>");
    }

    private static void AppendChanges(StringBuilder html, AuditReport before, AuditReport after)
    {
        var old = before.Modules.SelectMany(m => m.Findings).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
        var changed = after.Modules.SelectMany(m => m.Findings)
            .Where(f => old.TryGetValue(f.Id, out var o) && o.Status != f.Status)
            .ToList();
        html.Append("<section><h2>Ce qui a changé</h2>");
        if (changed.Count == 0)
        {
            html.Append("<p>Aucun verdict n'a changé entre les deux audits.</p></section>");
            return;
        }

        html.Append("<table><thead><tr><th>Constat</th><th>Avant</th><th>Après</th></tr></thead><tbody>");
        foreach (var finding in changed)
        {
            var was = old[finding.Id];
            html.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(finding.Title)}</td><td><span class=\"tag s-{Css(was.Status)}\">{E(Labels.Of(was.Status))}</span> {E(was.Current ?? string.Empty)}</td>")
                .Append(CultureInfo.InvariantCulture, $"<td><span class=\"tag s-{Css(finding.Status)}\">{E(Labels.Of(finding.Status))}</span> {E(finding.Current ?? string.Empty)}</td></tr>");
        }

        html.Append("</tbody></table></section>");
    }

    private static int Count(AuditReport report, FindingStatus status) => report.Modules.Sum(m => m.Count(status));

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
        ".sub,footer,.val,.was{color:var(--muted)}header p{margin:4px 0}section{margin-top:8px}" +
        ".cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px}" +
        ".card{background:var(--card);border:1px solid var(--line);border-top:4px solid var(--c);border-radius:10px;padding:12px}.card .n{font-size:30px;font-weight:600;color:var(--c)}" +
        ".s-ok{--c:var(--ok)}.s-info{--c:var(--info)}.s-improvable{--c:var(--improvable)}.s-warning{--c:var(--warning)}.s-problem{--c:var(--problem)}.s-unknown{--c:var(--unknown)}" +
        "details{background:var(--card);border:1px solid var(--line);border-radius:10px;margin:8px 0;padding:8px 14px}summary{cursor:pointer;font-weight:600;padding:4px 0}" +
        ".dot{display:inline-block;width:10px;height:10px;border-radius:50%;background:var(--c);margin-right:8px}" +
        ".finding{border-left:4px solid var(--c);padding:6px 12px;margin:10px 0}.ft{display:flex;justify-content:space-between;gap:12px}.finding p{margin:4px 0}.adv{font-style:italic}" +
        ".tag{color:var(--c);font-weight:600;font-size:13px;white-space:nowrap}" +
        "table{width:100%;border-collapse:collapse;background:var(--card);border-radius:10px;overflow:hidden}th,td{text-align:left;vertical-align:top;padding:8px 10px;border-bottom:1px solid var(--line)}" +
        "code{font-family:Consolas,monospace;font-size:13px;overflow-wrap:anywhere}.ok{color:var(--ok)}.warn,.err{color:var(--warning)}footer{margin-top:40px;font-size:13px}" +
        "@media print{details{break-inside:avoid}details:not([open])>*:not(summary){display:block}}";
}
