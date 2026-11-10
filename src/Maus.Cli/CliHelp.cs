using Maus.Core;
using static Maus.Core.Localization.Texts;

namespace Maus.Cli;

/// <summary>Le texte de <c>maus --help</c>.</summary>
internal static class CliHelp
{
    public static void Write(IEnumerable<IAuditModule> modules, TextWriter output)
    {
        output.WriteLine(T("Usage : maus [--json | --html FICHIER] [--module Mxx]..."));
        output.WriteLine(T("Audit en lecture seule : MAUS ne modifie rien sur ce PC."));
        output.WriteLine(T("  sans option                             rapport à l'écran, avec les vrais noms et chemins : ne le collez pas tel quel"));
        output.WriteLine(T("  --json                                  rapport en JSON sur la sortie standard (noms, chemins et SID masqués)"));
        output.WriteLine(T("  --html FICHIER                          rapport HTML enregistré dans ce fichier (noms, chemins et SID masqués)"));
        output.WriteLine(T("  --module Mxx                            limite l'audit à ce module ; se répète (liste ci-dessous)"));
        output.WriteLine(T("  --lang fr|en|es                         langue des textes (par défaut : celle de l'application, sinon de Windows)"));
        output.WriteLine(T("  --summary                               résumé à coller sur un forum pour demander de l'aide (sans données personnelles)"));
        output.WriteLine(T("  --version                               version de MAUS"));
        output.WriteLine();
        output.WriteLine(T("Corrections (droits administrateur requis pour appliquer ou annuler) :"));
        output.WriteLine(T("  maus --plan [--module Mxx]              corrections proposées, sans rien modifier"));
        output.WriteLine(T("  maus --apply ID [ID...]                 applique les corrections choisies, après confirmation"));
        output.WriteLine(T("  maus --apply-recommended [--module Mxx] applique les corrections recommandées"));
        output.WriteLine(T("       avec --apply ou --apply-recommended : [--without-restore-point | --enable-protection] [--yes] [--html FICHIER]"));
        output.WriteLine(T("  maus --journal                          séances de corrections enregistrées"));
        output.WriteLine(T("  maus --revert SEANCE [--change ID] [--force] [--yes]"));
        output.WriteLine(T("                                          remet les valeurs d'origine (séance entière ou une correction)"));
        output.WriteLine(T("  maus --restart-explorer [--yes]         redémarre l'Explorateur"));
        output.WriteLine();
        output.WriteLine(T("Vos choix (droits administrateur) :"));
        output.WriteLine(T("  maus --ack ID [--yes] | --unack ID      marque un constat « voulu », après confirmation (ou retire la marque)"));
        output.WriteLine(T("  maus --set gamebar=1|2|3|auto           profil Game Bar"));
        output.WriteLine(T("  maus --set alimentation=performance|partout|autonomie|auto"));
        output.WriteLine(T("       performance : sur secteur, Équilibré sur batterie ; partout : aussi sur batterie ; autonomie : la batterie d'abord"));
        output.WriteLine(T("  maus --prefs                            affiche vos choix"));
        output.WriteLine();
        output.WriteLine(T("Modules :"));
        foreach (var module in modules)
        {
            output.WriteLine($"  {module.Id}  {module.Title}");
        }

        output.WriteLine();
        output.WriteLine(T("Codes de sortie, pour un script :"));
        output.WriteLine(T("  0    terminé, aucun problème rouge"));
        output.WriteLine(T("  1    ligne de commande refusée, réponse « non » à une question, sélection vide ou erreur inattendue"));
        output.WriteLine(T("  2    audit ou résumé : au moins un problème rouge ; corrections : au moins une a échoué"));
        output.WriteLine(T("  3    droits administrateur manquants, action bloquée, ou rapport HTML ou choix non enregistré"));
        output.WriteLine(T("  4    audit ou résumé : un module n'a pas pu être vérifié, le résultat est partiel"));
        output.WriteLine(T("  130  audit arrêté par Ctrl+C"));
    }
}
