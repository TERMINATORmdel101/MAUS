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

    /// <summary>Résultats des tests : le rapport devient le « contrôle technique » du PC.</summary>
    public IReadOnlyList<string> TestLines { get; init; } = [];

    /// <summary>
    /// Masque nom d'utilisateur, dossier du profil, nom du PC et e-mails dans tous les textes du rapport (exclusions Defender,
    /// chemins…). L'application et <c>maus --html</c> passent <see cref="PrivacyFilter.ForCurrentUser"/> ; sans filtre, rien n'est masqué.
    /// </summary>
    public PrivacyFilter? Privacy { get; init; }
}

/// <summary>Une ligne d'une page simple : ce qui a été lu et sa valeur.</summary>
public sealed record HtmlPageLine(string Label, string Value);

/// <summary>Une partie d'une page simple.</summary>
public sealed record HtmlPageSection(string Title, IReadOnlyList<HtmlPageLine> Lines, string? Note = null);

/// <summary>Une page simple, sans audit : titre, mention visible en tête, parties, pied de page. Le masquage y est obligatoire.</summary>
public sealed record HtmlPageInput(string Title, string Notice, IReadOnlyList<string> Header, IReadOnlyList<HtmlPageSection> Sections, string Footer, PrivacyFilter Privacy);

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

    /// <summary>Filtre de la construction en cours (le rapport se construit d'un seul tenant, sur un seul fil).</summary>
    [ThreadStatic]
    private static PrivacyFilter? t_privacy;

    public static string Build(HtmlReportInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        t_privacy = input.Privacy;
        try
        {
            return BuildCore(input);
        }
        finally
        {
            t_privacy = null;
        }
    }

    private static string BuildCore(HtmlReportInput input)
    {
        var after = input.After;
        var html = new StringBuilder();
        var title = input.TestLines.Count > 0 ? T("Contrôle technique du PC") : input.Before is null ? T("Rapport d'audit MAUS") : T("Rapport avant/après MAUS");
        html.Append(CultureInfo.InvariantCulture, $"<!DOCTYPE html><html lang=\"{Language}\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append(CultureInfo.InvariantCulture, $"<title>{E(title)}</title><style>{StyleSheet}</style></head><body><main>");

        html.Append("<header class=\"hero\">");
        if (Logo.Value is { } logo)
        {
            // Logo embarqué dans la page (base64) : le rapport reste un seul fichier, lisible hors connexion.
            html.Append(CultureInfo.InvariantCulture, $"<img class=\"logo\" alt=\"MAUS\" src=\"data:image/jpeg;base64,{logo}\">");
        }

        html.Append(CultureInfo.InvariantCulture, $"<div class=\"who\"><h1>{E(title)}</h1><p class=\"sub\">{E(after.CreatedAt.ToLocalTime().ToString("f", Culture))}</p>")
            .Append(CultureInfo.InvariantCulture, $"<p>{E(T("{0} {1} (build {2}), édition {3}", after.Windows.ProductName, after.Windows.DisplayVersion, after.Windows.FullBuild, after.Windows.EditionId))}</p>")
            .Append(CultureInfo.InvariantCulture, $"<p>{E($"{Labels.Of(after.Hardware.FormFactor)} · {after.Hardware.Cpu.DisplayName}")}")
            .Append(after.Hardware.Gpus.Count > 0 ? E(" · " + string.Join(", ", after.Hardware.Gpus.Select(g => g.Name))) : string.Empty)
            .Append("</p></div></header>");

        var breakdown = HealthScore.Explain(after.Modules);
        var score = breakdown.Score;
        html.Append(CultureInfo.InvariantCulture, $"<section><h2>{E(T("En résumé"))}</h2><div class=\"overview\">")
            .Append(CultureInfo.InvariantCulture, $"<div class=\"ring s-{Css(ScoreTone(score))}\" style=\"--p:{score}\"><span><b>{score}</b>/100</span></div><div class=\"verdict\">")
            .Append(CultureInfo.InvariantCulture, $"<p class=\"score\">{E(T("santé du PC : {0}", HealthScore.Describe(score)))}</p>");
        if (HealthScore.PartialNote(breakdown) is { } partial)
        {
            html.Append(CultureInfo.InvariantCulture, $"<p class=\"err\">{E(partial)}</p>");
        }

        if (!after.IsElevated)
        {
            html.Append(CultureInfo.InvariantCulture, $"<p class=\"err\">{E(HealthScore.LimitedNote(after.Modules))}</p>");
        }

        AppendScoreDetail(html, breakdown);
        html.Append("</div></div><div class=\"cards\">");
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

        if (input.TestLines.Count > 0)
        {
            html.Append(CultureInfo.InvariantCulture, $"<section><h2>{E(T("Résultats des tests"))}</h2><ul>");
            foreach (var line in input.TestLines)
            {
                html.Append(CultureInfo.InvariantCulture, $"<li>{E(line)}</li>");
            }

            html.Append("</ul></section>");
        }

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
            html.Append(CultureInfo.InvariantCulture, $"<details{(module.WorstStatus.Rank() >= FindingStatus.Improvable.Rank() ? " open" : string.Empty)}><summary><span class=\"dot s-{Css(module.Verdict)}\"></span><span class=\"mt\">{E($"{module.ModuleId} · {module.Title}")}</span>");
            AppendCounts(html, module);
            html.Append("</summary>");
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

                // Encadré (à faire avant le BIOS) et note : comme à l'écran. ScreenOnlyNote n'est jamais écrite ici.
                if (finding.Callout is { Length: > 0 } callout)
                {
                    html.Append(CultureInfo.InvariantCulture, $"<p class=\"adv\">{E(callout).Replace("\n", "<br>", StringComparison.Ordinal)}</p>");
                }

                if (finding.SideNote is { Length: > 0 } sideNote)
                {
                    html.Append(CultureInfo.InvariantCulture, $"<p>{E(sideNote)}</p>");
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

    /// <summary>
    /// Page autonome à lignes « libellé, valeur », avec le même style, le même encodage et le même masquage que le rapport d'audit.
    /// </summary>
    public static string BuildPage(HtmlPageInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        t_privacy = input.Privacy;
        try
        {
            var html = new StringBuilder();
            html.Append(CultureInfo.InvariantCulture, $"<!DOCTYPE html><html lang=\"{Language}\"><head><meta charset=\"utf-8\">")
                .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
                .Append(CultureInfo.InvariantCulture, $"<title>{E(input.Title)}</title><style>{StyleSheet}</style></head><body><main><header class=\"hero\">");
            if (Logo.Value is { } logo)
            {
                html.Append(CultureInfo.InvariantCulture, $"<img class=\"logo\" alt=\"MAUS\" src=\"data:image/jpeg;base64,{logo}\">");
            }

            html.Append(CultureInfo.InvariantCulture, $"<div class=\"who\"><h1>{E(input.Title)}</h1><p class=\"sub\">{E(input.Notice)}</p>");
            foreach (var line in input.Header)
            {
                html.Append(CultureInfo.InvariantCulture, $"<p>{E(line)}</p>");
            }

            html.Append("</div></header>");
            foreach (var section in input.Sections)
            {
                html.Append(CultureInfo.InvariantCulture, $"<section><h2>{E(section.Title)}</h2>");
                if (section.Note is not null)
                {
                    html.Append(CultureInfo.InvariantCulture, $"<p class=\"val\">{E(section.Note)}</p>");
                }

                html.Append("<table><tbody>");
                foreach (var line in section.Lines)
                {
                    html.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(line.Label)}</td><td>{E(line.Value)}</td></tr>");
                }

                html.Append("</tbody></table></section>");
            }

            return html.Append(CultureInfo.InvariantCulture, $"<footer>{E(input.Footer)}</footer></main></body></html>").ToString();
        }
        finally
        {
            t_privacy = null;
        }
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
            // La moins bonne des écritures de cette correction ; la vérification ne vaut que tant que tout est encore en place.
            var first = change.FirstOrDefault(e => e.State != EntryState.Applied) ?? change.First();
            var result = first.State == EntryState.Applied && verified.TryGetValue(change.Key, out var v)
                ? v.Message
                : first.State switch
                {
                    EntryState.Applied => T("Appliqué"),
                    EntryState.Reverted => T("Annulé depuis"),
                    EntryState.Failed => T("Échec : valeur d'origine remise"),
                    EntryState.RevertSkipped => T("Modifiée depuis, laissée telle quelle"),
                    EntryState.Pending => T("Interrompue : peut-être appliquée"),
                    EntryState.RollbackFailed => T("Retour arrière raté : peut-être encore en place"),
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
        // La même comparaison que l'écran de MAUS : mêmes audits, même réponse, constats apparus ou disparus compris.
        var changed = AuditComparison.Compare(
            AuditSnapshot.From(before.Modules, string.Empty, before.CreatedAt) with { MausVersion = before.MausVersion },
            AuditSnapshot.From(after.Modules, string.Empty, after.CreatedAt) with { MausVersion = after.MausVersion });
        html.Append(CultureInfo.InvariantCulture, $"<section><h2>{E(T("Ce qui a changé"))}</h2>");
        if (changed.Count == 0)
        {
            html.Append(CultureInfo.InvariantCulture, $"<p>{E(T("Aucun verdict n'a changé entre les deux audits."))}</p></section>");
            return;
        }

        html.Append(CultureInfo.InvariantCulture, $"<table><thead><tr><th>{E(T("Constat"))}</th><th>{E(T("Avant"))}</th><th>{E(T("Après"))}</th></tr></thead><tbody>");
        foreach (var change in changed)
        {
            html.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(change.Title)}{(change.Kind == AuditChangeKind.NotComparable ? " " + E(T("(pas forcément un changement du PC : MAUS a changé de version)")) : string.Empty)}</td>{ChangeCell(change.Before, change.CurrentBefore)}{ChangeCell(change.After, change.CurrentAfter)}</tr>");
        }

        html.Append("</tbody></table></section>");
    }

    /// <summary>État et valeur lue d'un constat dans l'un des deux audits ; sans état, il n'y figurait pas.</summary>
    private static string ChangeCell(FindingStatus? status, string? current) => status is { } known
        ? $"<td><span class=\"tag s-{Css(known)}\">{E(Labels.Of(known))}</span> {E(current ?? string.Empty)}</td>"
        : $"<td>{E(T("Absent de cet audit"))}</td>";

    private static int Count(AuditReport report, FindingStatus status) => report.Modules.Sum(m => m.Count(status));

    /// <summary>Couleur de l'anneau du score, aux mêmes paliers que <see cref="HealthScore.Describe"/>.</summary>
    private static FindingStatus ScoreTone(int score) => score switch
    {
        >= 75 => FindingStatus.Ok,
        >= 50 => FindingStatus.Warning,
        _ => FindingStatus.Problem,
    };

    /// <summary>Nombre de constats par état sur la ligne d'un module : ce qui demande de l'attention, sinon ce qui va bien.</summary>
    private static void AppendCounts(StringBuilder html, ModuleResult module)
    {
        var present = Order.Take(4).Append(FindingStatus.Ok).Where(s => module.Count(s) > 0).ToList();
        html.Append("<span class=\"counts\">");
        foreach (var status in present.Count > 1 ? present.Where(s => s != FindingStatus.Ok) : present)
        {
            html.Append(CultureInfo.InvariantCulture, $"<span class=\"tag s-{Css(status)}\" title=\"{E(Labels.Of(status))}\">{module.Count(status)}</span>");
        }

        html.Append("</span>");
    }

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
    /// <remarks>Le masquage se fait sur le texte brut, avant l'échappement : il ne peut jamais toucher une balise.</remarks>
    private static string E(string text) => (t_privacy is { } privacy ? privacy.Mask(text) : text)
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);

    private static string Css(FindingStatus status) => status.ToString().ToLowerInvariant();

    private const string LightColors =
        "--bg:#f4f6fb;--fg:#1b1d24;--muted:#5d6475;--card:#fff;--line:#e2e6ef;--accent:#2563c9;--ok:#1f8a4c;--info:#56607a;--improvable:#2563c9;--warning:#c46a00;--problem:#c42b1c;--unknown:#8a8f9c;";

    private const string StyleSheet =
        ":root{" + LightColors + "}" +
        "@media(prefers-color-scheme:dark){:root{--bg:#14161c;--fg:#e8eaf0;--muted:#a3a9b8;--card:#1d2029;--line:#2c313d;--accent:#5b9bff;--ok:#3fbf73;--info:#9aa3ba;--improvable:#5b9bff;--warning:#f0a030;--problem:#ff6b5b;--unknown:#8a8f9c}}" +
        "*{box-sizing:border-box}html{-webkit-print-color-adjust:exact;print-color-adjust:exact}" +
        "body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.55 'Segoe UI Variable','Segoe UI',system-ui,sans-serif}" +
        "main{max-width:980px;margin:0 auto;padding:24px 16px 48px}h1{font-size:30px;line-height:1.2;margin:0}h3{font-size:16px;margin:20px 0 8px}section{margin-top:8px}" +
        "h2{display:flex;align-items:center;gap:10px;font-size:20px;margin:36px 0 14px}h2::before{content:'';flex:none;width:4px;height:20px;border-radius:2px;background:var(--accent)}" +
        ".hero{display:flex;flex-wrap:wrap;align-items:center;gap:18px 24px;padding:24px;border-radius:18px;color:#fff;background:linear-gradient(135deg,#131a2e,#22366b 60%,#2563c9);box-shadow:0 10px 30px rgba(15,25,55,.28)}" +
        ".logo{display:block;width:190px;max-width:100%;height:auto;border-radius:12px;box-shadow:0 4px 18px rgba(0,0,0,.35)}.who{flex:1 1 320px;min-width:0}.who p{margin:4px 0;color:#d6ddf0;overflow-wrap:anywhere}.who .sub{color:#fff;font-weight:600}" +
        ".val,.was{color:var(--muted)}.val{font-size:14px}" +
        ".overview{display:flex;flex-wrap:wrap;align-items:center;gap:16px 24px;background:var(--card);border:1px solid var(--line);border-radius:14px;padding:18px 20px;margin-bottom:12px}" +
        ".ring{flex:none;display:grid;place-items:center;width:116px;height:116px;border-radius:50%;background:conic-gradient(var(--c) calc(var(--p)*1%),var(--line) 0)}" +
        ".ring span{display:grid;place-content:center;width:92px;height:92px;border-radius:50%;background:var(--card);color:var(--muted);font-size:13px;line-height:1.1;text-align:center}.ring b{color:var(--fg);font-size:32px}" +
        ".verdict{flex:1 1 300px;min-width:0}.score{font-size:19px;font-weight:600;margin:0 0 6px}.score::first-letter{text-transform:uppercase}" +
        ".cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px}" +
        ".card{background:var(--card);background:color-mix(in srgb,var(--c) 7%,var(--card));border:1px solid var(--line);border-top:3px solid var(--c);border-radius:12px;padding:14px}.card .n{font-size:34px;line-height:1.15;font-weight:650;color:var(--c)}" +
        ".s-ok{--c:var(--ok)}.s-info{--c:var(--info)}.s-improvable{--c:var(--improvable)}.s-warning{--c:var(--warning)}.s-problem{--c:var(--problem)}.s-unknown{--c:var(--unknown)}" +
        "details{background:var(--card);border:1px solid var(--line);border-radius:12px;margin:10px 0;padding:10px 16px}" +
        "summary{display:flex;flex-wrap:wrap;align-items:center;gap:8px;cursor:pointer;font-weight:600;list-style:none}summary::-webkit-details-marker{display:none}" +
        "summary::after{content:'›';color:var(--muted);font-size:20px;line-height:1;transition:transform .15s}details[open]>summary::after{transform:rotate(90deg)}" +
        "details.why{background:none;border:0;margin:4px 0 0;padding:0}details.why summary{color:var(--accent);font-weight:500}details.why li{margin:2px 0}" +
        ".mt{flex:1 1 auto;min-width:0}.counts{display:flex;gap:6px}.dot{flex:none;width:10px;height:10px;border-radius:50%;background:var(--c)}" +
        ".tag{flex:none;color:var(--c);background:color-mix(in srgb,var(--c) 14%,transparent);border-radius:999px;padding:1px 10px;font-weight:600;font-size:12.5px;white-space:nowrap}" +
        ".finding{border-left:3px solid var(--c);padding:6px 0 6px 14px;margin:12px 0}.ft{display:flex;justify-content:space-between;align-items:flex-start;gap:12px}.finding p{margin:4px 0}" +
        ".adv{background:color-mix(in srgb,var(--accent) 9%,transparent);border-radius:8px;padding:6px 10px}" +
        "table{width:100%;border-collapse:separate;border-spacing:0;background:var(--card);border:1px solid var(--line);border-radius:12px;overflow:hidden}th,td{text-align:left;vertical-align:top;padding:9px 12px;border-bottom:1px solid var(--line)}tr:last-child td{border-bottom:0}th{color:var(--muted);font-size:13px;font-weight:600}" +
        "code{font-family:Consolas,monospace;font-size:13px;overflow-wrap:anywhere}.ok{color:var(--ok)}.warn,.err{color:var(--warning)}footer{margin-top:40px;padding-top:16px;border-top:1px solid var(--line);color:var(--muted);font-size:13px}" +
        "@media(prefers-reduced-motion:no-preference){header,section{animation:rise .35s ease-out both}}@keyframes rise{from{opacity:0;transform:translateY(8px)}}" +
        "@media print{:root{" + LightColors + "--bg:#fff}header,section{animation:none}.hero{box-shadow:none}details,.finding{break-inside:avoid}details::details-content{content-visibility:visible;display:block}summary::after{content:none}details:not([open])>*:not(summary){display:block}details:not([open])::details-content{content-visibility:visible;display:block}}";
}
