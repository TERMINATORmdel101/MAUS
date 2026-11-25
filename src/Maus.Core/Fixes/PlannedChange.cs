namespace Maus.Core.Fixes;

/// <summary>Ce qu'il faut faire après la correction pour qu'elle prenne effet.</summary>
public enum ChangeEffect
{
    /// <summary>Effet immédiat.</summary>
    Immediate,

    /// <summary>Effet complet après redémarrage de l'Explorateur (ou fermeture de session).</summary>
    ExplorerRestart,

    /// <summary>Effet à la prochaine ouverture de session.</summary>
    SignOut,

    /// <summary>Effet au prochain redémarrage du PC.</summary>
    Restart,
}

/// <summary>Une écriture élémentaire : la valeur visée, ou <c>null</c> pour supprimer la valeur.</summary>
public sealed record SettingWrite(SettingKey Key, SettingValue? Value);

/// <summary>
/// Correction proposée par un module (étape Plan) : rien n'est écrit tant que l'utilisateur ne l'a pas cochée.
/// Toutes ses écritures réussissent ensemble, ou sont annulées ensemble.
/// </summary>
public sealed record PlannedChange
{
    /// <summary>Identifiant stable, en général celui du constat corrigé (par exemple « M06.taskview »).</summary>
    public required string Id { get; init; }

    public required string ModuleId { get; init; }

    /// <summary>Constat corrigé, s'il diffère de <see cref="Id"/> (un constat peut donner plusieurs corrections, par exemple une par service).</summary>
    public string? FindingId { get; init; }

    /// <summary>Libellé court de l'action (« Masquer le bouton Vue des tâches »).</summary>
    public required string Title { get; init; }

    /// <summary>Ce que fait la correction, en français clair.</summary>
    public required string Description { get; init; }

    /// <summary>Ce que l'utilisateur y gagne.</summary>
    public string? Gain { get; init; }

    /// <summary>Ce qu'il peut perdre ; <c>null</c> si aucun risque connu.</summary>
    public string? Risk { get; init; }

    /// <summary>Avertissement renforcé affiché avant de cocher la case (anti-triche, instantanés supprimés…).</summary>
    public string? Warning { get; init; }

    public ChangeEffect Effect { get; init; } = ChangeEffect.Immediate;

    /// <summary>Case pré-cochée dans l'interface (valeur recommandée). L'utilisateur garde toujours le dernier mot.</summary>
    public bool Recommended { get; init; } = true;

    /// <summary>Réglage « Avancé » : jamais inclus dans un profil standard, demande une case dédiée.</summary>
    public bool Advanced { get; init; }

    public string? Category { get; init; }

    public required IReadOnlyList<SettingWrite> Writes { get; init; }

    /// <summary>
    /// Question à poser dans la fenêtre du moteur avant d'écrire, pour ce qu'une correction ordinaire ne touche pas (logiciel de
    /// sécurité lancé au démarrage) ; <c>null</c> pour une correction ordinaire.
    /// </summary>
    public string? EngineQuestion { get; init; }

    /// <summary>Au moins une écriture vise le profil de l'utilisateur de la session.</summary>
    public bool IsUserScoped => Writes.Any(w => w.Key.IsUserScoped);

    /// <summary>
    /// La correction pose une stratégie de Windows (valeur écrite sous <c>…\Policies\…</c> ; retirer une stratégie ne compte pas) :
    /// Paramètres peut alors dire que le réglage est géré par une organisation et ne plus laisser changer l'interrupteur.
    /// </summary>
    public bool SetsPolicy => Writes.Any(w => w.Value is not null && w.Key.Kind == SettingKind.Registry
        && (w.Key.Path ?? string.Empty).Contains(@"\Policies\", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Ce qu'une stratégie posée change dans Paramètres. Windows affiche « Certains de ces paramètres sont gérés par votre
    /// organisation » dès qu'une stratégie est posée, PC personnel compris (Microsoft Q&amp;A, « Windows 11 says "Some of these
    /// settings are managed by your organization" even though my PC isn't connected to an organization », questions 6004558 et
    /// 4288372) ; une stratégie fixe le réglage, que l'interrupteur ne change plus (exemple documenté : Policy CSP
    /// System/AllowTelemetry, Microsoft Learn).
    /// </summary>
    public static string PolicyRisk => Localization.Texts.T("Stratégie de Windows : Paramètres peut afficher « Certains de ces paramètres sont gérés par votre organisation », même sur un PC personnel, et ne plus laisser changer ce réglage. C'est l'effet normal d'une stratégie ; pour revenir en arrière, utilisez « Annuler » dans l'Historique de MAUS.");

    /// <summary>Rappel de <see cref="PolicyRisk"/> dans la question posée avant d'appliquer, si une correction cochée pose une stratégie.</summary>
    public static string PolicyNotice => Localization.Texts.T("Certaines de ces corrections posent une stratégie de Windows : Paramètres peut ensuite afficher « Certains de ces paramètres sont gérés par votre organisation », même sur un PC personnel, et ne plus laisser changer ces réglages. Pour revenir en arrière, utilisez « Annuler » dans l'Historique de MAUS.");

    /// <summary>Le risque d'une correction, complété par <see cref="PolicyRisk"/> si elle pose une stratégie.</summary>
    public static string? WithPolicyRisk(string? risk, bool setsPolicy) =>
        !setsPolicy ? risk : risk is null ? PolicyRisk : risk + " " + PolicyRisk;
}
