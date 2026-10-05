using static Maus.Core.Localization.Texts;

namespace Maus.Core.Reporting;

/// <summary>Une idée reçue sur l'optimisation de Windows, ce qu'il en est vraiment, et la source qui le dit.</summary>
public sealed record OptimizationMyth(string Claim, string Truth, Uri Source);

/// <summary>
/// Idées reçues que MAUS ne suit pas, et pourquoi (principe 6 : honnêteté sur les gains). Chaque entrée s'appuie sur une
/// page de Microsoft (principe 7 : pas de source, pas d'affirmation). Propriété calculée : textes dans la langue active.
/// </summary>
public static class OptimizationMyths
{
    public static IReadOnlyList<OptimizationMyth> All =>
    [
        new(
            T("« Un nettoyeur de registre accélère le PC. »"),
            T("Microsoft ne prend pas en charge ces outils : une modification incorrecte du registre peut rendre Windows instable, au point de devoir le réinstaller."),
            new Uri("https://support.microsoft.com/en-us/topic/microsoft-support-policy-for-the-use-of-registry-cleaning-utilities-0485f4df-9520-3691-2461-7b0fd54e8b3a")),
        new(
            T("« Il faut supprimer le fichier d'échange quand on a beaucoup de mémoire. »"),
            T("Sans fichier d'échange, Windows ne peut pas enregistrer le vidage mémoire d'un écran bleu, et la mémoire totale utilisable par les programmes (limite de validation) diminue."),
            new Uri("https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/how-to-determine-the-appropriate-page-file-size-for-64-bit-versions-of-windows")),
        new(
            T("« Régler le nombre de processeurs dans msconfig active tous les cœurs. »"),
            T("Cette option (numproc) fait l'inverse : selon Microsoft, Windows n'utilise alors que le nombre de processeurs indiqué. Elle peut en retirer, jamais en ajouter."),
            new Uri("https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/bcdedit--set")),
        new(
            T("« Forcer l'horloge HPET ou couper le « dynamic tick » donne plus d'images par seconde. »"),
            T("Microsoft indique que ces réglages de démarrage (useplatformclock, disabledynamictick) ne doivent servir qu'au débogage."),
            new Uri("https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/bcdedit--set")),
        new(
            T("« Il faut défragmenter un SSD. »"),
            T("Pour un SSD, l'outil « Optimiser les lecteurs » de Windows envoie la commande TRIM au lieu de défragmenter, et il le fait tout seul pendant la maintenance automatique de Windows."),
            new Uri("https://learn.microsoft.com/en-us/powershell/module/storage/optimize-volume")),
    ];
}
