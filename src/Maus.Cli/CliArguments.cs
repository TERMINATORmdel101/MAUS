using Maus.Core.Localization;
using static Maus.Core.Localization.Texts;

namespace Maus.Cli;

/// <summary>
/// Les options de la ligne de commande, contrôlées avant tout audit : une option inconnue, mal écrite, sans sa valeur, en
/// trop ou qui ne va pas avec la commande est refusée, au lieu d'être ignorée et d'élargir l'action demandée.
/// </summary>
internal sealed class CliArguments
{
    private enum ValueKind
    {
        None,
        One,
        OneOrMore,
    }

    /// <param name="Name">Nom de l'option.</param>
    /// <param name="Kind">Sans valeur, avec une valeur, ou avec une liste de valeurs.</param>
    /// <param name="Commands">Commandes avec lesquelles elle a un sens (<see cref="AuditCommand"/> : l'audit simple) ; <c>null</c> : toutes.</param>
    /// <param name="Repeatable">Peut être écrite plusieurs fois.</param>
    /// <param name="Missing">Message propre à l'option quand sa valeur manque ; <c>null</c> : le message commun.</param>
    private sealed record Option(string Name, ValueKind Kind = ValueKind.None, string[]? Commands = null, bool Repeatable = false, Func<string>? Missing = null);

    /// <summary>Audit simple : aucune commande écrite.</summary>
    private const string AuditCommand = "";

    private static readonly string[] ApplyCommands = ["--apply", "--apply-recommended"];

    /// <summary>Une seule à la fois : chacune fait une chose différente.</summary>
    private static readonly Option[] Commands =
    [
        new("--plan"),
        new("--apply", ValueKind.OneOrMore, Missing: () => T("Indiquez au moins une correction : maus --apply ID [ID...] (voir maus --plan).")),
        new("--apply-recommended"),
        new("--summary"),
        new("--journal"),
        new("--revert", ValueKind.One, Missing: () => T("Indiquez la séance à annuler : maus --revert SEANCE (voir maus --journal).")),
        new("--restart-explorer"),
        new("--prefs"),
        new("--set", ValueKind.One, Missing: () => T("Syntaxe : maus --set gamebar=1|2|3|auto  ou  maus --set alimentation=performance|partout|autonomie|auto")),
        new("--ack", ValueKind.One, Missing: () => T("Indiquez le constat : maus --ack ID (voir le rapport d'audit).")),
        new("--unack", ValueKind.One, Missing: () => T("Indiquez le constat : maus --unack ID (voir maus --prefs).")),
    ];

    private static readonly Option[] Modifiers =
    [
        new("--lang", ValueKind.One),
        new("--json", Commands: [AuditCommand]),
        new("--html", ValueKind.One, [AuditCommand, .. ApplyCommands]),
        new("--module", ValueKind.One, [AuditCommand, "--summary", "--plan", "--ack", .. ApplyCommands], Repeatable: true, Missing: () => T("Indiquez le module : maus --module M01 (voir maus --help).")),
        new("--yes", Commands: ["--revert", "--restart-explorer", "--ack", .. ApplyCommands]),
        new("--without-restore-point", Commands: ApplyCommands),
        new("--enable-protection", Commands: ApplyCommands),
        // Sans identifiant, FixEngine.Revert annulerait toute la séance : jamais par une option mal écrite.
        new("--change", ValueKind.One, ["--revert"], Missing: () => T("Indiquez la correction à annuler : maus --revert SEANCE --change CORRECTION.")),
        new("--force", Commands: ["--revert"]),
    ];

    /// <summary>Elles répondent seules : le reste de la ligne n'est pas contrôlé.</summary>
    private static readonly string[] Informative = ["--version", "--help", "-h", "/?"];

    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Le refus éventuel : le texte n'est écrit qu'à la lecture, une fois la langue choisie (<c>--lang</c> vient de ces mêmes arguments).</summary>
    private Func<string>? _error;

    private CliArguments()
    {
    }

    /// <summary>Message de refus dans la langue active, ou <c>null</c> si la ligne est correcte.</summary>
    public string? Error => _error?.Invoke();

    /// <summary>Langue demandée, si elle existe (« EN » et « en-US » valent « en ») ; une langue inconnue est un refus (<see cref="Error"/>).</summary>
    public string? Language
    {
        get
        {
            var asked = Value("--lang")?.Split('-', '_')[0];
            return Texts.Languages.Select(l => l.Code).FirstOrDefault(code => code.Equals(asked, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Value(string name) => _values.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    public IReadOnlyList<string> Values(string name) => _values.TryGetValue(name, out var values) ? values : [];

    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        var parsed = new CliArguments();
        for (var index = 0; index < args.Count; index++)
        {
            var name = args[index];
            if (Informative.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                parsed._values.TryAdd(name, []);
                continue;
            }

            if (Find(name) is not { } option)
            {
                parsed.Refuse(name.StartsWith('-') ? () => T("Option inconnue : {0} (voir maus --help).", name) : () => T("Argument en trop : {0} (voir maus --help).", name));
                continue;
            }

            if (parsed._values.TryGetValue(name, out var values) && !option.Repeatable)
            {
                parsed.Refuse(() => T("Option écrite deux fois : {0}.", name));
                continue;
            }

            if (values is null)
            {
                values = [];
                parsed._values.Add(name, values);
            }

            if (option.Kind == ValueKind.None)
            {
                continue;
            }

            var read = 0;
            while (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal) && (read == 0 || option.Kind == ValueKind.OneOrMore))
            {
                values.Add(args[++index]);
                read++;
            }

            if (read == 0)
            {
                parsed.Refuse(option.Missing ?? (() => T("Valeur manquante après {0} (voir maus --help).", name)));
            }
        }

        parsed.CheckCombination();
        return parsed;
    }

    private static Option? Find(string name) =>
        Commands.Concat(Modifiers).FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Garde la première erreur de la ligne : c'est elle qu'il faut corriger d'abord.</summary>
    private void Refuse(Func<string> error) => _error ??= error;

    private void CheckCombination()
    {
        var commands = Commands.Where(c => Has(c.Name)).Select(c => c.Name).ToList();
        if (commands.Count > 1)
        {
            Refuse(() => T("Une seule commande à la fois : {0}.", string.Join(", ", commands)));
            return;
        }

        var command = commands.Count == 1 ? commands[0] : AuditCommand;
        foreach (var modifier in Modifiers.Where(m => Has(m.Name) && m.Commands?.Contains(command) == false))
        {
            Refuse(command == AuditCommand
                ? () => T("L'option {0} ne s'emploie pas avec un audit simple (voir maus --help).", modifier.Name)
                : () => T("L'option {0} ne s'emploie pas avec {1} (voir maus --help).", modifier.Name, command));
        }

        if (Has("--json") && Has("--html"))
        {
            Refuse(() => T("Choisissez un seul format de rapport : --json ou --html."));
        }

        if (Has("--without-restore-point") && Has("--enable-protection"))
        {
            Refuse(() => T("--without-restore-point et --enable-protection se contredisent : choisissez l'une des deux."));
        }

        if (Value("--lang") is { } language && Language is null)
        {
            Refuse(() => T("Langue inconnue : {0}. Langues disponibles : {1}.", language, string.Join(", ", Texts.Languages.Select(l => l.Code))));
        }
    }
}
